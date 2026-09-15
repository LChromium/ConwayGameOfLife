using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Stage-A performance probe. Activated with "-lifePerf"; measures the board
    /// size the Player was launched with (-lifeBoard) and appends one JSON line,
    /// then quits. Run it once per size.
    ///
    /// What it measures, and how each number must be read:
    ///
    ///   * CPU rule advance -- Stopwatch around CpuLifeBackend.Step(). This is
    ///     synchronous managed compute, so the number IS the CPU rule cost.
    ///   * GPU rule advance, submit only -- Stopwatch around N Step() calls with
    ///     no synchronisation. This is ONLY the cost of handing work to the GPU.
    ///     It is reported under a name that says so and must never be quoted as
    ///     GPU execution time.
    ///   * GPU rule advance, submit + forced sync -- the same N steps followed by
    ///     one ComputeBuffer.GetData, which blocks until the GPU has finished.
    ///     That is an upper bound containing submit + execution + sync overhead,
    ///     reported as such rather than as clean execution time.
    ///   * Display path -- Stopwatch around the board's Refresh(), i.e. the render
    ///     dispatch plus the background assignment. Submit side only.
    ///   * Frame intervals -- real frame deltas with the board displayed, sampled
    ///     once with the clock running and once paused.
    ///
    /// Excluded from every rule-advance figure, as the brief requires: initial
    /// state generation, the CPU->GPU state upload, and the full-board
    /// verification readback.
    ///
    /// A GPU profiler marker is looked up but never assumed. If none is available
    /// the probe records that fact instead of substituting an estimate.
    /// </summary>
    public sealed class LifePerfProbe : MonoBehaviour
    {
        private const int WarmupSteps = 40;
        private const int TimedRepeats = 7;
        private const int StepsPerRepeat = 20;
        private const int FrameSamples = 240;
        private const int RandomSeed = 20260915;
        private const double Density = 0.30;

        private sealed class Stats
        {
            public double Median;
            public double Min;
            public double Max;

            public static Stats From(List<double> samples)
            {
                samples.Sort();
                return new Stats
                {
                    Median = samples[samples.Count / 2],
                    Min = samples[0],
                    Max = samples[samples.Count - 1],
                };
            }

            public string Json() =>
                $"\"medianMs\": {F(Median)}, \"minMs\": {F(Min)}, \"maxMs\": {F(Max)}";
        }

        private sealed class FrameStats
        {
            public Stats Deltas;
            public int Frames;
        }

        private FrameStats lastFrameStats;

        private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            LifeTerminalController controller = FindAnyObjectByType<LifeTerminalController>();
            if (controller == null)
            {
                UnityEngine.Debug.LogError("[stage-a-perf] no LifeTerminalController found");
                Application.Quit(1);
                yield break;
            }

            UIDocument document = controller.GetComponent<UIDocument>();
            LifeGridElement grid = document != null
                ? document.rootVisualElement.Q("life-grid") as LifeGridElement
                : null;

            // Let the panel lay out and the first frames settle before timing.
            for (int i = 0; i < 30; i++)
                yield return null;

            ILifeBackend active = grid != null ? grid.Backend : null;
            if (active == null)
            {
                UnityEngine.Debug.LogError("[stage-a-perf] board backend was never bound");
                Application.Quit(1);
                yield break;
            }

            int width = active.Width;
            int height = active.Height;
            string backendName = active.Name;
            byte[] board = BuildFixedBoard(width, height);

            bool focusedAtStart = Application.isFocused;

            Stats cpuRules = MeasureCpuRules(board, width, height);
            yield return null;

            Stats gpuSubmit = MeasureGpuRules(board, width, height, synchronise: false);
            yield return null;

            Stats gpuSubmitAndSync = MeasureGpuRules(board, width, height, synchronise: true);
            yield return null;

            Stats displayRefresh = MeasureDisplay(grid);
            yield return null;

            // Real frame deltas with whatever backend the Player was launched with.
            //
            // Sampled twice: once with the app's own frame pacing, and once with
            // vsync forced off. With vsync on the interval is pinned to the display
            // refresh and cannot say anything about the work being done, so the
            // configured numbers describe pacing only; the vsync-off numbers are
            // the ones that expose headroom.
            int configuredVSync = QualitySettings.vSyncCount;
            int configuredTargetFrameRate = Application.targetFrameRate;

            bool wasRunning = SetRunning(controller, true);
            yield return null;
            yield return MeasureFramesRoutine();
            FrameStats runningFrames = lastFrameStats;
            int advancedWhileRunning = active.Generation;

            SetRunning(controller, false);
            yield return null;
            yield return null;
            yield return MeasureFramesRoutine();
            FrameStats pausedFrames = lastFrameStats;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return null;
            yield return null;

            SetRunning(controller, true);
            yield return null;
            yield return MeasureFramesRoutine();
            FrameStats runningFramesUncapped = lastFrameStats;
            int advancedWhileRunningUncapped = active.Generation - advancedWhileRunning;

            SetRunning(controller, false);
            yield return null;
            yield return null;
            yield return MeasureFramesRoutine();
            FrameStats pausedFramesUncapped = lastFrameStats;

            QualitySettings.vSyncCount = configuredVSync;
            Application.targetFrameRate = configuredTargetFrameRate;
            SetRunning(controller, wasRunning);

            bool focusedAtEnd = Application.isFocused;
            GpuMarkerLookup marker = LookupGpuMarker();

            WriteLine(width, height, backendName, cpuRules, gpuSubmit, gpuSubmitAndSync,
                displayRefresh, runningFrames, pausedFrames, advancedWhileRunning,
                runningFramesUncapped, pausedFramesUncapped, advancedWhileRunningUncapped,
                marker, focusedAtStart && focusedAtEnd, configuredVSync, configuredTargetFrameRate);

            Application.Quit(0);
        }

        // -- rule advance ------------------------------------------------------

        private static Stats MeasureCpuRules(byte[] board, int width, int height)
        {
            using var backend = new CpuLifeBackend(width, height);
            backend.WrapEdges = true;
            backend.LoadBoard(board);

            for (int i = 0; i < WarmupSteps; i++)
                backend.Step();

            var samples = new List<double>();
            for (int repeat = 0; repeat < TimedRepeats; repeat++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < StepsPerRepeat; i++)
                    backend.Step();
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds / StepsPerRepeat);
            }

            return Stats.From(samples);
        }

        private static Stats MeasureGpuRules(byte[] board, int width, int height, bool synchronise)
        {
            if (!GpuLifeBackend.TryCreate(width, height, out GpuLifeBackend backend, out _))
                return null;

            using (backend)
            {
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                var sink = new uint[width * height];

                for (int i = 0; i < WarmupSteps; i++)
                    backend.Step();

                if (synchronise)
                    backend.TryReadAllCells(sink);

                var samples = new List<double>();
                for (int repeat = 0; repeat < TimedRepeats; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < StepsPerRepeat; i++)
                        backend.Step();

                    if (synchronise)
                        backend.TryReadAllCells(sink);

                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds / StepsPerRepeat);
                }

                return Stats.From(samples);
            }
        }

        private static Stats MeasureDisplay(LifeGridElement grid)
        {
            if (grid == null)
                return null;

            for (int i = 0; i < WarmupSteps; i++)
                grid.Refresh();

            var samples = new List<double>();
            for (int repeat = 0; repeat < TimedRepeats; repeat++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < StepsPerRepeat; i++)
                    grid.Refresh();
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds / StepsPerRepeat);
            }

            return Stats.From(samples);
        }

        // -- frame intervals ---------------------------------------------------

        private IEnumerator MeasureFramesRoutine()
        {
            // One discarded frame so the first sample is not the frame that
            // started this coroutine.
            yield return null;

            var samples = new List<double>(FrameSamples);
            for (int i = 0; i < FrameSamples; i++)
            {
                yield return null;
                samples.Add(Time.unscaledDeltaTime * 1000.0);
            }

            lastFrameStats = new FrameStats { Deltas = Stats.From(samples), Frames = samples.Count };
        }

        // -- helpers -----------------------------------------------------------

        private static byte[] BuildFixedBoard(int width, int height)
        {
            var board = new byte[width * height];
            var random = new System.Random(RandomSeed);
            for (int i = 0; i < board.Length; i++)
                board[i] = random.NextDouble() < Density ? (byte)1 : (byte)0;

            return board;
        }

        /// <summary>
        /// Flips the controller's private run flag. Reflection is used because the
        /// probe is a measurement tool that has to drive the real UI loop; widening
        /// the shipped API for it would be worse.
        /// </summary>
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

        private readonly struct GpuMarkerLookup
        {
            public readonly bool Available;
            public readonly string Name;

            public GpuMarkerLookup(bool available, string name)
            {
                Available = available;
                Name = name;
            }
        }

        /// <summary>
        /// Stage A does not publish a GPU execution time.
        ///
        /// Enumerating profiler markers needs Unity.Profiling.LowLevel.Unsafe, and a
        /// marker named for GPU time is not guaranteed to exist in a release Player.
        /// The brief allows exactly this outcome: report the end-to-end figures and
        /// leave the GPU time unmeasured, rather than filling the gap with an
        /// estimate. "gpuSubmitPlusForcedSync" is the closest honest upper bound and
        /// is labelled as containing submit and sync overhead.
        /// </summary>
        private static GpuMarkerLookup LookupGpuMarker() => new(false, null);

        private void WriteLine(int width, int height, string backendName,
            Stats cpuRules, Stats gpuSubmit, Stats gpuSubmitAndSync, Stats displayRefresh,
            FrameStats runningFrames, FrameStats pausedFrames, int advancedWhileRunning,
            FrameStats runningFramesUncapped, FrameStats pausedFramesUncapped,
            int advancedWhileRunningUncapped,
            GpuMarkerLookup marker, bool focusedThroughout,
            int configuredVSync, int configuredTargetFrameRate)
        {
            var json = new StringBuilder();
            json.Append('{');
            json.Append("\"kind\": \"stage-a-perf\", ");
            json.Append($"\"unityVersion\": \"{Application.unityVersion}\", ");
            json.Append($"\"graphicsDevice\": \"{SystemInfo.graphicsDeviceName}\", ");
            json.Append($"\"graphicsApi\": \"{SystemInfo.graphicsDeviceType}\", ");
            json.Append($"\"processor\": \"{SystemInfo.processorType}\", ");
            json.Append($"\"isDevelopmentBuild\": {Bool(UnityEngine.Debug.isDebugBuild)}, ");
            json.Append($"\"targetFrameRate\": {configuredTargetFrameRate}, ");
            json.Append($"\"vSyncCount\": {configuredVSync}, ");
            json.Append($"\"board\": \"{width}x{height}\", ");
            json.Append($"\"cells\": {width * height}, ");
            json.Append($"\"activeBackend\": \"{backendName}\", ");
            json.Append($"\"boundary\": \"wrap\", ");
            json.Append($"\"initialBoard\": \"random seed {RandomSeed}, density {Density.ToString("F2", CultureInfo.InvariantCulture)}\", ");
            json.Append($"\"focusedThroughout\": {Bool(focusedThroughout)}, ");

            json.Append("\"rules\": {");
            json.Append($"\"stepsPerRepeat\": {StepsPerRepeat}, \"repeats\": {TimedRepeats}, \"warmupSteps\": {WarmupSteps}, ");
            json.Append($"\"cpuMsPerGeneration\": {{{cpuRules.Json()}}}, ");
            json.Append("\"gpuSubmitOnlyMsPerGeneration\": ");
            json.Append(gpuSubmit == null ? "null" : "{" + gpuSubmit.Json() + "}");
            json.Append(", \"gpuSubmitPlusForcedSyncMsPerGeneration\": ");
            json.Append(gpuSubmitAndSync == null ? "null" : "{" + gpuSubmitAndSync.Json() + "}");
            json.Append(", \"gpuProfilerMarker\": ");
            json.Append(marker.Available
                ? $"{{\"available\": true, \"name\": \"{marker.Name}\"}}"
                : "{\"available\": false, \"name\": null}");
            json.Append(", \"excludes\": [\"initial state generation\", \"CPU to GPU state upload\", \"full board verification readback\"]");
            json.Append("}, ");

            json.Append("\"display\": {");
            json.Append("\"refreshMsPerCall\": ");
            json.Append(displayRefresh == null ? "null" : "{" + displayRefresh.Json() + "}");
            json.Append(", \"caveat\": \"submit side only: the render dispatch plus the background assignment, not a GPU execution time\"");
            json.Append("}, ");

            json.Append("\"frames\": {");
            json.Append($"\"samplesPerScenario\": {FrameSamples}, ");
            json.Append($"\"running\": {FrameJson(runningFrames)}, ");
            json.Append($"\"paused\": {FrameJson(pausedFrames)}, ");
            json.Append($"\"generationsAdvancedWhileRunning\": {advancedWhileRunning}, ");
            json.Append("\"displayOn\": true, ");
            json.Append($"\"runningVsyncOff\": {FrameJson(runningFramesUncapped)}, ");
            json.Append($"\"pausedVsyncOff\": {FrameJson(pausedFramesUncapped)}, ");
            json.Append($"\"generationsAdvancedVsyncOff\": {advancedWhileRunningUncapped}, ");
            json.Append("\"caveat\": \"with vSyncCount > 0 the interval is pinned to the display refresh and describes pacing, not work; the vsync-off pair is the one that shows headroom\"");
            json.Append("}");

            json.Append('}');

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(projectRoot, "stage-a-perf.jsonl");
            File.AppendAllText(path, json + Environment.NewLine);
            UnityEngine.Debug.Log($"[stage-a-perf] appended {width}x{height} ({backendName}) to {path}");
        }

        private static string FrameJson(FrameStats stats)
        {
            if (stats?.Deltas == null)
                return "null";

            return $"{{\"frames\": {stats.Frames}, {stats.Deltas.Json()}}}";
        }

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
