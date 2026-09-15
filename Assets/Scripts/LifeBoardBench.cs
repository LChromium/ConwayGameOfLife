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
    /// "-lifeBoard WxH"; measures the board the Player was launched with, appends one
    /// JSON line to <c>stage-c-bench.jsonl</c> and quits. One process per board size,
    /// so each size gets a fresh allocator and a fresh peak, which is the only way a
    /// memory figure for a size means anything.
    ///
    /// <para><b>Five axes, recorded separately and never summed into one number.</b>
    /// Stage A refused to attribute a frame to its parts without per-part data; this
    /// probe is that data, so blurring it back together would waste the exercise.</para>
    ///
    /// <list type="bullet">
    /// <item><b>generate</b> -- <see cref="LifeNoiseSeeding.Generate"/> for fBm and for
    /// uniform, Stopwatch around the call. Pure managed compute, so the number IS the
    /// CPU generation cost. Excludes upload, evolution and display.</item>
    /// <item><b>upload</b> -- <c>ILifeBackend.LoadBoard</c>. On the GPU that is the
    /// staging copy plus <c>ComputeBuffer.SetData</c>, which returns once the data is
    /// handed over: it is NOT a transfer-complete time. On the CPU it is the app's real
    /// load path (a call per live cell, so population stays incremental).</item>
    /// <item><b>evolution</b> -- CPU steps, GPU steps submitted without synchronisation,
    /// and GPU steps followed by a full readback. The last one is an upper bound that
    /// contains submit plus execution plus sync overhead, and is labelled as such.
    /// <b>No CPU/GPU speed ratio is derived from any of these.</b></item>
    /// <item><b>display</b> -- the repaint path (<c>LifeGridElement.Refresh</c>, i.e. the
    /// render dispatch plus the background assignment), the CPU backend's readback and
    /// upload for a repaint (the GPU backend has no such cost: the kernel reads the live
    /// buffer), real frame deltas with vsync off, frame timings from
    /// <see cref="FrameTimingManager"/> where the platform provides them, and the
    /// viewport facts -- how much of the board is actually on screen.</item>
    /// <item><b>memory</b> -- measured deltas for allocating one more board at this size
    /// (managed heap and graphics driver separately) plus the computed per-buffer
    /// breakdown, so the numbers can be checked against the source.</item>
    /// </list>
    ///
    /// <para>Deliberately self-contained: it does not share its twenty-line statistics
    /// helper with the archived stage-A probe, because editing that probe would
    /// invalidate stage A's published numbers.</para>
    /// </summary>
    public sealed class LifeBoardBench : MonoBehaviour
    {
        /// <summary>Board the stage-B reproduction command uses, so generation figures are comparable.</summary>
        private static readonly LifeNoiseParameters FbmParameters =
            new(LifeSeedingMode.Fbm, 20260915, 0.32f, 60f, 10f, 0.7f);

        /// <summary>
        /// True when this player reported frame timings at all. Recorded per line, because a
        /// reader has to be able to tell which configuration a GPU frame time came from
        /// without trusting a filename.
        /// </summary>
        private bool frameTimingStatsReported;

        private static readonly LifeNoiseParameters UniformParameters =
            new(LifeSeedingMode.Uniform, 20260915, 0.32f, 48f, 0f, 0f);

        private const int RandomSeed = 20260915;
        private const double Density = 0.30;

        private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>
        /// Wall-clock cost of one phase, logged as it finishes. A benchmark that only
        /// reports its measurements cannot say where its own time went, and a phase that
        /// suddenly takes minutes is a finding rather than a mystery.
        ///
        /// <para>Focus is logged with it on purpose. A standalone player stops running when
        /// its window loses focus (<c>Application.runInBackground</c> is false by default),
        /// and a phase that straddles such a pause reports a wall-clock time of minutes
        /// while every measurement inside it stays fast. That exact shape was observed
        /// here, and it is recorded rather than silently averaged away.</para>
        /// </summary>
        private static void Phase(string name, ref double startedAt)
        {
            double now = Time.realtimeSinceStartup;
            Debug.Log($"[stage-c-bench] phase {name}: {now - startedAt:F3}s focused={Application.isFocused}");
            startedAt = now;
        }

        // -- results carried to the writer -------------------------------------

        private sealed class Stats
        {
            public double Median;
            public double Min;
            public double Max;
            public int Count;
            public double Mean;

            public static Stats From(List<double> samples)
            {
                samples.Sort();
                double total = 0.0;
                foreach (double sample in samples)
                    total += sample;

                return new Stats
                {
                    Count = samples.Count,
                    Median = samples[samples.Count / 2],
                    Min = samples[0],
                    Max = samples[samples.Count - 1],
                    Mean = total / samples.Count,
                };
            }

            public string Json() =>
                $"\"samples\": {Count}, \"medianMs\": {F(Median)}, \"meanMs\": {F(Mean)}, " +
                $"\"minMs\": {F(Min)}, \"maxMs\": {F(Max)}";
        }

        private sealed class Generation
        {
            public Stats Stats;
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
            bool configuredRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            bool focusedAtStart = Application.isFocused;

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
            gridRef = grid;
            Debug.Log($"[stage-c-bench] {width}x{height} ({cells} cells), active backend {active.Name}");

            // The starting board is always a plain fixed random one: generation is
            // measured separately below, and every other axis must be comparable across
            // board sizes without a noise field in the picture.
            byte[] board = BuildFixedBoard(cells);

            Generation fbm = MeasureGeneration(FbmParameters, width, height);
            Phase("generate-fbm", ref phaseStartedAt);
            yield return null;

            Generation uniform = MeasureGeneration(UniformParameters, width, height);
            Phase("generate-uniform", ref phaseStartedAt);
            yield return null;

            Stats cpuLoad = MeasureUploadCpu(board, width, height);
            Phase("upload-cpu", ref phaseStartedAt);
            yield return null;

            Stats gpuLoad = MeasureUploadGpu(board, width, height);
            Phase("upload-gpu", ref phaseStartedAt);
            yield return null;

            Stats cpuRules = MeasureCpuRules(board, width, height, cells);
            Phase("evolution-cpu", ref phaseStartedAt);
            yield return null;

            Stats gpuSubmit = MeasureGpuRules(board, width, height, cells, synchronise: false);
            Phase("evolution-gpu-submit", ref phaseStartedAt);
            yield return null;

            Stats gpuSynced = MeasureGpuRules(board, width, height, cells, synchronise: true);
            Phase("evolution-gpu-sync", ref phaseStartedAt);
            yield return null;

            DisplayFacts display = MeasureDisplay(grid, board, width, height, cells);
            Phase("display-refresh", ref phaseStartedAt);
            yield return null;

            // Frame deltas with vsync off, so the interval describes work rather than the
            // display refresh. Sampled paused and running: the difference is the cost of a
            // repaint per generation.
            int configuredVSync = QualitySettings.vSyncCount;
            int configuredTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 5; i++)
                yield return null;

            bool wasRunning = SetRunning(controller, false);
            yield return MeasureFrames(active, cells, Repaint.None, budgetSeconds: 0.4f, minGenerations: 0);
            Stats idleFrames = lastFrames;
            float idleFps = lastFramesPerSecond;

            yield return MeasureFrames(active, cells, Repaint.EveryFrame, budgetSeconds: 0.4f, minGenerations: 0);
            Stats repaintFrames = lastFrames;
            float repaintFps = lastFramesPerSecond;
            Phase("display-frames-idle-and-repaint", ref phaseStartedAt);

            // The running case has to actually evolve, or the interval says nothing about
            // the app: the clock goes to the UI's maximum rate and the window stays open
            // until at least five generations have gone by.
            int requestedGenerationsPerSecond = SetSpeed(controller, 20);
            SetRunning(controller, true);
            yield return null;
            yield return MeasureFrames(active, cells, Repaint.EveryFrame, budgetSeconds: 2.0f, minGenerations: 5);
            Stats runningFrames = lastFrames;
            float runningFps = lastFramesPerSecond;
            int advanced = lastFrameGenerationDelta;
            float achievedGenerationsPerSecond = lastGenerationsPerSecond;
            Phase("display-frames-running", ref phaseStartedAt);

            SetRunning(controller, false);
            yield return FrameTimings(120);
            FrameStats timings = lastTimings;
            Phase("display-frame-timings", ref phaseStartedAt);

            QualitySettings.vSyncCount = configuredVSync;
            Application.targetFrameRate = configuredTargetFrameRate;
            SetRunning(controller, wasRunning);

            MemoryFacts memory = MeasureMemory(width, height, board);
            Phase("memory", ref phaseStartedAt);
            yield return null;

            bool focusedAtEnd = Application.isFocused;
            Application.runInBackground = configuredRunInBackground;

            WriteLine(width, height, cells, active.Name, fbm, uniform, cpuLoad, gpuLoad,
                cpuRules, gpuSubmit, gpuSynced, display, idleFrames, idleFps, repaintFrames,
                repaintFps, runningFrames, runningFps, advanced, achievedGenerationsPerSecond,
                requestedGenerationsPerSecond, timings, memory, configuredVSync,
                configuredTargetFrameRate, focusedAtStart && focusedAtEnd,
                Time.realtimeSinceStartup - startedAt);

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
                Stats = Stats.From(samples),
                Alive = alive,
                RealisedDensity = (double)alive / destination.Length,
                Parameters = parameters.ToString(),
            };
        }

        // -- 2. upload ---------------------------------------------------------

        private static Stats MeasureUploadCpu(byte[] board, int width, int height)
        {
            using var backend = new CpuLifeBackend(width, height);
            return MeasureUpload(backend, board, repeats: 5);
        }

        private static Stats MeasureUploadGpu(byte[] board, int width, int height)
        {
            if (!GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend backend, out string error))
            {
                Debug.LogWarning($"[stage-c-bench] GPU upload not measured: {error}");
                return null;
            }

            using (backend)
            {
                return MeasureUpload(backend, board, repeats: 5);
            }
        }

        private static Stats MeasureUpload(ILifeBackend backend, byte[] board, int repeats)
        {
            var samples = new List<double>(repeats);
            for (int i = 0; i < repeats; i++)
            {
                var watch = Stopwatch.StartNew();
                backend.LoadBoard(board);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            return Stats.From(samples);
        }

        // -- 3. evolution ------------------------------------------------------

        private static Stats MeasureCpuRules(byte[] board, int width, int height, int cells)
        {
            using var backend = new CpuLifeBackend(width, height);
            backend.WrapEdges = true;
            backend.LoadBoard(board);

            for (int i = 0; i < WarmupFor(cells); i++)
                backend.Step();

            int repeats = RepeatsFor(cells);
            int steps = StepsFor(cells);
            var samples = new List<double>(repeats);

            for (int repeat = 0; repeat < repeats; repeat++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < steps; i++)
                    backend.Step();
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds / steps);
            }

            return Stats.From(samples);
        }

        private static Stats MeasureGpuRules(byte[] board, int width, int height, int cells, bool synchronise)
        {
            if (!GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend backend, out string error))
            {
                Debug.LogWarning($"[stage-c-bench] GPU evolution not measured: {error}");
                return null;
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
                int steps = StepsFor(cells);
                var samples = new List<double>(repeats);

                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < steps; i++)
                        backend.Step();

                    if (synchronise)
                        backend.TryReadAllCells(sink);

                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds / steps);
                }

                return Stats.From(samples);
            }
        }

        // -- 4. display --------------------------------------------------------

        private sealed class DisplayFacts
        {
            public Stats Refresh;
            public Stats CpuRepaintUpload;
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

            // The repaint path exactly as the frame loop calls it.
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

                facts.Refresh = Stats.From(samples);
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

            // The CPU backend's repaint cost: a full-board readback plus the upload into the
            // display buffer. The GPU backend has none of this -- the render kernel reads its
            // live buffer -- so it is measured on a CPU board of the same size whatever the
            // Player was launched with, rather than reported as zero and forgotten.
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

                facts.CpuRepaintUpload = Stats.From(samples);
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

        private Stats lastFrames;
        private float lastFramesPerSecond;
        private int lastFrameGenerationDelta;
        private float lastGenerationsPerSecond;

        private enum Repaint
        {
            None,
            EveryFrame,
        }

        /// <summary>
        /// Samples real frame deltas for one scenario. The window is bounded by a wall-clock
        /// budget, a frame cap and -- for the running case -- a minimum number of
        /// generations: without that last bound a fast board fits its whole frame budget
        /// into a few milliseconds and the sample reports a frame interval for a board that
        /// never advanced.
        /// </summary>
        private IEnumerator MeasureFrames(
            ILifeBackend active, int cells, Repaint repaint, float budgetSeconds, int minGenerations)
        {
            int maxFrames = minGenerations > 0 ? 200000 : FrameSamplesFor(cells);
            int startGeneration = active.Generation;
            double startedAt = Time.realtimeSinceStartup;

            // One discarded frame so the first sample is not the frame that started this.
            yield return null;

            var samples = new List<double>(Math.Min(maxFrames, 8192));
            for (int i = 0; i < maxFrames; i++)
            {
                if (repaint == Repaint.EveryFrame)
                    gridRef?.MarkBoardDirty();

                yield return null;
                samples.Add(Time.unscaledDeltaTime * 1000.0);

                bool windowOver = Time.realtimeSinceStartup - startedAt >= budgetSeconds && samples.Count >= 10;
                bool enoughGenerations = active.Generation - startGeneration >= minGenerations;
                if (windowOver && enoughGenerations)
                    break;
            }

            lastFrames = Stats.From(samples);
            lastFrameGenerationDelta = active.Generation - startGeneration;

            double elapsed = Time.realtimeSinceStartup - startedAt;
            lastFramesPerSecond = elapsed > 0.0 ? (float)(samples.Count / elapsed) : 0f;
            lastGenerationsPerSecond = elapsed > 0.0 ? (float)(lastFrameGenerationDelta / elapsed) : 0f;
        }

        private LifeGridElement gridRef;

        private sealed class FrameStats
        {
            public bool Available;
            public Stats Cpu;
            public Stats Gpu;
            public string Reason;
        }

        private FrameStats lastTimings;

        /// <summary>
        /// Real GPU frame times, where the platform provides them. A development build
        /// with frame timing enabled reports them; anything else returns zeros, and the
        /// probe records that instead of substituting the submit-side number.
        /// </summary>
        private IEnumerator FrameTimings(int frames)
        {
            var result = new FrameStats();
            var timings = new FrameTiming[frames];
            var cpu = new List<double>();
            var gpu = new List<double>();

            for (int i = 0; i < frames; i++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
            }

            uint captured = FrameTimingManager.GetLatestTimings((uint)frames, timings);
            for (int i = 0; i < captured && i < timings.Length; i++)
            {
                if (timings[i].cpuFrameTime > 0.0)
                    cpu.Add(timings[i].cpuFrameTime);

                if (timings[i].gpuFrameTime > 0.0)
                    gpu.Add(timings[i].gpuFrameTime);
            }

            if (cpu.Count > 0)
                result.Cpu = Stats.From(cpu);

            if (gpu.Count > 0)
                result.Gpu = Stats.From(gpu);

            result.Available = cpu.Count > 0 || gpu.Count > 0;
            result.Reason = result.Available
                ? null
                : "FrameTimingManager returned no frame timings (needs a development build with frame timing enabled)";

            lastTimings = result;
            frameTimingStatsReported = result.Available;
        }

        // -- 5. memory ---------------------------------------------------------

        private sealed class MemoryFacts
        {
            public long ManagedTotalMB;
            public long AllocatedTotalMB;
            public long ReservedTotalMB;
            public long GraphicsDriverMB;
            public long SystemMemoryMB;
            public long GraphicsMemoryMB;

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

            public long CpuStateBytes;
            public long GpuComputeBufferBytes;
            public long GpuStagingBytes;
            public long RendererUploadBufferBytes;
            public long RendererUploadScratchBytes;
            public long BenchBoardBytes;
            public long BenchScratchBytes;
            public long ComputedTotalBytes;
        }

        private static MemoryFacts MeasureMemory(int width, int height, byte[] board)
        {
            var facts = new MemoryFacts
            {
                SystemMemoryMB = SystemInfo.systemMemorySize,
                GraphicsMemoryMB = SystemInfo.graphicsMemorySize,
            };

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
            facts.GpuComputeBufferBytes = cells * 4 * 2; // front + back
            facts.GpuStagingBytes = cells * 4 * 2;       // uploadScratch + readbackScratch
            facts.RendererUploadBufferBytes = cells * 4; // display upload ComputeBuffer
            facts.RendererUploadScratchBytes = cells * 4;
            facts.BenchBoardBytes = cells;               // the byte[] this probe built
            facts.BenchScratchBytes = cells * 4;         // readback sink
            facts.ComputedTotalBytes = facts.CpuStateBytes + facts.GpuComputeBufferBytes +
                                       facts.GpuStagingBytes + facts.RendererUploadBufferBytes +
                                       facts.RendererUploadScratchBytes + facts.BenchBoardBytes +
                                       facts.BenchScratchBytes;

            return facts;
        }

        private static long ToMB(long bytes) => bytes / (1024 * 1024);

        // -- sizing helpers ----------------------------------------------------

        /// <summary>Repeats per measurement, scaled down as the board grows.</summary>
        private static int RepeatsFor(int cells) => cells <= 1 << 20 ? 5 : cells <= 1 << 22 ? 3 : 2;

        private static int WarmupFor(int cells) => cells <= 1 << 20 ? 20 : 2;

        private static int StepsFor(int cells) => cells <= 1 << 20 ? 20 : cells <= 1 << 22 ? 5 : 2;

        /// <summary>Frames per sampling window, scaled down as a frame gets more expensive.</summary>
        private static int FrameSamplesFor(int cells) => cells <= 1 << 22 ? 120 : 40;

        private static byte[] BuildFixedBoard(int cells)
        {
            var board = new byte[cells];
            var random = new System.Random(RandomSeed);
            for (int i = 0; i < cells; i++)
                board[i] = random.NextDouble() < Density ? (byte)1 : (byte)0;

            return board;
        }

        private static bool SetRunning(LifeTerminalController controller, bool value)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "running", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return false;

            bool previous = (bool)field.GetValue(controller);
            field.SetValue(controller, value);
            return previous;
        }

        /// <summary>
        /// Drives the real speed slider, so the running sample uses the rate the interface
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

        private void WriteLine(int width, int height, int cells, string backendName,
            Generation fbm, Generation uniform, Stats cpuLoad, Stats gpuLoad,
            Stats cpuRules, Stats gpuSubmit, Stats gpuSynced, DisplayFacts display,
            Stats idleFrames, float idleFps, Stats repaintFrames, float repaintFps,
            Stats runningFrames, float runningFps, int advanced, float achievedGenerationsPerSecond,
            int requestedGenerationsPerSecond, FrameStats timings, MemoryFacts memory,
            int configuredVSync, int configuredTargetFrameRate, bool focusedThroughout,
            double elapsedSeconds)
        {
            var json = new StringBuilder();
            json.Append('{');
            json.Append("\"kind\": \"stage-c-bench\", ");
            json.Append($"\"unityVersion\": \"{Application.unityVersion}\", ");
            json.Append($"\"graphicsDevice\": \"{SystemInfo.graphicsDeviceName}\", ");
            json.Append($"\"graphicsApi\": \"{SystemInfo.graphicsDeviceType}\", ");
            json.Append($"\"processor\": \"{SystemInfo.processorType}\", ");
            json.Append($"\"processorCount\": {SystemInfo.processorCount}, ");
            json.Append($"\"systemMemoryMB\": {memory.SystemMemoryMB}, ");
            json.Append($"\"graphicsMemoryMB\": {memory.GraphicsMemoryMB}, ");
            json.Append($"\"isDevelopmentBuild\": {Bool(Debug.isDebugBuild)}, ");
            json.Append($"\"buildGuid\": \"{Application.buildGUID}\", ");
            json.Append($"\"dataPath\": \"{Application.dataPath.Replace('\\', '/')}\", ");
            json.Append($"\"frameTimingStatsRequested\": {Bool(frameTimingStatsReported)}, ");
            json.Append($"\"runInBackgroundForced\": true, ");
            json.Append($"\"focusedThroughout\": {Bool(focusedThroughout)}, ");
            json.Append($"\"targetFrameRate\": {configuredTargetFrameRate}, ");
            json.Append($"\"vSyncCount\": {configuredVSync}, ");
            json.Append($"\"board\": \"{width}x{height}\", ");
            json.Append($"\"width\": {width}, \"height\": {height}, \"cells\": {cells}, ");
            json.Append($"\"activeBackend\": \"{backendName}\", ");
            json.Append("\"boundary\": \"wrap\", ");
            json.Append($"\"initialBoard\": \"random seed {RandomSeed}, density {F(Density)}\", ");
            json.Append($"\"elapsedSeconds\": {F(elapsedSeconds)}, ");

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
            json.Append($"\"gpuSubmitPlusForcedSyncMsPerGeneration\": {StatsOrNull(gpuSynced)}, ");
            json.Append($"\"cpuCellsPerSecond\": {CellsPerSecond(cpuRules, cells)}, ");
            json.Append($"\"stepsPerRepeat\": {StepsFor(cells)}, ");
            json.Append("\"boundary\": \"wrap\", ");
            json.Append("\"excludes\": [\"initial state generation\", \"CPU to GPU state upload\", \"display\", \"full board verification readback inside the submit-only figure\"], ");
            json.Append("\"caveat\": \"the submit-only figure is the cost of handing work to the GPU and must never be quoted as GPU execution time; the submit+sync figure is an upper bound containing execution and sync overhead; no CPU/GPU ratio is derived\"");
            json.Append("}, ");

            json.Append("\"display\": {");
            json.Append($"\"refreshMsPerCall\": {StatsOrNull(display.Refresh)}, ");
            json.Append($"\"cpuBackendRepaintReadbackAndUploadMs\": {StatsOrNull(display.CpuRepaintUpload)}, ");
            json.Append($"\"rendererPath\": \"{display.RendererPath}\", ");
            json.Append($"\"viewport\": \"{display.ViewportWidth}x{display.ViewportHeight}\", ");
            json.Append($"\"cellPixels\": {display.CellPixels}, ");
            json.Append($"\"visibleCells\": \"{display.VisibleCellsX}x{display.VisibleCellsY}\", ");
            json.Append($"\"visibleFractionOfBoard\": {F(display.VisibleFraction)}, ");
            json.Append($"\"boardFitsViewport\": {Bool(display.BoardFitsViewport)}, ");
            json.Append($"\"framesPausedVsyncOff\": {StatsOrNull(idleFrames)}, ");
            json.Append($"\"framesPausedVsyncOffPerSecond\": {F(idleFps)}, ");
            json.Append($"\"framesPausedRepaintEveryFrameVsyncOff\": {StatsOrNull(repaintFrames)}, ");
            json.Append($"\"framesPausedRepaintEveryFrameVsyncOffPerSecond\": {F(repaintFps)}, ");
            json.Append($"\"framesRunningVsyncOff\": {StatsOrNull(runningFrames)}, ");
            json.Append($"\"framesRunningVsyncOffPerSecond\": {F(runningFps)}, ");
            json.Append($"\"requestedGenerationsPerSecond\": {requestedGenerationsPerSecond}, ");
            json.Append($"\"achievedGenerationsPerSecond\": {F(achievedGenerationsPerSecond)}, ");
            json.Append($"\"generationsAdvancedWhileRunning\": {advanced}, ");
            json.Append($"\"gpuFrameTimeMs\": {StatsOrNull(timings?.Gpu)}, ");
            json.Append($"\"cpuFrameTimeMs\": {StatsOrNull(timings?.Cpu)}, ");
            json.Append($"\"frameTimingUnavailableReason\": {(timings != null && !timings.Available ? "\"" + timings.Reason + "\"" : "null")}, ");
            json.Append("\"caveat\": \"refreshMs is submit side only (dispatch + background assignment); the three frame scenarios are all vsync off and uncapped, so the interval describes work rather than pacing, and the running one reports how many generations it actually advanced\"");
            json.Append("}, ");

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
            json.Append("\"computedBytes\": {");
            json.Append($"\"cpuState\": {memory.CpuStateBytes}, ");
            json.Append($"\"gpuComputeBuffers\": {memory.GpuComputeBufferBytes}, ");
            json.Append($"\"gpuStagingArrays\": {memory.GpuStagingBytes}, ");
            json.Append($"\"rendererUploadBuffer\": {memory.RendererUploadBufferBytes}, ");
            json.Append($"\"rendererUploadScratch\": {memory.RendererUploadScratchBytes}, ");
            json.Append($"\"benchBoard\": {memory.BenchBoardBytes}, ");
            json.Append($"\"benchScratch\": {memory.BenchScratchBytes}, ");
            json.Append($"\"total\": {memory.ComputedTotalBytes}");
            json.Append("}, ");
            json.Append("\"caveat\": \"the deltas are one more board at this size allocated and released inside this process; the computed block is what the source allocates, so the two can be checked against each other\"");
            json.Append("}, ");

            json.Append($"\"utc\": \"{DateTime.UtcNow:O}\"");
            json.Append('}');

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(projectRoot, "stage-c-bench.jsonl");
            File.AppendAllText(path, json + Environment.NewLine);
            Debug.Log($"[stage-c-bench] appended {width}x{height} ({backendName}) to {path}");
            Debug.Log($"[stage-c-bench] {json}");
        }

        private static string GenerationJson(Generation generation) =>
            generation == null
                ? "null"
                : $"{{{generation.Stats.Json()}, \"realisedDensity\": {F(generation.RealisedDensity)}, " +
                  $"\"alive\": {generation.Alive}, \"parameters\": \"{generation.Parameters}\"}}";

        private static string StatsOrNull(Stats stats) =>
            stats == null ? "null" : "{" + stats.Json() + "}";

        private static string CellsPerSecond(Stats stats, long cells) =>
            stats == null || stats.Median <= 0.0
                ? "null"
                : F(cells / (stats.Median / 1000.0));
    }
}
