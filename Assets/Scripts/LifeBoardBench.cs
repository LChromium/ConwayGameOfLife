using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Stage-C large-board benchmark. Activated with "-lifeBench" together with
    /// "-lifeBoard WxH"; measures the board the Player was launched with, appends one JSON
    /// line to <c>stage-c-bench-r5.jsonl</c> and quits. One process per board size, so each
    /// size gets a fresh allocator and a fresh peak, which is the only way a memory figure
    /// for a size means anything.
    ///
    /// <para><b>A run may measure a subset of the axes.</b> "-lifeBenchScenarios frame" runs
    /// only the four cross-frame scenarios, for the case where one axis's semantics changed and
    /// re-publishing the others would be noise. A skipped axis is written as <c>null</c> and
    /// named in <c>phasesSkipped</c>; it is never written as a zero.</para>
    ///
    /// <para><b>Five axes, recorded separately and never summed into one number.</b>
    /// Stage A refused to attribute a frame to its parts without per-part data; this probe
    /// is that data, so blurring it back together would waste the exercise.</para>
    ///
    /// <list type="bullet">
    /// <item><b>generate</b> -- <see cref="LifeNoiseSeeding.Generate"/>, Stopwatch around the
    /// call. Pure managed compute, so the number IS the CPU generation cost.</item>
    /// <item><b>upload</b> -- <c>ILifeBackend.LoadBoard</c>. On the GPU that is the staging
    /// copy plus <c>ComputeBuffer.SetData</c>, which returns once the data is handed over:
    /// NOT a transfer-complete time. On the CPU it is the app's real load path.</item>
    /// <item><b>evolution</b> -- CPU steps; GPU steps submitted without synchronisation; and
    /// a batch of <see cref="BatchSteps"/> GPU steps followed by one full readback, divided by
    /// the batch size. That last one is an <b>amortised</b> figure whose readback share is not
    /// decomposed, and the batch size is the same at every board size so the trend across
    /// sizes compares like with like. <b>No CPU/GPU speed ratio is derived.</b></item>
    /// <item><b>display</b> -- the repaint dispatch, the cost of the CPU backend's board copy
    /// and upload, and four <b>separately labelled</b> frame scenarios, each with its own
    /// frame-timing samples and valid-sample counts: paused, paused with a forced repaint
    /// every frame, normal evolution, and normal evolution on the CPU backend. Each scenario
    /// records the rate the benchmark itself counted over its own wall clock <b>and</b> the
    /// rate the controller published, side by side -- and, when the CPU backend computes on a
    /// worker (stage D), what the WORKER paid (rule step, result read-out) separately from what
    /// the MAIN THREAD paid (board handover, display upload). A <c>pauseResponse</c> block
    /// records the pause command, whether a generation was in flight when it arrived, whether
    /// the display froze, and whether the finished generation was kept for the resume.</item>
    /// <item><b>memory</b> -- the capacity of the buffers this probe enumerates (from the
    /// source), the measured graphics-driver delta for one more board, and the difference
    /// between them marked <b>unattributed</b>. No internal split is claimed, because the
    /// measurement cannot see one.</item>
    /// </list>
    ///
    /// <para>Deliberately self-contained: it does not share its statistics helper with the
    /// archived stage-A probe, because editing that probe would invalidate stage A's
    /// published numbers.</para>
    /// </summary>
    public sealed class LifeBoardBench : MonoBehaviour
    {
        /// <summary>Round of the measurement contract that produced a record.</summary>
        private const int RecordRound = 5;

        /// <summary>The axes a full run measures. A run that names a subset records that.</summary>
        private static readonly string[] AllPhases =
        {
            "generate", "upload", "evolution", "display-fixed-costs", "frame-scenarios", "memory",
        };

        /// <summary>
        /// Board the stage-B reproduction command uses, so generation figures are comparable.
        /// </summary>
        private static readonly LifeNoiseParameters FbmParameters =
            new(LifeSeedingMode.Fbm, 20260915, 0.32f, 60f, 10f, 0.7f);

        private static readonly LifeNoiseParameters UniformParameters =
            new(LifeSeedingMode.Uniform, 20260915, 0.32f, 48f, 0f, 0f);

        private const int RandomSeed = 20260915;
        private const double Density = 0.30;

        /// <summary>
        /// Steps per batch for the amortised GPU figure. Uniform across board sizes on
        /// purpose: with a size-dependent batch the readback (one per batch) would weigh
        /// more heavily on the large boards and the across-size trend would partly be an
        /// artefact of the batch size.
        /// </summary>
        private const int BatchSteps = 5;

        private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>
        /// True when this player reported frame timings at all. Recorded per line, because a
        /// reader has to be able to tell which configuration a GPU frame time came from
        /// without trusting a filename.
        /// </summary>
        private bool frameTimingStatsReported;

        /// <summary>
        /// Wall-clock cost of one phase, logged as it finishes. A benchmark that only
        /// reports its measurements cannot say where its own time went, and a phase that
        /// suddenly takes minutes is a finding rather than a mystery.
        ///
        /// <para>Focus is logged with it on purpose. A standalone player stops running when
        /// its window loses focus (<c>Application.runInBackground</c> is false by default),
        /// and a phase that straddles such a pause reports a wall-clock time of minutes
        /// while every measurement inside it stays fast. That exact shape was observed, and
        /// it is recorded rather than silently averaged away.</para>
        /// </summary>
        private static void Phase(string name, ref double startedAt)
        {
            double now = Time.realtimeSinceStartup;
            Debug.Log($"[stage-c-bench] phase {name}: {now - startedAt:F3}s focused={Application.isFocused}");
            startedAt = now;
        }

        /// <summary>
        /// "-lifeBenchScenarios frame" measures only the four cross-frame scenarios. A record
        /// never claims more than it measured: the axes the run skipped are written as null and
        /// listed in <c>phasesSkipped</c>.
        ///
        /// <para>This exists because a change to one axis does not invalidate the others. Round 4
        /// changed the clock's rate and overload reporting, which only the frame scenarios
        /// observe; re-publishing the generation, upload and evolution figures would have added
        /// rows that differ from round 3 by run-to-run noise and nothing else.</para>
        /// </summary>
        private static bool FrameScenariosOnly()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-lifeBenchScenarios")
                    return string.Equals(args[i + 1], "frame", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // -- results carried to the writer -------------------------------------

        private sealed class Generation
        {
            public LifeBenchStatistics.Summary Stats;
            public double RealisedDensity;
            public int Alive;
            public string Parameters;
        }

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            double startedAt = Time.realtimeSinceStartup;
            double phaseStartedAt = startedAt;

            // A standalone player stops advancing when its window loses focus, and a
            // measurement run must not be at the mercy of whatever took focus. This was
            // observed: one phase reported 778 s of wall clock while every sample inside it
            // was under 100 ms.
            //
            // It is forced true and LEFT true until the process exits. Round 3 restored it
            // before Application.Quit(), which put the player back into exactly the state where
            // an unfocused player does not run its loop -- and then the quit was not processed:
            // round 4's release run wrote its record at 13.2 s and was still alive when the
            // external 180 s guard killed it. This probe always quits as its last act, so there
            // is nothing to restore the setting for.
            bool configuredRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            bool focusedAtStart = Application.isFocused;
            Debug.Log($"[stage-c-bench] runInBackground was {configuredRunInBackground}, forced true " +
                      "and left true until the process exits");

            for (int i = 0; i < 30; i++)
                yield return null;

            LifeTerminalController controller = FindAnyObjectByType<LifeTerminalController>();
            UIDocument document = controller != null ? controller.GetComponent<UIDocument>() : null;
            LifeGridElement grid = document?.rootVisualElement.Q("life-grid") as LifeGridElement;
            ILifeBackend active = grid?.Backend;

            if (active == null)
            {
                Debug.LogError("[stage-c-bench] no board backend; cannot benchmark");
                Application.Quit(1);
                yield break;
            }

            int width = active.Width;
            int height = active.Height;
            int cells = width * height;
            bool frameScenariosOnly = FrameScenariosOnly();
            gridRef = grid;
            controllerRef = controller;
            Debug.Log($"[stage-c-bench] {width}x{height} ({cells} cells), active backend {active.Name}" +
                      (frameScenariosOnly ? ", frame scenarios only" : ", all axes"));

            // The starting board for every measurement below is a plain fixed random one:
            // generation is measured separately, and the other axes must be comparable
            // across board sizes without a noise field in the picture.
            byte[] board = BuildFixedBoard(cells);

            // The cross-frame scenarios run THIS board on the ACTIVE backend. Round 1 loaded
            // it only into the temporary measurement backends, so the live board was still
            // the default specimen while the record claimed a random density-0.30 start.
            active.WrapEdges = true;
            active.LoadBoard(board);
            string boardIdentifiedAs =
                $"fixed random seed {RandomSeed} density {F(Density)}, loaded into the active backend " +
                "before the frame scenarios, wrap boundary, generation reset to 0";

            Generation fbm = null;
            Generation uniform = null;
            LifeBenchStatistics.Summary cpuLoad = default;
            LifeBenchStatistics.Summary gpuLoad = default;
            LifeBenchStatistics.Summary cpuRules = default;
            LifeBenchStatistics.Summary gpuSubmit = default;
            LifeBenchStatistics.Summary gpuBatch = default;
            DisplayFacts display = null;

            if (frameScenariosOnly)
            {
                Debug.Log("[stage-c-bench] skipped by -lifeBenchScenarios frame: generate, upload, " +
                          "evolution, display fixed costs");
            }
            else
            {
                fbm = MeasureGeneration(FbmParameters, width, height);
                Phase("generate-fbm", ref phaseStartedAt);
                yield return null;

                uniform = MeasureGeneration(UniformParameters, width, height);
                Phase("generate-uniform", ref phaseStartedAt);
                yield return null;

                cpuLoad = MeasureUploadCpu(board, width, height);
                Phase("upload-cpu", ref phaseStartedAt);
                yield return null;

                gpuLoad = MeasureUploadGpu(board, width, height);
                Phase("upload-gpu", ref phaseStartedAt);
                yield return null;

                cpuRules = MeasureCpuRules(board, width, height, cells);
                Phase("evolution-cpu", ref phaseStartedAt);
                yield return null;

                gpuSubmit = MeasureGpuRules(board, width, height, cells, synchronise: false);
                Phase("evolution-gpu-submit", ref phaseStartedAt);
                yield return null;

                gpuBatch = MeasureGpuRules(board, width, height, cells, synchronise: true);
                Phase("evolution-gpu-batch-readback", ref phaseStartedAt);
                yield return null;

                display = MeasureDisplay(grid, board, width, height, cells);
                Phase("display-fixed-costs", ref phaseStartedAt);
                yield return null;
            }

            // Frame scenarios. vsync off and uncapped, so the interval describes work rather
            // than pacing -- and is still not a pure compute cost.
            int configuredVSync = QualitySettings.vSyncCount;
            int configuredTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 5; i++)
                yield return null;

            bool wasRunning = SetRunning(controller, false);
            int requestedGenerationsPerSecond = SetSpeed(controller, 20);

            var scenarios = new List<ScenarioResult>();
            PauseResponseFacts pauseResponse = null;

            // 1. paused, nothing changes.
            active.LoadBoard(board);
            yield return MeasureScenario(scenarios, "paused", active, cells, requestedGenerationsPerSecond,
                Repaint.None, budgetSeconds: 0.4f, minGenerations: 0, captureFrameTimings: true);
            Phase("scenario-paused", ref phaseStartedAt);

            // 2. paused with a forced repaint every frame. A deliberate stress case, not
            //    what the application does.
            active.LoadBoard(board);
            yield return MeasureScenario(scenarios, "paused-forced-repaint-every-frame", active, cells,
                requestedGenerationsPerSecond, Repaint.EveryFrame, budgetSeconds: 0.4f,
                minGenerations: 0, captureFrameTimings: true);
            Phase("scenario-paused-forced-repaint", ref phaseStartedAt);

            // 3. normal evolution: the clock runs and the application repaints once per
            //    generation by itself. No extra forced repaint here -- round 1 added one and
            //    then described the result as the normal path.
            active.LoadBoard(board);
            SetRunning(controller, true);
            yield return null;
            yield return MeasureScenario(scenarios, "running", active, cells, requestedGenerationsPerSecond,
                Repaint.None, budgetSeconds: 2.0f, minGenerations: 5, captureFrameTimings: true);
            Phase("scenario-running", ref phaseStartedAt);
            SetRunning(controller, false);

            // 4. normal evolution on the CPU backend: the configuration where the display
            //    path has to copy and upload the whole board after every change.
            //
            //    This scenario now asks for the SLIDER MAXIMUM at every board size, including
            //    the sizes where one CPU generation costs 150-650 ms against a 50 ms clock
            //    interval. Round 2 had to ask for a reduced rate because the clock's catch-up
            //    was unbounded and the player hung (a 318 s crash and a 600 s timeout). With
            //    the overload cap in LifeTerminalController the request is safe to make, and
            //    the record shows what actually happened: the controller's own achieved rate,
            //    and whether it had to discard catch-up debt.
            SetSpeed(controller, requestedGenerationsPerSecond);
            Phase("cpu-backend-switch-enter", ref phaseStartedAt);
            if (SwitchBackend(controller, gpu: false))
            {
                Phase("cpu-backend-switch-done", ref phaseStartedAt);
                ILifeBackend cpu = grid.Backend;
                if (cpu != null && cpu.Width == width && cpu.Height == height)
                {
                    cpu.WrapEdges = true;
                    cpu.LoadBoard(board);
                    Phase("cpu-backend-board-loaded", ref phaseStartedAt);

                    SetRunning(controller, true);
                    yield return null;
                    Phase("cpu-backend-first-frame", ref phaseStartedAt);

                    yield return MeasureScenario(scenarios, "running-cpu-backend", cpu, cells,
                        requestedGenerationsPerSecond, Repaint.None, budgetSeconds: 2.0f,
                        minGenerations: 1, captureFrameTimings: true);
                    Phase("scenario-running-cpu-backend", ref phaseStartedAt);
                    SetRunning(controller, false);

                    // Pause response, on the same backend and board: the scenario above says what
                    // the interface paid while the worker computed, this says what pausing during a
                    // computation costs and what happens to the generation that was in flight.
                    pauseResponse = new PauseResponseFacts();
                    yield return MeasurePauseResponse(pauseResponse);
                    Phase("pause-response", ref phaseStartedAt);
                }

                SwitchBackend(controller, gpu: true);
                Phase("cpu-backend-switch-back", ref phaseStartedAt);
                active = grid.Backend;
                if (active != null)
                    active.LoadBoard(board);
            }
            else
            {
                Debug.LogWarning("[stage-c-bench] could not switch to the CPU backend; " +
                                 "the CPU-backend frame scenario was not measured");
            }

            QualitySettings.vSyncCount = configuredVSync;
            Application.targetFrameRate = configuredTargetFrameRate;
            SetRunning(controller, wasRunning);

            MemoryFacts memory = null;
            if (frameScenariosOnly)
            {
                Debug.Log("[stage-c-bench] skipped by -lifeBenchScenarios frame: memory");
            }
            else
            {
                memory = MeasureMemory(width, height, board);
                Phase("memory", ref phaseStartedAt);
                yield return null;
            }

            bool focusedAtEnd = Application.isFocused;

            WriteLine(frameScenariosOnly, width, height, cells, active?.Name ?? "unknown", boardIdentifiedAs,
                fbm, uniform, cpuLoad, gpuLoad, cpuRules, gpuSubmit, gpuBatch, display, scenarios,
                pauseResponse, memory, configuredVSync, configuredTargetFrameRate,
                focusedAtStart && focusedAtEnd, Time.realtimeSinceStartup - startedAt);

            // runInBackground is deliberately NOT restored here: an unfocused player that is not
            // running in the background does not process this quit. See the note where it is set.
            Application.Quit(0);
        }

        // -- 1. generate -------------------------------------------------------

        private static Generation MeasureGeneration(in LifeNoiseParameters parameters, int width, int height)
        {
            var destination = new byte[width * height];
            int repeats = RepeatsFor(destination.Length);
            var samples = new List<double>(repeats);

            for (int i = 0; i < repeats; i++)
            {
                var watch = Stopwatch.StartNew();
                LifeNoiseSeeding.Generate(parameters, width, height, destination);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            int alive = LifeNoiseSeeding.CountAlive(destination);
            return new Generation
            {
                Stats = LifeBenchStatistics.Summarise(samples),
                Alive = alive,
                RealisedDensity = (double)alive / destination.Length,
                Parameters = parameters.ToString(),
            };
        }

        // -- 2. upload ---------------------------------------------------------

        private static LifeBenchStatistics.Summary MeasureUploadCpu(byte[] board, int width, int height)
        {
            using var backend = new CpuLifeBackend(width, height);
            return MeasureUpload(backend, board, repeats: 5);
        }

        private static LifeBenchStatistics.Summary MeasureUploadGpu(byte[] board, int width, int height)
        {
            if (!GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend backend, out string error))
            {
                Debug.LogWarning($"[stage-c-bench] GPU upload not measured: {error}");
                return default;
            }

            using (backend)
            {
                return MeasureUpload(backend, board, repeats: 5);
            }
        }

        private static LifeBenchStatistics.Summary MeasureUpload(ILifeBackend backend, byte[] board, int repeats)
        {
            var samples = new List<double>(repeats);
            for (int i = 0; i < repeats; i++)
            {
                var watch = Stopwatch.StartNew();
                backend.LoadBoard(board);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            return LifeBenchStatistics.Summarise(samples);
        }

        // -- 3. evolution ------------------------------------------------------

        private static LifeBenchStatistics.Summary MeasureCpuRules(byte[] board, int width, int height, int cells)
        {
            using var backend = new CpuLifeBackend(width, height);
            backend.WrapEdges = true;
            backend.LoadBoard(board);

            for (int i = 0; i < WarmupFor(cells); i++)
                backend.Step();

            int repeats = RepeatsFor(cells);
            var samples = new List<double>(repeats);

            for (int repeat = 0; repeat < repeats; repeat++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < BatchSteps; i++)
                    backend.Step();
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds / BatchSteps);
            }

            return LifeBenchStatistics.Summarise(samples);
        }

        /// <summary>
        /// GPU rule advance. <paramref name="synchronise"/> false is submit-only. True is a
        /// batch of <see cref="BatchSteps"/> dispatches followed by ONE full readback, divided
        /// by the batch size: an amortised figure, not a per-generation execution time, and
        /// the readback's share of it is not measured.
        /// </summary>
        private static LifeBenchStatistics.Summary MeasureGpuRules(byte[] board, int width, int height, int cells, bool synchronise)
        {
            if (!GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend backend, out string error))
            {
                Debug.LogWarning($"[stage-c-bench] GPU evolution not measured: {error}");
                return default;
            }

            using (backend)
            {
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                var sink = new uint[cells];
                for (int i = 0; i < WarmupFor(cells); i++)
                    backend.Step();

                if (synchronise)
                    backend.TryReadAllCells(sink);

                int repeats = RepeatsFor(cells);
                var samples = new List<double>(repeats);

                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < BatchSteps; i++)
                        backend.Step();

                    if (synchronise)
                        backend.TryReadAllCells(sink);

                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds / BatchSteps);
                }

                return LifeBenchStatistics.Summarise(samples);
            }
        }

        // -- 4. display --------------------------------------------------------

        private sealed class DisplayFacts
        {
            public LifeBenchStatistics.Summary Refresh;
            public LifeBenchStatistics.Summary CpuBoardCopyAndUpload;
            public int ViewportWidth;
            public int ViewportHeight;
            public int CellPixels;
            public int VisibleCellsX;
            public int VisibleCellsY;
            public double VisibleFraction;
            public bool BoardFitsViewport;
            public string RendererPath;
        }

        private static DisplayFacts MeasureDisplay(
            LifeGridElement grid, byte[] board, int width, int height, int cells)
        {
            var facts = new DisplayFacts();

            // The repaint dispatch, exactly as the frame loop calls it.
            if (grid != null)
            {
                for (int i = 0; i < 5; i++)
                    grid.Refresh();

                var samples = new List<double>(5);
                for (int i = 0; i < 5; i++)
                {
                    var watch = Stopwatch.StartNew();
                    grid.Refresh();
                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }

                facts.Refresh = LifeBenchStatistics.Summarise(samples);
                facts.CellPixels = grid.CellPixels;
            }

            LifeBoardRenderer renderer = ReadRenderer(grid);
            if (renderer?.Target != null)
            {
                facts.ViewportWidth = renderer.Target.width;
                facts.ViewportHeight = renderer.Target.height;
                facts.RendererPath = "compute (LifeGpu Render kernel)";
            }
            else
            {
                facts.RendererPath = "per-cell Painter2D fallback";
            }

            if (facts.CellPixels <= 0)
                facts.CellPixels = 1;

            if (facts.ViewportWidth > 0 && facts.ViewportHeight > 0)
            {
                // What is on screen, capped by the board: a 256x256 board in a 615x456
                // viewport shows all of it, a 4096x4096 board at one pixel per cell shows
                // a 615x456 window into it.
                facts.VisibleCellsX = Math.Min(width, facts.ViewportWidth / facts.CellPixels);
                facts.VisibleCellsY = Math.Min(height, facts.ViewportHeight / facts.CellPixels);
                facts.VisibleFraction = (double)facts.VisibleCellsX * facts.VisibleCellsY / cells;
                facts.BoardFitsViewport = (long)width * facts.CellPixels <= facts.ViewportWidth &&
                                          (long)height * facts.CellPixels <= facts.ViewportHeight;
            }

            // The component cost of the CPU backend's display path: a managed copy into the
            // upload scratch (with 0/1 normalisation) plus ComputeBuffer.SetData.
            //
            // Scope, because round 1 overstated it: this is a CPU-side array read, not a
            // GPU-to-CPU transfer, and the grid runs it only when the board has changed AND
            // the CPU backend is active -- LifeGridElement gates it on uploadPending, so
            // panning, zooming or a plain Refresh do not trigger it. The scenario
            // "running-cpu-backend" below measures the same path in place, through the grid.
            if (renderer != null)
            {
                using var cpu = new CpuLifeBackend(width, height);
                cpu.LoadBoard(board);

                var sink = new uint[cells];
                var samples = new List<double>(3);
                for (int i = 0; i < 3; i++)
                {
                    var watch = Stopwatch.StartNew();
                    cpu.TryReadAllCells(sink);
                    renderer.Upload(sink);
                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }

                facts.CpuBoardCopyAndUpload = LifeBenchStatistics.Summarise(samples);
            }

            return facts;
        }

        /// <summary>The grid's renderer, read reflectively: the probe measures the shipped object.</summary>
        private static LifeBoardRenderer ReadRenderer(LifeGridElement grid)
        {
            if (grid == null)
                return null;

            FieldInfo field = typeof(LifeGridElement).GetField(
                "renderer", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(grid) as LifeBoardRenderer;
        }

        private LifeGridElement gridRef;
        private LifeTerminalController controllerRef;

        private enum Repaint
        {
            None,
            EveryFrame,
        }

        /// <summary>One cross-frame scenario, with everything needed to read its numbers.</summary>
        private sealed class ScenarioResult
        {
            public string Name;
            public string Backend;
            public string Board;
            public bool ForcedRepaint;
            public int RequestedGenerationsPerSecond;
            public int StartGeneration;
            public int GenerationsAdvanced;
            public LifeBenchStatistics.Summary Frames;
            public float FramesPerSecond;
            public float GenerationsPerSecond;
            public float ControllerAchievedGenerationsPerSecond;
            public bool ControllerRateSampleFormed;
            public bool ControllerOverloaded;

            /// <summary>
            /// The overload flag sampled over the whole window, not just read at the end. With a
            /// background backend the clock crosses its interval once per frame and drops the debt
            /// each time, so the flag flips on and off many times inside one generation: a single
            /// read at the end of the scenario is a coin flip, while "was it ever set" and "how
            /// many frames was it set" describe the window.
            /// </summary>
            public bool ControllerEverOverloaded;
            public int ControllerOverloadFrames;
            public double FirstDecileFrameMedianMs;
            public double LastDecileFrameMedianMs;
            public double? EdgeDecileRatio;
            public bool CutOffBySafetyCap;
            public int FramesWhereGridUploadCostWasNonZero;
            public LifeBenchStatistics.Summary GridReportedUploadMs;

            // Stage D: what a background backend costs where. The frame statistics above say what
            // the interface paid; these say what the worker paid, and the handover copy says what
            // the main thread paid to feed it. They are never added together into "the cost of a
            // generation", because they happen on different threads at different moments.
            public bool BackgroundBackend;
            public int BackgroundAdoptionsObserved;
            public LifeBenchStatistics.Summary BackgroundComputeMs;
            public LifeBenchStatistics.Summary BackgroundResultCopyMs;
            public LifeBenchStatistics.Summary BackgroundHandoverCopyMs;
            public int BackgroundRefusedGenerationsAtEnd;
            public int BackgroundRefusedSubmissionsAtEnd;

            // Frame timings. Three different counts, because round 2 collapsed them into one
            // and then called the number "requested frames":
            //   * captureCalls -- how many times CaptureFrameTimings was called (once per frame
            //     in the window, plus the discarded frame);
            //   * returnCap -- the size of the array handed to GetLatestTimings, i.e. the
            //     maximum number of records asked back;
            //   * validReturned -- how many of those actually carried non-zero times.
            // GetLatestTimings is called ONCE, at the end of the window, so what comes back is
            // the most recent records available at that moment, NOT a uniform sample of the
            // window. The record says so.
            public int FrameTimingsCaptureCalls;
            public int FrameTimingsReturnCap;
            public int ValidGpuFrameTimingSamples;
            public int ValidCpuFrameTimingSamples;
            public LifeBenchStatistics.Summary GpuFrameMs;
            public LifeBenchStatistics.Summary CpuFrameMs;
        }

        /// <summary>Frames sampled per scenario before the wall-clock budget can end it.</summary>
        private static int FrameSamplesFor(int cells) => cells <= 1 << 22 ? 120 : 40;

        /// <summary>
        /// Hard ceiling on one scenario's wall clock. The clock is bounded now, so this is a
        /// backstop rather than the thing that keeps the benchmark alive.
        /// </summary>
        private const float SafetyCapSeconds = 20f;

        /// <summary>
        /// Runs one scenario and samples everything about it in the same window: frame
        /// deltas, how many generations actually went by, the upload cost the grid itself
        /// reported, and frame timings.
        ///
        /// <para>The window is bounded by a wall-clock budget, a frame cap and -- for running
        /// scenarios -- a minimum number of generations: without that last bound a fast board
        /// fits its whole budget into a few milliseconds and the sample reports a frame
        /// interval for a board that never advanced.</para>
        /// </summary>
        private IEnumerator MeasureScenario(
            List<ScenarioResult> results, string name, ILifeBackend backend, int cells,
            int requestedRate, Repaint repaint, float budgetSeconds, int minGenerations,
            bool captureFrameTimings)
        {
            var result = new ScenarioResult
            {
                Name = name,
                Backend = backend.Name,
                Board = $"{backend.Width}x{backend.Height}",
                ForcedRepaint = repaint == Repaint.EveryFrame,
                RequestedGenerationsPerSecond = requestedRate,
                StartGeneration = backend.Generation,
            };

            int maxFrames = minGenerations > 0 ? 200000 : FrameSamplesFor(cells);
            double startedAt = Time.realtimeSinceStartup;
            var samples = new List<double>(Math.Min(maxFrames, 8192));
            var uploads = new List<double>();
            var timings = new FrameTiming[Math.Min(maxFrames, 200)];

            // Stage D: a background backend retires generations between frames, so the worker's own
            // costs are collected as each adoption is observed rather than sampled per frame.
            var asyncBackend = backend as ILifeAsyncBackend;
            var computeSamples = new List<double>();
            var resultCopySamples = new List<double>();
            var handoverSamples = new List<double>();
            int adoptionsSeen = asyncBackend?.AdoptedGenerations ?? 0;
            result.BackgroundBackend = asyncBackend != null;

            // One discarded frame so the first sample is not the frame that started this.
            yield return null;

            for (int i = 0; i < maxFrames; i++)
            {
                if (repaint == Repaint.EveryFrame)
                    gridRef?.MarkBoardDirty();

                if (captureFrameTimings)
                {
                    FrameTimingManager.CaptureFrameTimings();
                    result.FrameTimingsCaptureCalls++;
                }

                yield return null;

                samples.Add(Time.unscaledDeltaTime * 1000.0);

                if (controllerRef != null && controllerRef.ClockOverloaded)
                {
                    result.ControllerEverOverloaded = true;
                    result.ControllerOverloadFrames++;
                }

                if (asyncBackend != null && asyncBackend.AdoptedGenerations != adoptionsSeen)
                {
                    adoptionsSeen = asyncBackend.AdoptedGenerations;
                    result.BackgroundAdoptionsObserved++;
                    computeSamples.Add(asyncBackend.LastComputeMilliseconds);
                    resultCopySamples.Add(asyncBackend.LastResultCopyMilliseconds);

                    // The main-thread handover copy that rebuilt the worker's simulation for this
                    // run. Taken once, on the first adoption seen: whether it happened just before
                    // this window or inside it, it is the copy this scenario's generations were
                    // computed from.
                    if (handoverSamples.Count == 0 && asyncBackend.LastResyncCopyMilliseconds > 0.0)
                        handoverSamples.Add(asyncBackend.LastResyncCopyMilliseconds);
                }

                // The production path reports its own upload cost. The counter is sticky, so
                // this counts FRAMES whose reported cost was non-zero, which is not a count of
                // uploads; the record labels it that way.
                if (gridRef != null && gridRef.LastUploadMilliseconds > 0f)
                {
                    result.FramesWhereGridUploadCostWasNonZero++;
                    uploads.Add(gridRef.LastUploadMilliseconds);
                }

                bool windowOver = Time.realtimeSinceStartup - startedAt >= budgetSeconds && samples.Count >= 10;
                bool enoughGenerations = backend.Generation - result.StartGeneration >= minGenerations;

                // Backstop on the scenario's wall clock. The clock itself is bounded now, so
                // this only catches a scenario that keeps progressing without meeting its
                // exit conditions.
                bool cutOff = Time.realtimeSinceStartup - startedAt >= SafetyCapSeconds;
                result.CutOffBySafetyCap = cutOff;

                if (cutOff || (windowOver && enoughGenerations))
                    break;
            }

            // Deciles first, from the list in TIME ORDER. Round 2 summarised (which sorted the
            // list in place) and only then took the deciles, so those figures described the
            // fastest and slowest tenth rather than the window's beginning and end.
            result.FirstDecileFrameMedianMs = LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: false);
            result.LastDecileFrameMedianMs = LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: true);
            result.EdgeDecileRatio = LifeBenchStatistics.EdgeDecileRatio(samples);
            result.Frames = LifeBenchStatistics.Summarise(samples);
            result.GenerationsAdvanced = backend.Generation - result.StartGeneration;

            double elapsed = Time.realtimeSinceStartup - startedAt;
            result.FramesPerSecond = elapsed > 0.0 ? (float)(samples.Count / elapsed) : 0f;
            result.GenerationsPerSecond = elapsed > 0.0
                ? (float)(result.GenerationsAdvanced / elapsed)
                : 0f;

            // What the controller itself says it achieved, and whether it had to drop
            // catch-up debt. This is the number the interface shows; it is recorded next to
            // the benchmark's own count so the two can be compared -- and it is NOT evidence
            // on its own, because the controller publishes one tumbling window's average.
            result.ControllerAchievedGenerationsPerSecond = controllerRef != null
                ? controllerRef.AchievedGenerationsPerSecond
                : 0f;
            result.ControllerRateSampleFormed = controllerRef != null && controllerRef.AchievedRateMeasured;
            result.ControllerOverloaded = controllerRef != null && controllerRef.ClockOverloaded;

            if (asyncBackend != null)
            {
                if (computeSamples.Count > 0)
                    result.BackgroundComputeMs = LifeBenchStatistics.Summarise(computeSamples);

                if (resultCopySamples.Count > 0)
                    result.BackgroundResultCopyMs = LifeBenchStatistics.Summarise(resultCopySamples);

                if (handoverSamples.Count > 0)
                    result.BackgroundHandoverCopyMs = LifeBenchStatistics.Summarise(handoverSamples);

                result.BackgroundRefusedGenerationsAtEnd = asyncBackend.RefusedGenerations;
                result.BackgroundRefusedSubmissionsAtEnd = asyncBackend.RefusedSubmissions;
            }

            if (uploads.Count > 0)
                result.GridReportedUploadMs = LifeBenchStatistics.Summarise(uploads);

            if (captureFrameTimings)
            {
                result.FrameTimingsReturnCap = timings.Length;
                uint captured = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
                var cpu = new List<double>();
                var gpu = new List<double>();
                for (int i = 0; i < captured && i < timings.Length; i++)
                {
                    if (timings[i].cpuFrameTime > 0.0)
                        cpu.Add(timings[i].cpuFrameTime);

                    if (timings[i].gpuFrameTime > 0.0)
                        gpu.Add(timings[i].gpuFrameTime);
                }

                if (cpu.Count > 0)
                    result.CpuFrameMs = LifeBenchStatistics.Summarise(cpu);

                if (gpu.Count > 0)
                    result.GpuFrameMs = LifeBenchStatistics.Summarise(gpu);

                result.ValidCpuFrameTimingSamples = cpu.Count;
                result.ValidGpuFrameTimingSamples = gpu.Count;
                frameTimingStatsReported |= cpu.Count > 0 || gpu.Count > 0;
            }

            results.Add(result);
            Debug.Log($"[stage-c-bench] scenario {name}: {samples.Count} frames, " +
                      $"{result.GenerationsAdvanced} generations, median {result.Frames.MedianMs:F3} ms, " +
                      $"max {result.Frames.MaxMs:F3} ms, first/last decile " +
                      $"{result.FirstDecileFrameMedianMs:F3}/{result.LastDecileFrameMedianMs:F3} ms, " +
                      $"controller rate {result.ControllerAchievedGenerationsPerSecond:F1}/s " +
                      $"overloaded={result.ControllerOverloaded}, " +
                      $"frames with non-zero upload cost {result.FramesWhereGridUploadCostWasNonZero}, " +
                      $"frame timings captured {result.FrameTimingsCaptureCalls}x, returned " +
                      $"{result.ValidGpuFrameTimingSamples} gpu / {result.ValidCpuFrameTimingSamples} cpu " +
                      $"of cap {result.FrameTimingsReturnCap}");
        }

        /// <summary>
        /// What a pause costs while the background CPU backend is computing. The pause semantics
        /// are a product decision (see the stage-D document), and this is the record of them: the
        /// command itself, whether a generation was in flight, whether the display froze, whether
        /// the finished generation was kept for the resume, and what the frames after the pause
        /// cost.
        /// </summary>
        private sealed class PauseResponseFacts
        {
            public bool Measured;
            public string Backend;
            public string Board;
            public bool StepInFlightAtPause;
            public double PauseCommandMs;
            public int GenerationsBefore;
            public int GenerationImmediatelyAfterPause;
            public int GenerationAfterPauseObservation;
            public int FramesObservedAfterPause;
            public LifeBenchStatistics.Summary FramesAfterPause;
            public bool CompletionWaitingWhilePaused;
            public bool AdvancedByOneOnResume;
            public int GenerationAfterResume;
            public int RefusedGenerationsAtEnd;
            public double ObservationSeconds;

            /// <summary>True when the display did not move while the clock was paused.</summary>
            public bool DisplayFrozen => GenerationAfterPauseObservation == GenerationImmediatelyAfterPause;
        }

        /// <summary>How long to wait for a generation to be in flight before pausing.</summary>
        private const float PauseCatchSeconds = 2.0f;

        /// <summary>How long the frames right after the pause are observed, at minimum.</summary>
        private const float PauseObservationSeconds = 0.5f;

        /// <summary>
        /// How long to keep observing when a generation was in flight: it has to finish somewhere,
        /// and at 4096x4096 that takes most of a second.
        /// </summary>
        private const float PauseStashWaitSeconds = 4.0f;

        private IEnumerator MeasurePauseResponse(PauseResponseFacts facts)
        {
            if (controllerRef == null || gridRef?.Backend is not ILifeAsyncBackend backend)
                yield break;

            facts.Measured = true;
            facts.Backend = gridRef.Backend.Name;
            facts.Board = $"{gridRef.Backend.Width}x{gridRef.Backend.Height}";

            // Run until a generation is in flight. A fast board (256x256) finishes between frames,
            // so not catching one is a legitimate outcome and is recorded as such.
            SetRunning(controllerRef, true);
            float catchUntil = Time.realtimeSinceStartup + PauseCatchSeconds;
            while (Time.realtimeSinceStartup < catchUntil && !backend.IsComputing && !backend.HasCompletedGeneration)
                yield return null;

            facts.GenerationsBefore = gridRef.Backend.Generation;
            facts.StepInFlightAtPause = backend.IsComputing;

            // The command under test: this is what has to be immediate.
            var watch = Stopwatch.StartNew();
            SetRunning(controllerRef, false);
            watch.Stop();
            facts.PauseCommandMs = watch.Elapsed.TotalMilliseconds;
            facts.GenerationImmediatelyAfterPause = gridRef.Backend.Generation;

            // Frames after the pause. Always at least PauseObservationSeconds; longer when a
            // generation was in flight, because that generation has to land in the waiting slot
            // before the stash can be observed.
            float pausedAt = Time.realtimeSinceStartup;
            var frames = new List<double>();
            while (true)
            {
                double paused = Time.realtimeSinceStartup - pausedAt;
                bool minimumElapsed = paused >= PauseObservationSeconds;
                bool waitingForStash = facts.StepInFlightAtPause && !backend.HasCompletedGeneration &&
                                       paused < PauseStashWaitSeconds;
                if (minimumElapsed && !waitingForStash)
                    break;

                yield return null;
                frames.Add(Time.unscaledDeltaTime * 1000.0);
            }

            facts.ObservationSeconds = Time.realtimeSinceStartup - pausedAt;
            facts.GenerationAfterPauseObservation = gridRef.Backend.Generation;
            facts.FramesObservedAfterPause = frames.Count;
            if (frames.Count > 0)
                facts.FramesAfterPause = LifeBenchStatistics.Summarise(frames);

            // The generation that was in flight must have finished into the waiting slot: kept for
            // the resume, not displayed and not thrown away.
            facts.CompletionWaitingWhilePaused = backend.HasCompletedGeneration;
            facts.RefusedGenerationsAtEnd = backend.RefusedGenerations;

            // Resume: the display advances by exactly one, and nothing is skipped.
            int before = gridRef.Backend.Generation;
            SetRunning(controllerRef, true);
            float resumeUntil = Time.realtimeSinceStartup + PauseStashWaitSeconds;
            while (Time.realtimeSinceStartup < resumeUntil && gridRef.Backend.Generation == before)
                yield return null;

            facts.GenerationAfterResume = gridRef.Backend.Generation;
            facts.AdvancedByOneOnResume = gridRef.Backend.Generation == before + 1;
            facts.RefusedGenerationsAtEnd = backend.RefusedGenerations;
            SetRunning(controllerRef, false);
        }

        /// <summary>
        /// Switches the controller's evolution backend through the control the interface
        /// uses, so the CPU scenario is a real configuration rather than a synthetic one.
        /// </summary>
        private static bool SwitchBackend(LifeTerminalController controller, bool gpu)
        {
            MethodInfo method = typeof(LifeTerminalController).GetMethod(
                "SwitchBackend", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                return false;

            method.Invoke(controller, new object[] { gpu });
            return true;
        }

        // -- 5. memory ---------------------------------------------------------

        private sealed class MemoryFacts
        {
            public long ManagedTotalMB;
            public long AllocatedTotalMB;
            public long ReservedTotalMB;
            public long GraphicsDriverMB;

            // Deltas are kept in bytes: at 256x256 a board is well under a megabyte, and
            // rounding to whole megabytes reported every one of them as zero.
            public long CpuBoardManagedDeltaBytes;
            public long CpuBoardAllocatedDeltaBytes;
            public long GpuBoardManagedDeltaBytes;
            public long GpuBoardGraphicsDriverDeltaBytes;
            public long GpuBoardAllocatedDeltaBytes;
            public bool GpuBoardMeasured;
            public string GpuBoardNote;
            public bool GraphicsDriverMemoryAvailable;
            public string GraphicsDriverMemoryNote;

            // What the source says the enumerated buffers hold. This is a CAPACITY SUBTOTAL
            // of the buffers this probe lists, not the application's live total and not a
            // peak: it leaves out, among others, the seeding session's two board arrays, the
            // controller's initial state and pattern board, the renderer's viewport texture,
            // and it counts a temporary readback pool that only exists during measurement.
            public long CpuStateBytes;
            public long GpuStateBuffersBytes;
            public long GpuStagingArraysBytes;
            public long RendererUploadBufferBytes;
            public long RendererUploadScratchBytes;
            public long BenchBoardBytes;
            public long BenchScratchBytes;
            public long ListedSubtotalBytes;
        }

        private static MemoryFacts MeasureMemory(int width, int height, byte[] board)
        {
            var facts = new MemoryFacts();

            long managedBefore = GC.GetTotalMemory(false);
            long allocatedBefore = Profiler.GetTotalAllocatedMemoryLong();

            using (var cpu = new CpuLifeBackend(width, height))
            {
                cpu.LoadBoard(board);
                facts.CpuBoardManagedDeltaBytes = GC.GetTotalMemory(false) - managedBefore;
                facts.CpuBoardAllocatedDeltaBytes =
                    Profiler.GetTotalAllocatedMemoryLong() - allocatedBefore;
            }

            // A fresh managed baseline for the GPU half: the CPU backend above has been
            // disposed by now, and reusing its baseline made this delta come out negative.
            long managedBeforeGpu = GC.GetTotalMemory(false);
            long allocatedBeforeGpu = Profiler.GetTotalAllocatedMemoryLong();
            long gfxBefore = Profiler.GetAllocatedMemoryForGraphicsDriver();

            if (GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend gpu, out string error))
            {
                gpu.LoadBoard(board);

                facts.GpuBoardMeasured = true;
                facts.GpuBoardManagedDeltaBytes = GC.GetTotalMemory(false) - managedBeforeGpu;
                facts.GpuBoardGraphicsDriverDeltaBytes =
                    Profiler.GetAllocatedMemoryForGraphicsDriver() - gfxBefore;
                facts.GpuBoardAllocatedDeltaBytes =
                    Profiler.GetTotalAllocatedMemoryLong() - allocatedBeforeGpu;

                gpu.Dispose();
            }
            else
            {
                facts.GpuBoardNote = error;
            }

            facts.ManagedTotalMB = ToMB(GC.GetTotalMemory(false));
            facts.AllocatedTotalMB = ToMB(Profiler.GetTotalAllocatedMemoryLong());
            facts.ReservedTotalMB = ToMB(Profiler.GetTotalReservedMemoryLong());
            facts.GraphicsDriverMB = ToMB(Profiler.GetAllocatedMemoryForGraphicsDriver());
            facts.GraphicsDriverMemoryAvailable = Profiler.GetAllocatedMemoryForGraphicsDriver() > 0;
            facts.GraphicsDriverMemoryNote = facts.GraphicsDriverMemoryAvailable
                ? null
                : "Profiler.GetAllocatedMemoryForGraphicsDriver and the frame timings it belongs " +
                  "with are only populated in a development build with frame timing stats enabled";

            long cells = (long)width * height;
            facts.CpuStateBytes = cells * 2;             // LifeSimulation current + next
            facts.GpuStateBuffersBytes = cells * 4 * 2;  // front + back
            facts.GpuStagingArraysBytes = cells * 4 * 2; // uploadScratch + readbackScratch
            facts.RendererUploadBufferBytes = cells * 4; // display upload ComputeBuffer
            facts.RendererUploadScratchBytes = cells * 4;
            facts.BenchBoardBytes = cells;               // the byte[] this probe built
            facts.BenchScratchBytes = cells * 4;         // readback sink
            facts.ListedSubtotalBytes = facts.CpuStateBytes + facts.GpuStateBuffersBytes +
                                        facts.GpuStagingArraysBytes + facts.RendererUploadBufferBytes +
                                        facts.RendererUploadScratchBytes + facts.BenchBoardBytes +
                                        facts.BenchScratchBytes;

            return facts;
        }

        private static long ToMB(long bytes) => bytes / (1024 * 1024);

        // -- sizing helpers ----------------------------------------------------

        /// <summary>Repeats per measurement, scaled down as the board grows. Precision only:
        /// the batch size is uniform, so the across-size trend is not an artefact of it.</summary>
        private static int RepeatsFor(int cells) => cells <= 1 << 20 ? 5 : cells <= 1 << 22 ? 3 : 2;

        private static int WarmupFor(int cells) => cells <= 1 << 20 ? 20 : 2;

        private static byte[] BuildFixedBoard(int cells)
        {
            var board = new byte[cells];
            var random = new System.Random(RandomSeed);
            for (int i = 0; i < cells; i++)
                board[i] = random.NextDouble() < Density ? (byte)1 : (byte)0;

            return board;
        }

        /// <summary>
        /// Starts or stops the clock through the controller's OWN commands, never by writing the
        /// private running flag. Round 3 wrote the flag, which skipped the pause path entirely --
        /// and the pause path is exactly what ends the run's clock state (debt, rate window,
        /// published rate, overload flag). A scenario that started from a stale report was
        /// measuring a state the application cannot reach. Returns the state it found.
        /// </summary>
        private static bool SetRunning(LifeTerminalController controller, bool value)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "running", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return false;

            bool previous = (bool)field.GetValue(controller);
            if (previous == value)
                return previous;

            MethodInfo command = typeof(LifeTerminalController).GetMethod(
                value ? "ToggleRunning" : "Stop", BindingFlags.Instance | BindingFlags.NonPublic);
            if (command == null)
            {
                Debug.LogWarning($"[stage-c-bench] could not {(value ? "start" : "stop")} the clock " +
                                 "through the controller's own command");
                return previous;
            }

            command.Invoke(controller, null);
            return previous;
        }

        /// <summary>
        /// Drives the real speed slider, so the running scenarios use the rate the interface
        /// offers rather than one invented for the benchmark. Returns the value it landed on.
        /// </summary>
        private static int SetSpeed(LifeTerminalController controller, int generationsPerSecond)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "speedSlider", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(controller) is not SliderInt slider)
                return -1;

            slider.value = generationsPerSecond;
            return slider.value;
        }

        // -- output ------------------------------------------------------------

        private void WriteLine(bool frameScenariosOnly, int width, int height, int cells, string backendName,
            string boardIdentifiedAs, Generation fbm, Generation uniform, LifeBenchStatistics.Summary cpuLoad, LifeBenchStatistics.Summary gpuLoad,
            LifeBenchStatistics.Summary cpuRules, LifeBenchStatistics.Summary gpuSubmit, LifeBenchStatistics.Summary gpuBatch, DisplayFacts display,
            List<ScenarioResult> scenarios, PauseResponseFacts pauseResponse, MemoryFacts memory, int configuredVSync,
            int configuredTargetFrameRate, bool focusedThroughout, double elapsedSeconds)
        {
            var json = new StringBuilder();
            json.Append('{');
            json.Append("\"kind\": \"stage-c-bench\", ");
            json.Append($"\"recordRound\": {RecordRound}, ");
            json.Append($"\"unityVersion\": \"{Application.unityVersion}\", ");
            json.Append($"\"graphicsDevice\": \"{SystemInfo.graphicsDeviceName}\", ");
            json.Append($"\"graphicsApi\": \"{SystemInfo.graphicsDeviceType}\", ");
            json.Append($"\"processor\": \"{SystemInfo.processorType}\", ");
            json.Append($"\"processorCount\": {SystemInfo.processorCount}, ");
            json.Append($"\"systemMemoryMB\": {SystemInfo.systemMemorySize}, ");
            json.Append($"\"graphicsMemoryMB\": {SystemInfo.graphicsMemorySize}, ");
            json.Append($"\"isDevelopmentBuild\": {Bool(Debug.isDebugBuild)}, ");
            json.Append($"\"buildGuid\": \"{Application.buildGUID}\", ");
            json.Append($"\"dataPath\": \"{Application.dataPath.Replace('\\', '/')}\", ");
            json.Append($"\"frameTimingStatsReported\": {Bool(frameTimingStatsReported)}, ");
            json.Append($"\"runInBackgroundForced\": true, ");
            json.Append($"\"runInBackgroundRestored\": false, ");
            json.Append($"\"focusedThroughout\": {Bool(focusedThroughout)}, ");
            json.Append($"\"targetFrameRate\": {configuredTargetFrameRate}, ");
            json.Append($"\"vSyncCount\": {configuredVSync}, ");
            json.Append($"\"frameScenariosRunVsyncOff\": true, ");
            json.Append($"\"phasesRun\": [{PhaseList(frameScenariosOnly)}], ");
            json.Append($"\"phasesSkipped\": [{SkippedPhaseList(frameScenariosOnly)}], ");
            json.Append($"\"phasesNote\": \"{PhasesNote(frameScenariosOnly)}\", ");
            json.Append($"\"board\": \"{width}x{height}\", ");
            json.Append($"\"width\": {width}, \"height\": {height}, \"cells\": {cells}, ");
            json.Append($"\"activeBackend\": \"{backendName}\", ");
            json.Append($"\"frameScenarioBoard\": \"{boardIdentifiedAs}\", ");
            json.Append($"\"elapsedSeconds\": {F(elapsedSeconds)}, ");

            if (frameScenariosOnly)
            {
                // Named as skipped rather than omitted or zeroed: a reader must be able to tell
                // "not measured" from "measured as nothing".
                json.Append("\"generate\": null, \"upload\": null, \"evolution\": null, ");
                json.Append("\"display\": null, ");
            }
            else
            {
                json.Append("\"generate\": {");
                json.Append($"\"fbm\": {GenerationJson(fbm)}, ");
                json.Append($"\"uniform\": {GenerationJson(uniform)}, ");
                json.Append($"\"excludes\": [\"upload\", \"evolution\", \"display\"], ");
                json.Append("\"method\": \"Stopwatch around LifeNoiseSeeding.Generate (CPU, managed, synchronous)\", ");
                json.Append("\"caveat\": \"uniform mode ignores scale/warp/cluster, so its parameters string is not comparable field by field\"");
                json.Append("}, ");

                json.Append("\"upload\": {");
                json.Append($"\"cpuLoadBoardMs\": {StatsOrNull(cpuLoad)}, ");
                json.Append($"\"gpuLoadBoardMs\": {StatsOrNull(gpuLoad)}, ");
                json.Append($"\"gpuCellsPerSecond\": {CellsPerSecond(gpuLoad, cells)}, ");
                json.Append($"\"cpuCellsPerSecond\": {CellsPerSecond(cpuLoad, cells)}, ");
                json.Append("\"gpuIncludes\": [\"staging uint copy\", \"ComputeBuffer.SetData\"], ");
                json.Append("\"caveat\": \"SetData returns once the data has been handed to the driver; it is not a transfer-complete figure, and no bandwidth claim is made from it\"");
                json.Append("}, ");

                json.Append("\"evolution\": {");
                json.Append($"\"cpuMsPerGeneration\": {StatsOrNull(cpuRules)}, ");
                json.Append($"\"gpuSubmitOnlyMsPerGeneration\": {StatsOrNull(gpuSubmit)}, ");
                json.Append($"\"gpuBatchAdvancePlusOneReadbackAmortisedMsPerGeneration\": {StatsOrNull(gpuBatch)}, ");
                json.Append($"\"batchSteps\": {BatchSteps}, ");
                json.Append($"\"cpuCellsPerSecond\": {CellsPerSecond(cpuRules, cells)}, ");
                json.Append("\"boundary\": \"wrap\", ");
                json.Append("\"excludes\": [\"initial state generation\", \"CPU to GPU state upload\", \"display\"], ");
                json.Append("\"caveat\": \"gpuSubmitOnly is the cost of handing work to the GPU and must never be quoted as GPU execution time. The batch figure is (N generations + ONE full readback) / N: it is amortised, its readback share is NOT decomposed, and N is identical at every board size so the trend across sizes compares like with like. The CPU figure computes in place and never reads back, so it is a different operation from the batch figure and no CPU/GPU ratio is derived from the pair\"");
                json.Append("}, ");

                json.Append("\"display\": {");
                json.Append($"\"repaintDispatchMsPerCall\": {StatsOrNull(display.Refresh)}, ");
                json.Append($"\"cpuBackendBoardCopyAndUploadMs\": {StatsOrNull(display.CpuBoardCopyAndUpload)}, ");
                json.Append("\"cpuBackendBoardCopyAndUploadIncludes\": [\"managed copy into the uint upload scratch, with 0/1 normalisation\", \"ComputeBuffer.SetData\"], ");
                json.Append("\"cpuBackendBoardCopyAndUploadIsGpuReadback\": false, ");
                json.Append("\"cpuBackendBoardCopyAndUploadWhen\": \"only when the board has changed AND the CPU backend is active; LifeGridElement gates it on uploadPending, so panning, zooming or a plain Refresh do not trigger it\", ");
                json.Append($"\"rendererPath\": \"{display.RendererPath}\", ");
                json.Append($"\"viewport\": \"{display.ViewportWidth}x{display.ViewportHeight}\", ");
                json.Append($"\"cellPixels\": {display.CellPixels}, ");
                json.Append($"\"visibleCells\": \"{display.VisibleCellsX}x{display.VisibleCellsY}\", ");
                json.Append($"\"visibleFractionOfBoard\": {F(display.VisibleFraction)}, ");
                json.Append($"\"boardFitsViewport\": {Bool(display.BoardFitsViewport)}, ");
                json.Append("\"caveat\": \"repaintDispatchMs is submit side only (dispatch + background assignment), not a GPU execution time\"");
                json.Append("}, ");
            }

            json.Append("\"frameScenarios\": [");
            for (int i = 0; i < scenarios.Count; i++)
            {
                if (i > 0)
                    json.Append(", ");

                json.Append(ScenarioJson(scenarios[i]));
            }

            json.Append("], ");

            json.Append("\"frameScenarioCaveat\": \"every scenario ran with vsync off and the frame rate uncapped, so an interval describes work rather than pacing -- and is still not a pure compute cost. Each scenario records TWO rates for the same window: achievedGenerationsPerSecond is this probe's own completed-generation difference over its own wall clock, and controllerReportedGenerationsPerSecond is what the panel published from its 0.5 s tumbling window; neither stands alone. GPU and CPU frame times come from FrameTimingManager, are frame-level (the whole frame, including UI composition), and are reported with the number of VALID samples against the number requested\", ");

            json.Append("\"pauseResponse\": ");
            json.Append(PauseResponseJson(pauseResponse));
            json.Append(", ");

            json.Append("\"backgroundEvolutionCaveat\": \"on the CPU backend a generation is computed on a worker thread. backgroundComputeMs is the rule step on THAT thread; backgroundResultCopyMs is the worker reading its finished board out for handover; backgroundHandoverCopyMs is the copy the MAIN THREAD makes when the worker has to rebuild after the board changed; gridReportedUploadMs in the frame scenarios is the main thread's whole-board upload to the display buffer. They happen on different threads at different moments and are never summed. Moving the computation off the frame does NOT make a generation cheaper and does NOT remove the upload: the frame statistics of running-cpu-backend show what the interface paid, and the upload is still paid on the main thread\", ");

            if (frameScenariosOnly)
            {
                json.Append("\"memory\": null, ");
            }
            else
            {
                json.Append("\"memory\": {");
                json.Append($"\"managedTotalMB\": {memory.ManagedTotalMB}, ");
                json.Append($"\"allocatedTotalMB\": {memory.AllocatedTotalMB}, ");
                json.Append($"\"reservedTotalMB\": {memory.ReservedTotalMB}, ");
                json.Append($"\"graphicsDriverAllocatedMB\": {memory.GraphicsDriverMB}, ");
                json.Append($"\"graphicsDriverMemoryAvailable\": {Bool(memory.GraphicsDriverMemoryAvailable)}, ");
                json.Append($"\"graphicsDriverMemoryNote\": {(memory.GraphicsDriverMemoryNote == null ? "null" : "\"" + memory.GraphicsDriverMemoryNote.Replace("\"", "'") + "\"")}, ");
                json.Append($"\"cpuBoardManagedDeltaBytes\": {memory.CpuBoardManagedDeltaBytes}, ");
                json.Append($"\"cpuBoardAllocatedDeltaBytes\": {memory.CpuBoardAllocatedDeltaBytes}, ");
                json.Append($"\"gpuBoardMeasured\": {Bool(memory.GpuBoardMeasured)}, ");
                json.Append($"\"gpuBoardManagedDeltaBytes\": {(memory.GpuBoardMeasured ? memory.GpuBoardManagedDeltaBytes.ToString(CultureInfo.InvariantCulture) : "null")}, ");
                json.Append($"\"gpuBoardGraphicsDriverDeltaBytes\": {(memory.GpuBoardMeasured ? memory.GpuBoardGraphicsDriverDeltaBytes.ToString(CultureInfo.InvariantCulture) : "null")}, ");
                json.Append($"\"gpuBoardAllocatedDeltaBytes\": {(memory.GpuBoardMeasured ? memory.GpuBoardAllocatedDeltaBytes.ToString(CultureInfo.InvariantCulture) : "null")}, ");
                json.Append($"\"gpuBoardNote\": {(memory.GpuBoardNote == null ? "null" : "\"" + memory.GpuBoardNote.Replace("\"", "'") + "\"")}, ");
                json.Append($"\"gpuStateBuffersBytes\": {memory.GpuStateBuffersBytes}, ");
                json.Append($"\"driverDeltaMinusStateBuffersBytes\": {DriverDeltaMinusStateBuffers(memory)}, ");
                json.Append("\"driverDeltaAttribution\": \"the driver delta covers backend construction AND a LoadBoard; the two explicit state buffers account for 8 bytes per cell, and the rest is NOT attributed to anything -- no internal split was measured\", ");
                json.Append("\"managedDeltaUsable\": false, ");
                json.Append("\"managedDeltaNote\": \"the managed-heap delta is negative at small sizes and does not track the arrays that were allocated; it is not usable as an allocation measure and its cause was not established\", ");
                json.Append("\"listedBufferCapacityBytes\": {");
                json.Append($"\"cpuState\": {memory.CpuStateBytes}, ");
                json.Append($"\"gpuStateBuffers\": {memory.GpuStateBuffersBytes}, ");
                json.Append($"\"gpuStagingArrays\": {memory.GpuStagingArraysBytes}, ");
                json.Append($"\"rendererUploadBuffer\": {memory.RendererUploadBufferBytes}, ");
                json.Append($"\"rendererUploadScratch\": {memory.RendererUploadScratchBytes}, ");
                json.Append($"\"benchBoard\": {memory.BenchBoardBytes}, ");
                json.Append($"\"benchScratch\": {memory.BenchScratchBytes}, ");
                json.Append($"\"subtotal\": {memory.ListedSubtotalBytes}");
                json.Append("}, ");
                json.Append("\"listedBufferCapacityNote\": \"a subtotal of the buffers this probe enumerates, from the source. It is NOT the application's live total and NOT a peak: it omits, among others, the seeding session's two board arrays, the controller's initial state and pattern board, and the renderer's viewport texture, while counting a readback pool that only exists during measurement\"");
                json.Append("}, ");
            }

            json.Append($"\"utc\": \"{DateTime.UtcNow:O}\"");
            json.Append('}');

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(projectRoot, "stage-c-bench-r5.jsonl");
            File.AppendAllText(path, json + Environment.NewLine);
            Debug.Log($"[stage-c-bench] appended {width}x{height} ({backendName}) to {path}");
            Debug.Log($"[stage-c-bench] {json}");
        }

        private static string ScenarioJson(ScenarioResult scenario)
        {
            var json = new StringBuilder();
            json.Append('{');
            json.Append($"\"name\": \"{scenario.Name}\", ");
            json.Append($"\"backend\": \"{scenario.Backend}\", ");
            json.Append($"\"board\": \"{scenario.Board}\", ");
            json.Append($"\"forcedRepaintEveryFrame\": {Bool(scenario.ForcedRepaint)}, ");
            json.Append($"\"startGeneration\": {scenario.StartGeneration}, ");
            json.Append($"\"generationsAdvanced\": {scenario.GenerationsAdvanced}, ");
            json.Append($"\"requestedGenerationsPerSecond\": {scenario.RequestedGenerationsPerSecond}, ");
            json.Append($"\"achievedGenerationsPerSecond\": {F(scenario.GenerationsPerSecond)}, ");
            json.Append("\"achievedGenerationsPerSecondBasis\": \"the benchmark's own count: the board's generation at the end minus its generation at the start, divided by the wall clock between those two readings\", ");
            json.Append($"\"controllerReportedGenerationsPerSecond\": {F(scenario.ControllerAchievedGenerationsPerSecond)}, ");
            json.Append($"\"controllerRateSampleFormed\": {Bool(scenario.ControllerRateSampleFormed)}, ");
            json.Append($"\"controllerClockOverloadedAtEnd\": {Bool(scenario.ControllerOverloaded)}, ");
            json.Append($"\"controllerEverOverloaded\": {Bool(scenario.ControllerEverOverloaded)}, ");
            json.Append($"\"controllerOverloadFrames\": {scenario.ControllerOverloadFrames}, ");
            json.Append($"\"frames\": {StatsOrNull(scenario.Frames)}, ");
            json.Append($"\"framesPerSecond\": {F(scenario.FramesPerSecond)}, ");
            json.Append($"\"firstDecileFrameMedianMs\": {F(scenario.FirstDecileFrameMedianMs)}, ");
            json.Append($"\"lastDecileFrameMedianMs\": {F(scenario.LastDecileFrameMedianMs)}, ");
            json.Append($"\"lastOverFirstDecileRatio\": {LifeBenchStatistics.Format(scenario.EdgeDecileRatio)}, ");
            json.Append($"\"cutOffBySafetyCap\": {Bool(scenario.CutOffBySafetyCap)}, ");
            json.Append($"\"framesWhereGridUploadCostWasNonZero\": {scenario.FramesWhereGridUploadCostWasNonZero}, ");
            json.Append($"\"gridReportedUploadMs\": {StatsOrNull(scenario.GridReportedUploadMs)}, ");
            json.Append($"\"backgroundBackend\": {Bool(scenario.BackgroundBackend)}, ");
            json.Append($"\"backgroundAdoptionsObserved\": {scenario.BackgroundAdoptionsObserved}, ");
            json.Append($"\"backgroundComputeMs\": {StatsOrNull(scenario.BackgroundComputeMs)}, ");
            json.Append($"\"backgroundResultCopyMs\": {StatsOrNull(scenario.BackgroundResultCopyMs)}, ");
            json.Append($"\"backgroundHandoverCopyMs\": {StatsOrNull(scenario.BackgroundHandoverCopyMs)}, ");
            json.Append($"\"backgroundRefusedGenerationsAtEnd\": {scenario.BackgroundRefusedGenerationsAtEnd}, ");
            json.Append($"\"backgroundRefusedSubmissionsAtEnd\": {scenario.BackgroundRefusedSubmissionsAtEnd}, ");
            json.Append($"\"gpuFrameTimeMs\": {StatsOrNull(scenario.GpuFrameMs)}, ");
            json.Append($"\"cpuFrameTimeMs\": {StatsOrNull(scenario.CpuFrameMs)}, ");
            json.Append($"\"frameTimingsCaptureCalls\": {scenario.FrameTimingsCaptureCalls}, ");
            json.Append($"\"frameTimingsRequestedReturnCap\": {scenario.FrameTimingsReturnCap}, ");
            json.Append($"\"validGpuFrameTimingSamples\": {scenario.ValidGpuFrameTimingSamples}, ");
            json.Append($"\"validCpuFrameTimingSamples\": {scenario.ValidCpuFrameTimingSamples}, ");
            json.Append("\"frameTimingsScope\": \"capture was called once per frame for the whole window, but GetLatestTimings was called ONCE at the end, so what is reported is the most recent records available at that moment -- not a uniform sample of the window and not 'the number of frames requested'\", ");
            json.Append("\"gridUploadCounterNote\": \"LifeGridElement.LastUploadMilliseconds is sticky: it keeps its last value until another upload overwrites it or a path resets it to zero. The count above therefore counts FRAMES whose reported cost was non-zero, not uploads; the distribution of values is what carries information\"");
            json.Append('}');
            return json.ToString();
        }

        /// <summary>
        /// The measured driver delta minus the two explicit state buffers, or null when the
        /// counter was not populated. Emitting 0 - 8 bytes per cell would have said
        /// "the driver allocated nothing beyond the buffers", which is a claim the
        /// measurement never made.
        /// </summary>
        private static string DriverDeltaMinusStateBuffers(MemoryFacts memory)
        {
            if (!memory.GpuBoardMeasured || !memory.GraphicsDriverMemoryAvailable)
                return "null";

            return (memory.GpuBoardGraphicsDriverDeltaBytes - memory.GpuStateBuffersBytes)
                .ToString(CultureInfo.InvariantCulture);
        }

        private static string GenerationJson(Generation generation) =>
            generation == null
                ? "null"
                : $"{{{generation.Stats.Json()}, \"realisedDensity\": {F(generation.RealisedDensity)}, " +
                  $"\"alive\": {generation.Alive}, \"parameters\": \"{generation.Parameters}\"}}";

        /// <summary>
        /// The pause, as the record has to state it: what the command cost, whether a generation
        /// was in flight when it arrived, whether the display froze, whether the finished
        /// generation was kept, and whether the resume advanced by exactly one.
        /// </summary>
        private static string PauseResponseJson(PauseResponseFacts facts)
        {
            if (facts == null || !facts.Measured)
                return "null";

            var json = new StringBuilder();
            json.Append('{');
            json.Append($"\"backend\": \"{facts.Backend}\", ");
            json.Append($"\"board\": \"{facts.Board}\", ");
            json.Append($"\"stepInFlightAtPause\": {Bool(facts.StepInFlightAtPause)}, ");
            json.Append($"\"pauseCommandMs\": {F(facts.PauseCommandMs)}, ");
            json.Append($"\"generationsBefore\": {facts.GenerationsBefore}, ");
            json.Append($"\"generationImmediatelyAfterPause\": {facts.GenerationImmediatelyAfterPause}, ");
            json.Append($"\"generationAfterPauseObservation\": {facts.GenerationAfterPauseObservation}, ");
            json.Append($"\"advancedWhilePaused\": {facts.GenerationAfterPauseObservation - facts.GenerationImmediatelyAfterPause}, ");
            json.Append($"\"displayFrozenWhilePaused\": {Bool(facts.DisplayFrozen)}, ");
            json.Append($"\"observationSeconds\": {F(facts.ObservationSeconds)}, ");
            json.Append($"\"framesObservedAfterPause\": {facts.FramesObservedAfterPause}, ");
            json.Append($"\"framesAfterPause\": {StatsOrNull(facts.FramesAfterPause)}, ");
            json.Append($"\"completionWaitingWhilePaused\": {Bool(facts.CompletionWaitingWhilePaused)}, ");
            json.Append($"\"advancedByOneOnResume\": {Bool(facts.AdvancedByOneOnResume)}, ");
            json.Append($"\"generationAfterResume\": {facts.GenerationAfterResume}, ");
            json.Append($"\"refusedGenerationsAtEnd\": {facts.RefusedGenerationsAtEnd}, ");
            json.Append("\"caveat\": \"pauseCommandMs is the controller's own pause command on the main thread, not a frame time. A generation that was in flight when the pause arrived keeps running on the worker: it must land in the waiting slot (completionWaitingWhilePaused) without moving the display (displayFrozenWhilePaused) and be taken over on resume (advancedByOneOnResume). A fast board can finish between frames, in which case stepInFlightAtPause is false and the record says so rather than implying a generation was interrupted\"");
            json.Append('}');
            return json.ToString();
        }

        private static string StatsOrNull(LifeBenchStatistics.Summary stats) =>
            LifeBenchStatistics.SummaryOrNull(stats);

        private static string CellsPerSecond(LifeBenchStatistics.Summary stats, long cells) =>
            stats.Count == 0 || stats.MedianMs <= 0.0
                ? "null"
                : F(cells / (stats.MedianMs / 1000.0));

        /// <summary>The axes this run measured, as JSON strings.</summary>
        private static string PhaseList(bool frameScenariosOnly) =>
            frameScenariosOnly
                ? "\"frame-scenarios\""
                : string.Join(", ", Array.ConvertAll(AllPhases, phase => $"\"{phase}\""));

        /// <summary>The axes this run did not measure, as JSON strings.</summary>
        private static string SkippedPhaseList(bool frameScenariosOnly) =>
            frameScenariosOnly
                ? string.Join(", ", Array.ConvertAll(
                    Array.FindAll(AllPhases, phase => phase != "frame-scenarios"), phase => $"\"{phase}\""))
                : string.Empty;

        private static string PhasesNote(bool frameScenariosOnly) =>
            frameScenariosOnly
                ? "this run measured ONLY the cross-frame scenarios (-lifeBenchScenarios frame); every " +
                  "other axis is null and is named in phasesSkipped, because it was not measured in " +
                  "this run -- null here never means zero"
                : "this run measured every axis";
    }
}
