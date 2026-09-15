using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife
{
    public sealed class LifeTerminalController : MonoBehaviour
    {
        private const int DefaultGridWidth = 256;
        private const int DefaultGridHeight = 256;

        /// <summary>
        /// Panel width below which the workspace stacks the archive under the grid, in panel units.
        ///
        /// <para><b>Why 1280, argued from content rather than from the formula.</b>
        /// The two-column layout needs the display plus the archive plus their margin. In the wide
        /// layout the archive is a fixed 236 units and the display takes the rest, so at a 1280-wide
        /// panel the display gets roughly 1000 - comfortable for a 96-cell-wide grid. Below that the
        /// grid would start losing cell size faster than the archive's fixed width can justify, so
        /// the stacked layout (which gives the grid the full panel width) is the better trade.</para>
        ///
        /// <para><b>How to compute the panel width.</b> With <c>ScaleWithScreenSize</c> and
        /// MatchWidthOrHeight at 0.5, the fit scale is <c>(W/1600 + H/900) / 2</c>, so with aspect
        /// ratio <c>a = W/H</c>:
        /// <code>panelW = W / scale = 2880a / (0.9a + 1.6)</code>
        /// (The denominator was written as <c>1.8a + 1</c> here for several rounds. The example
        /// figures below were computed with the correct form, and <c>PanelScreenFit</c> --
        /// which the layout probe cross-checks against a running Player -- has always used it;
        /// only this line was wrong.)
        /// This depends <b>only on the aspect ratio, not on the window's absolute size</b>
        /// (400x400 and 4000x4000 both give 1152; 600x1000 gives 807.5), and it is
        /// <b>unbounded below</b> - a narrow enough window drives it toward zero. Earlier comments
        /// here claimed the width depended only on window area and had a floor of 1152; both were
        /// wrong, and the portrait capture disproved them.</para>
        /// </summary>
        private const float DesignWidth = 1280f;

        private const int ReferenceWidth = 1600;
        private const int ReferenceHeight = 900;
        private const float ReferenceMatch = 0.5f;

        private int gridWidth = DefaultGridWidth;
        private int gridHeight = DefaultGridHeight;

        // Stage A: the terminal owns backend selection, run state and commands.
        // Evolution lives in the backend, presentation lives in LifeGridElement.
        //
        // Stage D: the CPU path computes on a worker (LifeAsyncCpuBackend) so that a 650 ms
        // generation at 4096x4096 no longer happens inside a frame. The GPU backend stays
        // synchronous -- it is fast, and its board never round-trips through the main thread.
        private LifeAsyncCpuBackend cpuBackend;
        private GpuLifeBackend gpuBackend;
        private ILifeBackend backend;
        private LifeBoardRenderer renderer;
        private bool gpuAvailable;
        private string gpuUnavailableReason;
        private bool useGpu = true;
        private bool wrapEdges;

        // The board this experiment started from. "Reset" restores exactly this;
        // it never re-rolls a random board.
        private byte[] initialState;
        private string initialLabel = string.Empty;

        /// <summary>Stage-B seeding state. Editing it only ever changes a candidate.</summary>
        public LifeSeedingSession Seeding { get; private set; }

        private LifeGridElement grid;
        private Label generationLabel;
        private Label populationLabel;
        private Label stateLabel;
        private Label statusLabel;
        private Label sampleLabel;
        private Label fieldLabel;
        private Button playButton;
        private SliderInt speedSlider;
        private Button[] presetButtons;
        private int selectedPattern = 2;
        private bool running;
        private float accumulator;
        private int geometryLogs;

        // Clock overload protection. See Update for why there are two caps and what they mean.
        private const int MaxGenerationsPerFrame = 4;
        private const double GenerationBudgetMilliseconds = 6.0;

        /// <summary>
        /// Length of one rate-reporting window, in seconds. The window is TUMBLING, not sliding:
        /// when it closes, the rate is published and both counters restart from zero, so the
        /// figure describes that one segment rather than a trailing average.
        /// </summary>
        private const double RateWindowSeconds = 0.5;

        private int rateWindowGenerations;
        private double rateWindowSeconds;
        private float achievedGenerationsPerSecond;
        private bool achievedRateMeasured;
        private bool clockOverloaded;

        /// <summary>
        /// A "▸ 单步" has been asked for and its generation has not been taken over yet. A flag,
        /// not a queue: pressing the button again while one generation is in flight cannot
        /// accumulate a second one.
        /// </summary>
        private bool singleStepOutstanding;

        /// <summary>The worker failure already shown, so it is reported once and not every frame.</summary>
        private string asyncFailureLogged;

        /// <summary>
        /// Generations retired during the previous frame. Held back until this frame's delta
        /// arrives, so a window divides the generations of a set of frames by the time those
        /// same frames took. See <see cref="TrackAchievedRate"/>.
        /// </summary>
        private int generationsRetiredLastFrame;

        /// <summary>
        /// Generations the backend actually retired per second, over the last CLOSED rate
        /// window. With the clock capped this can sit below the slider's value, and the
        /// interface says so rather than pretending the requested rate was achieved.
        ///
        /// <para>Zero until a window has closed: check <see cref="AchievedRateMeasured"/>
        /// before reading zero as a measurement, because zero also means "nothing has been
        /// measured yet".</para>
        /// </summary>
        public float AchievedGenerationsPerSecond => achievedGenerationsPerSecond;

        /// <summary>
        /// True once at least one rate window has closed since the clock last started,
        /// stopped or changed backend. Until then there is no measured rate to report.
        /// </summary>
        public bool AchievedRateMeasured => achievedRateMeasured;

        /// <summary>True while the clock is discarding catch-up debt because it cannot keep up.</summary>
        public bool ClockOverloaded => clockOverloaded;

        // Stage-B seeding controls.
        private DropdownField seedingModeField;
        private IntegerField seedField;
        private Slider densitySlider;
        private Slider scaleSlider;
        private Slider warpSlider;
        private Slider clusterSlider;
        private Label seedingDensityLabel;
        private Button seedingApplyButton;
        private Button seedingCancelButton;

        // Seeding edits are debounced, then generated off the main thread. The debounce
        // only decides when the replacement computation is ASKED for; invalidating what is
        // already running happens immediately, inside SetParameters.
        private const float SeedingDebounceSeconds = 0.2f;
        private bool seedingDirty;
        private float seedingDirtySince;
        private bool seedingWasGenerating;
        private string seedingFailureLogged;

        // Command-line "-lifeSeedApply" waits for the background generation to land.
        private bool seedingApplyWhenReady;

        /// <summary>True from the moment a preview is requested until it is applied or cancelled.</summary>
        private bool previewMode;

        // What the panel showed before a candidate went up, so cancelling restores
        // the display rather than guessing from the experiment's initial label.
        private string captionBeforePreview;
        private int patternBeforePreview;
        private bool hadSelectionBeforePreview;

        private Button playButtonRef;
        private Button stepButtonRef;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<LifeTerminalController>() != null)
                return;

            GameObject host = new("Life Terminal");
            DontDestroyOnLoad(host);
            host.AddComponent<LifeTerminalController>();
        }

        private void Awake()
        {
            Application.targetFrameRate = 120;

            ReadBoardSizeFromCommandLine();
            Seeding = new LifeSeedingSession(gridWidth, gridHeight);

            if (LifeBoardRenderer.TryCreate(out renderer, out string rendererError))
            {
                Debug.Log($"[Life] display path: compute shader render kernel");
            }
            else
            {
                renderer = null;
                Debug.LogWarning($"[Life] compute display unavailable ({rendererError}); falling back to per-cell drawing");
            }

            cpuBackend = new LifeAsyncCpuBackend(gridWidth, gridHeight);
            gpuAvailable = GpuLifeBackend.TryCreate(gridWidth, gridHeight, out gpuBackend, out gpuUnavailableReason);
            if (!gpuAvailable)
                Debug.LogWarning($"[Life] GPU backend unavailable: {gpuUnavailableReason}");

            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "Life Terminal Panel Settings";
            panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;

            // 16:9, because that is the shape this project is reviewed at (1280x720, 1920x1080).
            // The VisiblePanelSize math in PanelScreenFit shows why the aspect ratio of the
            // reference matters more than its pixel count: against a 16:10 reference, a 16:9 window
            // exposes only ~854 panel units of height no matter how large the screen is, which
            // cannot fit the side-by-side layout and forced every 16:9 window into the stacked one.
            panelSettings.referenceResolution = new Vector2Int(ReferenceWidth, ReferenceHeight);
            panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panelSettings.match = ReferenceMatch;
            panelSettings.sortingOrder = 10;
            panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("LifeRuntimeTheme");

            UIDocument document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            BuildInterface(document.rootVisualElement);

            // USS cannot express media queries, so the responsive switch is driven from code by
            // watching the root's resolved width.
            document.rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ApplyCompactClass(document.rootVisualElement);

            ApplyBackend();
            LoadPattern(selectedPattern);

            // -lifePattern wins over the default specimen when it names a real one.
            int requested = ReadPatternFromCommandLine();
            if (requested >= 0)
                LoadPattern(requested);

            grid.SetInitialZoom(ReadIntFromCommandLine("-lifeZoom"));
            grid.CenterView();

            ApplySeedingFromCommandLine();

            // "-lifeRun" starts the clock, so a captured Player frame shows a board
            // that has actually evolved on the GPU rather than generation 0.
            if (HasFlag("-lifeRun"))
                ToggleRunning();

            // "-lifePerf" runs the stage-A measurement pass and quits.
            if (HasFlag("-lifePerf"))
                gameObject.AddComponent<LifePerfProbe>();

            // "-lifeBench" runs the stage-C large-board benchmark and quits.
            if (HasFlag("-lifeBench"))
                gameObject.AddComponent<LifeBoardBench>();
        }

        private static bool HasFlag(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == flag)
                    return true;
            }

            return false;
        }

        /// <summary>Reads "-name &lt;int&gt;"; returns 0 when absent or unparsable.</summary>
        private static int ReadIntFromCommandLine(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && int.TryParse(args[i + 1], out int value))
                    return value;
            }

            return 0;
        }

        private static int ReadIntFromCommandLine(string name, int fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && int.TryParse(args[i + 1], out int value))
                    return value;
            }

            return fallback;
        }

        private static float ReadFloatFromCommandLine(string name, float fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name
                    && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return value;
                }
            }

            return fallback;
        }

        // -- stage B seeding ---------------------------------------------------

        /// <summary>
        /// Optional command-line seeding, so evidence captures and smoke runs do not
        /// depend on driving the panel. "-lifeSeedApply" confirms the candidate;
        /// "-lifeSeedPreview" only puts it on screen.
        /// </summary>
        private void ApplySeedingFromCommandLine()
        {
            LifeNoiseParameters defaults = Seeding.Parameters;

            bool requested =
                HasFlag("-lifeSeedApply") || HasFlag("-lifeSeedPreview") ||
                HasFlag("-lifeSeed") || HasFlag("-lifeSeedUniform") ||
                HasFlag("-lifeDensity") || HasFlag("-lifeScale") ||
                HasFlag("-lifeWarp") || HasFlag("-lifeCluster");

            if (!requested)
                return;

            var parameters = new LifeNoiseParameters(
                HasFlag("-lifeSeedUniform") ? LifeSeedingMode.Uniform : defaults.Mode,
                ReadIntFromCommandLine("-lifeSeed", defaults.Seed),
                ReadFloatFromCommandLine("-lifeDensity", defaults.Density),
                ReadFloatFromCommandLine("-lifeScale", defaults.Scale),
                ReadFloatFromCommandLine("-lifeWarp", defaults.WarpStrength),
                ReadFloatFromCommandLine("-lifeCluster", defaults.ClusterStrength));

            Seeding.SetParameters(parameters);
            SyncSeedingControlsFromSession();

            if (HasFlag("-lifeSeedApply"))
            {
                seedingApplyWhenReady = true;
                PreviewSeeding();
            }
            else if (HasFlag("-lifeSeedPreview"))
            {
                PreviewSeeding();
            }
        }

        /// <summary>
        /// Puts a candidate on screen. The real board is not touched: the candidate
        /// goes into the display path only. Generation is requested, not performed --
        /// it runs on a background thread and lands in <see cref="PumpSeeding"/>.
        /// </summary>
        public void PreviewSeeding()
        {
            Stop();

            if (!previewMode)
            {
                RememberDisplayState();
                previewMode = true;
            }

            // Asking for a candidate is a seeding action, so bring its panel forward.
            ShowToolPage(true);

            seedingDirty = false;
            Seeding.RequestCandidate();
            UpdateSeedingActions();
            RefreshReadouts();
        }

        /// <summary>
        /// Confirms the candidate: it becomes the experiment's initial state, the
        /// generation counter goes back to zero, and the clock stays paused.
        ///
        /// If the preview is still waiting for a candidate that matches the controls --
        /// because a generation is running, or because a slider moved and the replacement
        /// is still inside the debounce window -- this waits for it rather than applying a
        /// stale board. The intent is remembered and spent only on an adopted candidate,
        /// so a result that gets dropped cannot consume it.
        /// </summary>
        public void ApplySeeding()
        {
            if (Seeding.IsGenerating)
            {
                seedingApplyWhenReady = true;
                UpdateSeedingActions();
                return;
            }

            byte[] board = Seeding.Apply();
            if (board == null)
                return;

            grid.ClearPreview();
            previewMode = false;
            seedingApplyWhenReady = false;

            string label = $"播种 / {Seeding.AppliedParameters}";
            CaptureInitialState(board, label);
            Restart();

            // Also drops the specimen highlight: the board is no longer that specimen.
            SelectCustom(label);
            SyncSeedingControlsFromSession();
            UpdateSeedingReadout();
            UpdateSeedingActions();
        }

        /// <summary>
        /// Throws the candidate away and restores what the panel showed before the
        /// preview started. The board was never moved, so only the display has to be
        /// put back -- and it is put back from a snapshot taken on entry, not from the
        /// experiment's label, which would be wrong for a hand-edited board.
        /// </summary>
        public void CancelSeeding()
        {
            if (!previewMode && !Seeding.HasCandidate && !Seeding.IsGenerating)
                return;

            Seeding.Cancel();
            grid.ClearPreview();
            previewMode = false;
            seedingDirty = false;
            seedingApplyWhenReady = false;

            RestoreDisplayState();
            UpdateSeedingReadout();
            UpdateSeedingActions();
            RefreshReadouts();
        }

        /// <summary>
        /// Commands that replace or move the board must not run underneath a candidate:
        /// otherwise the caption and the picture disagree. Every such command ends the
        /// preview first.
        /// </summary>
        private void EndPreviewForCommand()
        {
            if (!previewMode && !Seeding.HasCandidate && !Seeding.IsGenerating)
                return;

            Seeding.Cancel();
            grid.ClearPreview();
            previewMode = false;
            seedingDirty = false;
            seedingApplyWhenReady = false;
        }

        private void RememberDisplayState()
        {
            captionBeforePreview = sampleLabel.text;
            patternBeforePreview = selectedPattern;
            hadSelectionBeforePreview = false;

            for (int i = 0; i < presetButtons.Length; i++)
            {
                if (presetButtons[i].ClassListContains("selected"))
                {
                    hadSelectionBeforePreview = true;
                    break;
                }
            }
        }

        private void RestoreDisplayState()
        {
            sampleLabel.text = captionBeforePreview;

            for (int i = 0; i < presetButtons.Length; i++)
            {
                bool selected = hadSelectionBeforePreview && i == patternBeforePreview;
                presetButtons[i].EnableInClassList("selected", selected);
            }
        }

        /// <summary>
        /// Generating a large board is not free, so the cost is reported rather than
        /// left implicit. Shown alongside the realised density too: the base density
        /// is a probability, not a population promise.
        /// </summary>
        private void LogSeedingCost(string what)
        {
            Debug.Log($"[Life] seeding {what}: {Seeding.LastGenerationMilliseconds:F1} ms for " +
                      $"{gridWidth}x{gridHeight} ({Seeding.CandidateAliveCount} alive, " +
                      $"realised density {Seeding.CandidateDensity:F4}) -- {Seeding.CandidateParameters}");
        }

        private void OnDestroy()
        {
            // The session owns a background task. Disposing it does NOT wait for that
            // task -- this runs on the main thread during teardown, and blocking here to
            // collect a result nobody will use would turn a clean exit into a stall. It
            // only marks the session, so the late result is refused when it arrives.
            Seeding?.Dispose();

            // Buffers are released on exit; nothing is left allocated.
            backend = null;
            gpuBackend?.Dispose();
            gpuBackend = null;
            cpuBackend?.Dispose();
            cpuBackend = null;
            renderer?.Dispose();
            renderer = null;
        }

        /// <summary>
        /// Optional "-lifeBoard WxH" launch argument. Stage A is verified at 96x64,
        /// 256x256 and 1024x1024, so the size has to be selectable without a rebuild.
        /// </summary>
        private void ReadBoardSizeFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-lifeBoard")
                    continue;

                string[] parts = args[i + 1].Split('x', 'X');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int width)
                    && int.TryParse(parts[1], out int height)
                    && width > 0 && height > 0)
                {
                    gridWidth = width;
                    gridHeight = height;
                }
            }
        }

        /// <summary>
        /// Optional "-lifePattern &lt;EnglishName&gt;" launch argument, matched exactly
        /// against the archive. Unknown names are reported and ignored rather than
        /// silently falling back to a different specimen.
        /// </summary>
        private int ReadPatternFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-lifePattern")
                    continue;

                string requested = args[i + 1];
                for (int index = 0; index < LifePatterns.All.Length; index++)
                {
                    if (LifePatterns.All[index].EnglishName == requested)
                        return index;
                }

                var known = new List<string>();
                foreach (LifePattern pattern in LifePatterns.All)
                    known.Add(pattern.EnglishName);

                Debug.LogWarning($"[Life] -lifePattern '{requested}' is not a known specimen; " +
                                 $"known names: {string.Join(", ", known)}");
            }

            return -1;
        }

        /// <summary>Toggles the stacked layout when the panel cannot host two columns.</summary>
        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            VisualElement root = evt.target as VisualElement;
            ApplyCompactClass(root);

            // The panel scale has to be measured, not assumed: PanelScreenFit can
            // only predict it from Screen.*, and at Awake the window has not been
            // resized to its final size yet. Screen pixels per panel unit is exact.
            if (root != null && grid != null)
            {
                float panelWidth = root.resolvedStyle.width;
                if (!float.IsNaN(panelWidth) && panelWidth > 0f)
                {
                    float measured = Screen.width / panelWidth;
                    if (geometryLogs < 4)
                    {
                        geometryLogs++;
                        Debug.Log($"[Life] root geometry #{geometryLogs}: panelW={panelWidth:F1} " +
                                  $"panelH={root.resolvedStyle.height:F1} screen={Screen.width}x{Screen.height} " +
                                  $"measuredScale={measured:F4} currentPanelScale={grid.PanelScale:F4}");
                    }

                    grid.SetPanelScale(measured);
                }
            }
        }

        /// <summary>
        /// Applies the stacked layout when the panel is too narrow for two columns.
        ///
        /// Keyed on WIDTH ONLY, deliberately. An earlier version also stacked when the panel was
        /// short, which cannot work: the stacked layout is itself ~828 units tall, so a 700-unit
        /// panel switched layout and still did not fit - strictly worse than staying in two columns
        /// and letting the archive scroll. Height shortage is absorbed by the scrolling archive
        /// inside each layout; it is not something the breakpoint can fix.
        /// </summary>
        private static void ApplyCompactClass(VisualElement root)
        {
            if (root == null)
                return;

            float width = root.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 0f)
                return;

            bool compact = width < DesignWidth;
            if (root.ClassListContains("compact") != compact)
                root.EnableInClassList("compact", compact);
        }

        private void Update()
        {
            PumpSeeding();

            // A generation computed off the frame is taken over BEFORE the clock asks for the
            // next one, so the board moves in order and a paused clock keeps its display frozen
            // while the finished generation waits in the backend.
            int retired = PumpEvolution();

            if (!running || backend == null)
                return;

            accumulator += Time.unscaledDeltaTime;
            float interval = 1f / speedSlider.value;

            // Catch-up is bounded on purpose, and the bound is what keeps a slow backend
            // usable. Without it the failure is a cross-frame amplification, not a runaway
            // inside one frame: the accumulator grows by one frame delta per frame, so a
            // frame that took 650 ms (one CPU generation at 4096x4096) is followed by a
            // frame that tries to retire 13 generations at 650 ms each, which makes the next
            // delta larger still. Two caps, because they catch different shapes:
            //
            //   * MaxGenerationsPerFrame bounds a healthy-but-fast clock that simply owes
            //     many generations;
            //   * the time budget stops the loop as soon as this frame's stepping has already
            //     cost more than a frame may, which is the case a slow backend actually hits.
            //
            // When either cap bites, the WHOLE generations of debt are discarded and the
            // simulation is allowed to run slower than the slider asks. Steps are never
            // skipped: every generation that happens is a real evolution of the real board,
            // there are just fewer of them.
            //
            // Stage D adds a third bound for the background CPU path: a generation that is still
            // computing, or one whose result has not been taken over yet, stops the loop. The
            // pipeline holds one generation at a time, so the clock waits instead of queueing
            // work behind itself -- and the debt that builds while it waits is discarded by the
            // overload branch below, exactly as it is for a synchronous backend that cannot keep
            // up. What a background backend does NOT do is make a single generation cheaper: the
            // whole-board upload after each adopted generation is still paid on the main thread.
            int submitted = 0;
            var asyncBackend = backend as ILifeAsyncBackend;

            // Timestamps, not a Stopwatch object: this runs on every running frame, and a
            // Stopwatch.StartNew() per frame allocates even on the frames that advance nothing.
            long stepStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            while (accumulator >= interval && submitted < MaxGenerationsPerFrame)
            {
                if (asyncBackend != null && (asyncBackend.IsComputing || asyncBackend.HasCompletedGeneration))
                    break;

                accumulator -= interval;
                backend.Step();
                submitted++;

                if (asyncBackend == null)
                    retired++;

                double steppingMilliseconds =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - stepStartedTicks) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                if (steppingMilliseconds >= GenerationBudgetMilliseconds)
                    break;
            }

            bool overloaded = false;
            if (accumulator >= interval)
            {
                // Drop the whole generations of debt, keep the sub-interval remainder so the
                // clock keeps its phase. The board is not fast-forwarded to "now"; it is
                // simply behind, and the next frames advance it at whatever rate they can.
                accumulator %= interval;
                overloaded = true;
            }

            TrackAchievedRate(retired);

            if (overloaded != clockOverloaded)
            {
                // The readout has to say "slower than asked" as soon as the clock starts
                // dropping debt, not half a second later when the rate window closes.
                clockOverloaded = overloaded;
                RefreshState();
            }

            // One repaint per frame, not one per generation: a fast clock can
            // advance several generations in a single frame and only the final
            // state is ever visible. On a background backend the repaint belongs to the
            // adoption instead, which PumpEvolution has already handled.
            if (asyncBackend == null && submitted > 0)
            {
                RefreshReadouts();
                grid.MarkBoardDirty();
            }
        }

        /// <summary>
        /// Drives a backend that computes off the frame: takes a finished generation into the
        /// display, and issues the single generation a manual "▸ 单步" is still owed.
        ///
        /// <para><b>Pause semantics, which is what this method decides.</b> A paused clock stops
        /// adopting, so the display freezes at the generation it was showing. Whatever the worker
        /// finishes lands in the backend's waiting slot and stays there: nothing is thrown away
        /// and nothing is fast-forwarded. Resuming -- or asking for a single step -- takes it over
        /// first, so a generation that was already computed is never skipped. A result that is
        /// refused is refused because a command replaced the board it belonged to, and the backend
        /// counts it rather than dropping it silently.</para>
        ///
        /// <para>Returns how many generations moved the board this frame: 0 or 1, because the
        /// backend holds at most one generation at a time.</para>
        /// </summary>
        private int PumpEvolution()
        {
            if (backend is not ILifeAsyncBackend asyncBackend)
                return 0;

            int retired = 0;

            if ((running || singleStepOutstanding) && asyncBackend.HasCompletedGeneration &&
                asyncBackend.TryAdoptCompletedGeneration(out _))
            {
                retired = 1;
                singleStepOutstanding = false;
                RefreshReadouts();
                grid.MarkBoardDirty();

                // A single step while paused is not a run, so it does not enter the rate window
                // and only the state readout needs to change.
                if (!running)
                    RefreshState();
            }

            ReportAsyncFailure(asyncBackend);

            // The manual generation, issued only while the pipeline is free. Kept as a flag so a
            // second press while one is in flight cannot queue a third generation.
            if (!running && singleStepOutstanding &&
                !asyncBackend.IsComputing && !asyncBackend.HasCompletedGeneration)
            {
                backend.Step();
            }

            return retired;
        }

        /// <summary>
        /// Reports a worker failure once. Without it a backend that throws would leave the clock
        /// asking for generations that never arrive, with nothing on screen saying why.
        /// </summary>
        private void ReportAsyncFailure(ILifeAsyncBackend asyncBackend)
        {
            string failure = asyncBackend.FailureMessage;
            if (failure == null || failure == asyncFailureLogged)
                return;

            asyncFailureLogged = failure;
            Debug.LogWarning($"[Life] CPU evolution failed: {failure}");
            RefreshState();
        }

        /// <summary>
        /// Generations retired per second, published once per rate window. The slider says what
        /// was ASKED for; this says what the backend manages on this board, which is the number
        /// that matters once the clock can be overloaded.
        ///
        /// <para><b>What is divided by what.</b> The generations in a window are the ones retired
        /// during the frames that window covers, divided by the time those same frames took --
        /// which is why the count is held back by one frame. Stepping done in frame k is only
        /// charged to the clock when frame k's own cost arrives, as frame k+1's delta. Dividing
        /// this frame's steps by the time accumulated up to this frame's START would charge the
        /// work to a period that excludes it. On a steady board the two pairings cover the same
        /// frames shifted by one and agree; the difference appears where the frame cost changes
        /// -- the frames around a start, a pause or a spike, which is where a wrong pairing
        /// reads high.</para>
        ///
        /// <para><b>The window is tumbling, not sliding.</b> It is closed and restarted from
        /// zero, so the published number is one segment's average, not a trailing one. Until the
        /// first window closes, <see cref="AchievedRateMeasured"/> is false and the panel says it
        /// is still sampling rather than showing the initialised zero as a measurement.</para>
        ///
        /// <para>A window shorter than the clock's own interval can contain no generation at all,
        /// so on a backend that cannot reach the requested rate the figure moves in steps of
        /// roughly one generation per window. It is a report, not a precision instrument.</para>
        /// </summary>
        private void TrackAchievedRate(int retiredThisFrame)
        {
            rateWindowGenerations += generationsRetiredLastFrame;
            generationsRetiredLastFrame = retiredThisFrame;
            rateWindowSeconds += Time.unscaledDeltaTime;

            if (rateWindowSeconds < RateWindowSeconds)
                return;

            achievedGenerationsPerSecond = (float)(rateWindowGenerations / rateWindowSeconds);
            achievedRateMeasured = true;
            rateWindowGenerations = 0;
            rateWindowSeconds = 0.0;
            RefreshState();
        }

        /// <summary>
        /// Ends the clock state of the run that just finished: the outstanding debt, the rate
        /// window in progress, the published rate, and the overload flag.
        ///
        /// <para>Called wherever the board is replaced or the clock is stopped or started --
        /// pause, reset, backend switch. Carrying debt across any of those would make a later
        /// frame replay time that belongs to a board or a clock that no longer exists.</para>
        ///
        /// <para>The rate report goes with the debt because it describes the run that ended.
        /// Keeping it would leave the previous run's rate and overload warning on screen until
        /// the new window closed half a second later -- so a slow CPU backend switching to the
        /// GPU would still be claiming to be overloaded, and a fresh run would show the old
        /// run's rate as if it had already been measured.</para>
        /// </summary>
        private void ResetClockState()
        {
            accumulator = 0f;
            rateWindowGenerations = 0;
            rateWindowSeconds = 0.0;
            generationsRetiredLastFrame = 0;
            achievedGenerationsPerSecond = 0f;
            achievedRateMeasured = false;
            clockOverloaded = false;

            // The single-step intent is part of the state that ends here. Every caller is either
            // a command that replaces the board (reset, edit, pattern, backend switch), where the
            // step the user asked for no longer describes anything, or the step itself, which
            // sets the flag again immediately after.
            singleStepOutstanding = false;
        }

        /// <summary>
        /// Drives the seeding pipeline: fires a debounced generation request, adopts
        /// finished candidates, and keeps the panel's enabled state in step. Runs every
        /// frame because the generator is on a background thread; nothing here blocks.
        /// </summary>
        private void PumpSeeding()
        {
            if (Seeding == null || seedingModeField == null)
                return;

            if (previewMode)
            {
                if (seedingDirty && Time.unscaledTime - seedingDirtySince >= SeedingDebounceSeconds)
                {
                    // Deliberately NOT gated on "is something still generating". The
                    // request has to be issued when the debounce says so: an older task
                    // may well still be running with parameters that were already
                    // invalidated, and skipping the request because of it would leave a
                    // stale candidate on screen with nothing on the way.
                    seedingDirty = false;
                    Seeding.RequestCandidate();
                }
                else if (!seedingDirty && !Seeding.HasPendingRequest && !Seeding.IsWorking &&
                         Seeding.IsGenerating)
                {
                    // Self-heal. A panel waiting for a candidate nobody is computing would
                    // lock the clock, single-step and painting for good; asking again costs
                    // one comparison per frame and cannot happen on a healthy path.
                    Seeding.RequestCandidate();
                }
            }
            else
            {
                // Outside a preview there is no candidate to keep up to date. Clearing this
                // is what makes "adjusting parameters never generates anything" hold even
                // after a preview was cancelled mid-drag.
                seedingDirty = false;
            }

            bool adopted = Seeding.PumpGeneration();

            if (adopted)
            {
                LogSeedingCost("generated");

                if (previewMode)
                {
                    grid.ShowPreview(Seeding.Candidate, gridWidth, gridHeight);
                    sampleLabel.text = $"预览（未应用）/ {Seeding.CandidateParameters}";
                }

                if (seedingApplyWhenReady)
                {
                    seedingApplyWhenReady = false;
                    ApplySeeding();
                    return;
                }
            }

            string failure = Seeding.FailureMessage;
            bool failureChanged = !string.Equals(failure, seedingFailureLogged);
            if (failureChanged)
            {
                seedingFailureLogged = failure;
                if (failure != null)
                    Debug.LogWarning($"[Life] seeding generation failed: {failure}");
            }

            if (adopted || failureChanged || Seeding.IsGenerating != seedingWasGenerating)
            {
                seedingWasGenerating = Seeding.IsGenerating;
                UpdateSeedingReadout();
                UpdateSeedingActions();
            }
        }

        /// <summary>
        /// Writes the session's parameters back into every control, without firing
        /// their callbacks. Every path that changes parameters -- the command line,
        /// random seeding, and the controls themselves -- goes through here, so the
        /// caption, the realised-density readout and the candidate always describe
        /// the same set. Without it, dragging one slider reads the stale values of
        /// the others and overwrites them.
        /// </summary>
        private void SyncSeedingControlsFromSession()
        {
            if (seedingModeField == null)
                return;

            LifeNoiseParameters parameters = Seeding.Parameters;

            seedingModeField.SetValueWithoutNotify(
                parameters.Mode == LifeSeedingMode.Uniform ? UniformModeLabel : FbmModeLabel);
            seedField.SetValueWithoutNotify(parameters.Seed);
            densitySlider.SetValueWithoutNotify(parameters.Density);
            scaleSlider.SetValueWithoutNotify(parameters.Scale);
            warpSlider.SetValueWithoutNotify(parameters.WarpStrength);
            clusterSlider.SetValueWithoutNotify(parameters.ClusterStrength);
        }

        private void BuildInterface(VisualElement root)
        {
            StyleSheet styleSheet = Resources.Load<StyleSheet>("LifeTerminal");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("app");

            VisualElement header = Element("header");
            VisualElement titleBlock = Element("title-block");
            titleBlock.Add(Label("生命演算所", "title"));
            titleBlock.Add(Label("CONWAY'S GAME OF LIFE / FIELD STATION 04", "serial"));
            header.Add(titleBlock);
            statusLabel = Label("●  待机中", "status");
            header.Add(statusLabel);
            root.Add(header);

            VisualElement machine = Element("machine");
            VisualElement topLine = Element("top-line");
            topLine.Add(Label("LIFE TERMINAL · MODEL 1970", "micro"));
            VisualElement hardware = Label("CELLULAR AUTOMATA / B3 · S23", "micro");
            hardware.name = "hardware-note";
            topLine.Add(hardware);
            machine.Add(topLine);

            VisualElement workspace = Element("workspace");
            VisualElement display = Element("display");
            VisualElement screenBar = Element("screen-bar");
            sampleLabel = Label(string.Empty, "micro");
            screenBar.Add(sampleLabel);
            fieldLabel = Label(string.Empty, "micro");
            fieldLabel.name = "field-label";
            screenBar.Add(fieldLabel);
            display.Add(screenBar);

            grid = new LifeGridElement { name = "life-grid" };
            grid.AddToClassList("life-grid");
            grid.Edited = OnGridEdited;
            display.Add(grid);

            VisualElement readouts = Element("readouts");
            generationLabel = AddReadout(readouts, "GENERATION / 世代");
            populationLabel = AddReadout(readouts, "POPULATION / 存活");
            stateLabel = AddReadout(readouts, "STATE / 状态", true);
            display.Add(readouts);
            workspace.Add(display);

            VisualElement library = Element("library");

            // Two tool pages rather than one ever-growing column: the specimen
            // archive and the seeding panel each want the whole column, and stacking
            // them pushed the archive down to a few visible rows.
            VisualElement tabs = Element("tool-tabs");
            presetTabButton = Button("样本", () => ShowToolPage(false), "tool-tab");
            presetTabButton.name = "tool-tab-presets";
            seedingTabButton = Button("播种", () => ShowToolPage(true), "tool-tab");
            seedingTabButton.name = "tool-tab-seeding";
            tabs.Add(presetTabButton);
            tabs.Add(seedingTabButton);
            library.Add(tabs);

            VisualElement presetsPage = Element("tool-page");
            presetsPage.name = "tool-page-presets";
            presetsPage.Add(Label("样本档案", "section-title"));
            presetsPage.Add(Label($"SPECIMEN ARCHIVE / {LifePatterns.All.Length:00} ENTRIES", "archive-note"));

            // The archive is the only region that cannot shrink arbitrarily; scrolling it keeps
            // the machine inside the reference height so the controls are never clipped.
            //
            // It manages its own overflow and is NOT nested inside another scroll view:
            // nesting changed its geometry enough that an entry could no longer be
            // scrolled fully into view, which is exactly what the archive test checks.
            ScrollView archive = new(ScrollViewMode.Vertical) { verticalScrollerVisibility = ScrollerVisibility.Auto };
            archive.AddToClassList("preset-scroll");
            presetButtons = new Button[LifePatterns.All.Length];
            for (int i = 0; i < LifePatterns.All.Length; i++)
            {
                int index = i;
                LifePattern pattern = LifePatterns.All[i];
                Button button = new(() => LoadPattern(index));
                button.AddToClassList("preset");
                button.text = $"{pattern.Name}\n{KindName(pattern)}";
                archive.Add(button);
                presetButtons[i] = button;
            }

            presetsPage.Add(archive);
            library.Add(presetsPage);
            presetPage = presetsPage;

            // The seeding panel is the one that needs a scroll region of its own: in
            // the stacked layout it wraps onto several rows.
            ScrollView seedingScroll = new(ScrollViewMode.Vertical)
            {
                verticalScrollerVisibility = ScrollerVisibility.Auto,
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
            };
            seedingScroll.name = "tool-page-seeding";
            seedingScroll.AddToClassList("tool-page");

            // The controls are added to the scroll view's CONTENT CONTAINER, so the stacked
            // layout has to style that element. Styling the scroll view instead left the
            // container untouched: it then sized itself to its widest child (about 220 units)
            // and the seeding panel sat in a narrow column with the rest of the row empty.
            seedingScroll.contentContainer.AddToClassList("seed-page");
            BuildSeedingPanel(seedingScroll);
            library.Add(seedingScroll);
            seedingPage = seedingScroll;

            ShowToolPage(false);

            library.Add(Label("边界条件", "field-label"));
            DropdownField boundary = new(new List<string> { "固定边界", "环绕边界" }, "固定边界");
            boundary.name = "boundary-field";
            boundary.AddToClassList("dropdown");
            boundary.RegisterValueChangedCallback(evt =>
            {
                wrapEdges = evt.newValue == "环绕边界";
                // Changing the boundary restarts the experiment from the same board,
                // so the two runs stay comparable.
                if (backend != null)
                    backend.WrapEdges = wrapEdges;
                Stop();
                RestoreInitialState();
                RefreshReadouts();
            });
            library.Add(boundary);

            library.Add(Label("演算后端", "field-label"));
            List<string> backendChoices = new() { "GPU（Compute Shader）", "CPU（参考实现）" };
            DropdownField backendField = new(backendChoices, backendChoices[0]);
            backendField.name = "backend-field";
            backendField.AddToClassList("dropdown");
            backendField.SetEnabled(gpuAvailable);
            backendField.RegisterValueChangedCallback(evt =>
            {
                if (!gpuAvailable)
                    return;
                SwitchBackend(evt.newValue == backendChoices[0]);
            });
            library.Add(backendField);

            workspace.Add(library);
            machine.Add(workspace);

            VisualElement controls = Element("controls");
            playButton = Button("▶ 运行", ToggleRunning, "control", "primary");
            playButtonRef = playButton;
            controls.Add(playButton);
            stepButtonRef = Button("▸ 单步", StepOnce, "control");
            controls.Add(stepButtonRef);
            controls.Add(Button("↺ 重置", ResetToInitialState, "control"));
            controls.Add(Label("速率", "speed-label"));
            speedSlider = new SliderInt(1, 20) { value = 5 };
            speedSlider.AddToClassList("speed-slider");
            controls.Add(speedSlider);
            Label speedValue = Label("5 代/秒", "speed-value");
            speedSlider.RegisterValueChangedCallback(evt => speedValue.text = $"{evt.newValue} 代/秒");
            controls.Add(speedValue);
            VisualElement spacer = Element("spacer");
            controls.Add(spacer);
            controls.Add(Button("随机播种", Randomize, "control"));
            controls.Add(Button("清空", Clear, "control", "danger"));
            machine.Add(controls);
            root.Add(machine);

            VisualElement footer = Element("footer");
            footer.Add(Label("实验记录 / LIFE–001", "plate"));
            footer.Add(Label("生命，始于简单的规则。", "footer-text"));
            root.Add(footer);
        }

        private static VisualElement Element(string className)
        {
            VisualElement element = new();
            element.AddToClassList(className);
            return element;
        }
        private static Label Label(string text, string className)
        {
            Label label = new(text);
            label.AddToClassList(className);
            return label;
        }

        private static Button Button(string text, Action callback, params string[] classes)
        {
            Button button = new(callback) { text = text };
            foreach (string className in classes)
                button.AddToClassList(className);
            return button;
        }

        private static Label AddReadout(VisualElement parent, string caption, bool compact = false)
        {
            VisualElement readout = Element("readout");
            readout.Add(Label(caption, "readout-caption"));
            Label value = Label("0000", compact ? "readout-value-compact" : "readout-value");
            readout.Add(value);
            parent.Add(readout);
            return value;
        }

        // -- stage B seeding panel ---------------------------------------------

        /// <summary>
        /// Shows one tool page and marks its tab. Both pages stay in the tree; the
        /// inactive one is hidden, so switching costs nothing and keeps its state.
        /// </summary>
        private void ShowToolPage(bool seeding)
        {
            if (presetPage == null || seedingPage == null)
                return;

            presetPage.style.display = seeding ? DisplayStyle.None : DisplayStyle.Flex;
            seedingPage.style.display = seeding ? DisplayStyle.Flex : DisplayStyle.None;
            presetTabButton.EnableInClassList("tool-tab-active", !seeding);
            seedingTabButton.EnableInClassList("tool-tab-active", seeding);
        }

        private VisualElement presetPage;
        private VisualElement seedingPage;
        private Button presetTabButton;
        private Button seedingTabButton;

        /// <summary>
        /// Five parameters and three actions, nothing more. Every row here is height
        /// the specimen archive loses, so the controls are deliberately compact.
        /// </summary>
        private void BuildSeedingPanel(VisualElement library)
        {
            library.Add(Label("噪声播种", "field-label"));

            List<string> modes = new() { FbmModeLabel, UniformModeLabel };
            seedingModeField = new DropdownField(modes, FbmModeLabel);
            seedingModeField.name = "seed-mode";
            seedingModeField.AddToClassList("dropdown");
            seedingModeField.AddToClassList("seed-mode");
            seedingModeField.RegisterValueChangedCallback(_ => OnSeedingEdited());
            library.Add(seedingModeField);

            VisualElement seedRow = Element("seed-row");
            seedField = new IntegerField("种子") { value = Seeding.Parameters.Seed };
            seedField.name = "seed-field";
            seedField.AddToClassList("seed-field");
            seedField.RegisterValueChangedCallback(_ => OnSeedingEdited());
            seedRow.Add(seedField);

            // "换" rather than the ⟳ it used to carry: the runtime font has no U+27F3, and
            // the Player drew the button as an empty box. Which glyphs are present is not
            // something that can be settled from the source - it was checked in a capture -
            // so the replacement is a character class this build demonstrably renders
            // (every CJK label on screen does). The tooltip carries the full wording.
            Button reroll = Button("换", RerollSeed, "seed-button");
            reroll.name = "seed-reroll";
            reroll.tooltip = "换一个种子（由当前种子派生，不是重新随机整块盘面）";
            seedRow.Add(reroll);
            library.Add(seedRow);

            densitySlider = AddSeedingSlider(library, "密度", 0.05f, 0.60f,
                Seeding.Parameters.Density, "seed-density");
            scaleSlider = AddSeedingSlider(library, "团簇尺度", 4f, 160f,
                Seeding.Parameters.Scale, "seed-scale");
            warpSlider = AddSeedingSlider(library, "扭曲强度", 0f, 40f,
                Seeding.Parameters.WarpStrength, "seed-warp");
            clusterSlider = AddSeedingSlider(library, "聚集强度", 0f, 1f,
                Seeding.Parameters.ClusterStrength, "seed-cluster");

            seedingDensityLabel = Label(string.Empty, "seed-density");
            seedingDensityLabel.name = "seed-density-readout";
            library.Add(seedingDensityLabel);

            VisualElement actions = Element("seed-actions");
            seedingApplyButton = Button("应用", ApplySeeding, "control-small");
            seedingApplyButton.name = "seed-apply";
            seedingCancelButton = Button("取消", CancelSeeding, "control-small");
            seedingCancelButton.name = "seed-cancel";
            actions.Add(Button("预览", PreviewSeeding, "control-small"));
            actions.Add(seedingApplyButton);
            actions.Add(seedingCancelButton);
            library.Add(actions);

            UpdateSeedingReadout();
            UpdateSeedingActions();
        }

        private Slider AddSeedingSlider(VisualElement parent, string caption, float low, float high,
            float value, string name)
        {
            Slider slider = new(low, high) { label = caption, value = value, showInputField = true };
            slider.name = name;
            slider.AddToClassList("seed-slider");
            slider.RegisterValueChangedCallback(_ => OnSeedingEdited());
            parent.Add(slider);
            return slider;
        }

        private const string FbmModeLabel = "fBm 团簇";
        private const string UniformModeLabel = "均匀随机";

        private LifeNoiseParameters CurrentSeedingParameters() => new(
            seedingModeField.value == UniformModeLabel ? LifeSeedingMode.Uniform : LifeSeedingMode.Fbm,
            seedField.value,
            densitySlider.value,
            scaleSlider.value,
            warpSlider.value,
            clusterSlider.value);

        /// <summary>
        /// Stores the edited parameters. Outside a preview that is ALL it does: there is no
        /// candidate to keep up to date, so no generation is requested and the board is left
        /// exactly as it was, running or paused. Only while a preview is open does an edit
        /// schedule a replacement, debounced and generated off the main thread.
        ///
        /// <para><see cref="LifeSeedingSession.SetParameters"/> invalidates anything already
        /// in flight in the same call. So a request that was running when the slider moved
        /// cannot land on screen a moment later: the debounce decides when the replacement
        /// computation starts, not whether the old one still counts.</para>
        /// </summary>
        private void OnSeedingEdited()
        {
            if (Seeding == null || seedingModeField == null)
                return;

            Seeding.SetParameters(CurrentSeedingParameters());

            if (previewMode)
            {
                seedingDirty = true;
                seedingDirtySince = Time.unscaledTime;
            }

            UpdateSeedingReadout();
            UpdateSeedingActions();
        }

        /// <summary>
        /// Walks the seed to a different value. Kept behind its own button because
        /// changing the seed is meant to be deliberate.
        /// </summary>
        private void RerollSeed()
        {
            int next = unchecked(Seeding.Parameters.Seed * 1664525 + 1013904223);
            seedField.SetValueWithoutNotify(next);
            OnSeedingEdited();
        }

        private void UpdateSeedingReadout()
        {
            if (seedingDensityLabel == null)
                return;

            // Kept short on purpose: the column is 236 units wide and a longer
            // sentence is clipped rather than wrapped.
            if (Seeding.FailureMessage != null)
            {
                seedingDensityLabel.text = "生成失败";
            }
            else if (seedingDirty)
            {
                // Still inside the debounce window: nothing has been asked for yet, so
                // "生成中" would be a claim about work that has not started.
                seedingDensityLabel.text = "预览待更新";
            }
            else if (Seeding.IsGenerating)
            {
                seedingDensityLabel.text = "生成中…";
            }
            else if (Seeding.CandidateIsStale)
            {
                seedingDensityLabel.text = "预览待更新";
            }
            else if (Seeding.HasCandidate)
            {
                seedingDensityLabel.text =
                    $"实际 {Seeding.CandidateDensity:0.0000} · {Seeding.LastGenerationMilliseconds:F0} ms";
            }
            else
            {
                // No preview is open, so there is nothing to report but the way in.
                seedingDensityLabel.text = previewMode ? "实际 —（尚未生成候选）" : "实际 —（点「预览」生成）";
            }

            seedingDensityLabel.tooltip =
                $"基础密度 {Seeding.Parameters.Density:0.00} 是概率，不是人口承诺；" +
                "聚集会把实际密度推离它。";
        }

        /// <summary>
        /// Single place that decides what may be pressed. While a candidate is on screen the
        /// clock, single-stepping and painting are all unavailable: they would change the real
        /// board underneath a picture that no longer describes it. Panning and zooming stay
        /// available because they only move the view.
        ///
        /// <para>All three inputs are false outside a preview -- a candidate only exists
        /// while one is open, and <see cref="LifeSeedingSession.IsGenerating"/> is false
        /// unless a candidate is wanted -- so merely editing parameters with the seeding
        /// page open can never disable the clock. A background task that is still winding
        /// down after a cancel is not part of this decision either:
        /// <see cref="LifeSeedingSession.IsWorking"/> is deliberately not consulted here.</para>
        /// </summary>
        private void UpdateSeedingActions()
        {
            if (Seeding == null || seedingApplyButton == null)
                return;

            bool previewing = previewMode || Seeding.HasCandidate || Seeding.IsGenerating;
            bool canApply = Seeding.HasCandidate && !Seeding.IsGenerating && !Seeding.CandidateIsStale;

            seedingApplyButton.SetEnabled(canApply);
            seedingCancelButton.SetEnabled(previewing);

            // The run/pause and single-step controls are deliberately NOT gated on a background
            // computation. Pausing during one is the point of stage D, and 单步 while one is in
            // flight is a meaningful request: PumpEvolution hands over the generation that is
            // already being computed (or already waiting) rather than starting a second one.
            playButtonRef?.SetEnabled(!previewing);
            stepButtonRef?.SetEnabled(!previewing);

            grid?.SetEditingEnabled(!previewing);
        }

        private static string KindName(LifePattern pattern)
        {
            switch (pattern.Kind)
            {
                case LifePatternKind.StillLife:
                    return "稳定 / STILL LIFE";
                case LifePatternKind.Oscillator:
                    return $"振荡 / PERIOD {pattern.Period:00}";
                case LifePatternKind.PeriodicOscillator:
                    return $"循环震荡 / PERIOD {pattern.Period:00}";
                default:
                    return $"飞船 / PERIOD {pattern.Period:00}";
            }
        }

        // -- backend -----------------------------------------------------------

        private void ApplyBackend()
        {
            backend = useGpu && gpuAvailable ? gpuBackend : cpuBackend;
            backend.WrapEdges = wrapEdges;

            grid.Bind(backend);
            grid.AttachRenderer(renderer, PanelScreenFit.ScaleFactor(
                Screen.width, Screen.height, ReferenceWidth, ReferenceHeight, ReferenceMatch));

            fieldLabel.text = $"{gridWidth} × {gridHeight} / {backend.Name} · LIVE FIELD";
        }

        /// <summary>
        /// Switching backends pauses and restarts from the saved initial board.
        /// There is deliberately no hot switch mid-run.
        /// </summary>
        private void SwitchBackend(bool gpu)
        {
            EndPreviewForCommand();

            if (running)
                Stop();

            // A backend switch changes how long a generation costs, so any debt the old
            // backend accumulated says nothing about the new one -- and neither does the rate
            // it published.
            ResetClockState();

            useGpu = gpu;
            ApplyBackend();
            RestoreInitialState();
            RefreshReadouts();
            RefreshState();
            UpdateSeedingActions();
        }

        // -- commands ----------------------------------------------------------

        private void LoadPattern(int index)
        {
            EndPreviewForCommand();
            Seeding.ForgetApplied();

            selectedPattern = index;
            LifePattern pattern = LifePatterns.All[index];

            byte[] board = new byte[gridWidth * gridHeight];
            PlaceCentered(board, pattern.Cells);

            CaptureInitialState(board, $"样本 {index + 1:00} / {pattern.Name} · {pattern.EnglishName}");
            Restart();

            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].EnableInClassList("selected", i == index);

            UpdateSeedingActions();
        }

        private void Randomize()
        {
            EndPreviewForCommand();
            Stop();

            // Routed through the seeding parameters so there is one random path, not
            // two, and the controls end up describing the board that was produced.
            //
            // Generated inline rather than through the preview pipeline: uniform
            // seeding is one hash per cell -- a few milliseconds even at 1024x1024 --
            // and the pipeline exists for the half-second fBm case. Going through it
            // would leave a candidate pending that nothing is displaying.
            var parameters = new LifeNoiseParameters(
                LifeSeedingMode.Uniform, Environment.TickCount, 0.22f, 48f, 0f, 0f);
            Seeding.SetParameters(parameters);
            SyncSeedingControlsFromSession();
            Seeding.ForgetApplied();

            var board = new byte[gridWidth * gridHeight];
            LifeNoiseSeeding.Generate(parameters, gridWidth, gridHeight, board);

            string label = $"播种 / 均匀随机 seed {parameters.Seed}";
            CaptureInitialState(board, label);
            Restart();
            SelectCustom(label);
            UpdateSeedingReadout();
            UpdateSeedingActions();
        }

        private void Clear()
        {
            EndPreviewForCommand();
            Seeding.ForgetApplied();

            byte[] board = new byte[gridWidth * gridHeight];
            CaptureInitialState(board, "自由样本 / 空白");
            Restart();
            SelectCustom("自由样本 / 空白");
            UpdateSeedingActions();
        }

        /// <summary>
        /// Restores the board this experiment started from. Never re-randomises:
        /// a random board is produced once, on the explicit "随机播种" command.
        /// </summary>
        private void ResetToInitialState()
        {
            EndPreviewForCommand();

            Stop();
            RestoreInitialState();
            RefreshReadouts();
            UpdateSeedingActions();
        }

        private void RestoreInitialState()
        {
            if (initialState == null || backend == null)
                return;

            backend.WrapEdges = wrapEdges;
            backend.LoadBoard(initialState);
            sampleLabel.text = initialLabel;
            grid.MarkBoardDirty();
            grid.CenterView();
        }

        private void CaptureInitialState(byte[] board, string label)
        {
            initialState = board;
            initialLabel = label;
        }

        private void Restart()
        {
            Stop();
            RestoreInitialState();
            RefreshReadouts();
        }

        private void ToggleRunning()
        {
            // The clock is part of the board, and a candidate preview must not be run
            // over. The button is disabled in this state; this is the second lock.
            if (previewMode)
                return;

            running = !running;

            // Starting fresh or stopping both drop the old clock state: a pause must not be
            // followed by a burst of generations the clock owed before it was paused, and a
            // resumed run must not wear the paused run's rate or overload warning.
            ResetClockState();
            RefreshState();
        }

        private void Stop()
        {
            running = false;
            ResetClockState();
            RefreshState();
        }

        private void StepOnce()
        {
            if (previewMode)
                return;

            Stop();

            if (backend is ILifeAsyncBackend)
            {
                // One generation, computed off the frame. The intent is a flag, and
                // PumpEvolution both takes over a generation that is already finished (including
                // one computed before a pause) and issues this one -- in that order, so a single
                // step can never skip a generation that was already computed.
                singleStepOutstanding = true;
                RefreshState();
                return;
            }

            backend.Step();
            grid.MarkBoardDirty();
            RefreshReadouts();
        }

        private void OnGridEdited()
        {
            Stop();
            SelectCustom("自由样本 / 手动编辑");
        }

        private void SelectCustom(string label)
        {
            sampleLabel.text = label;
            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].RemoveFromClassList("selected");
            grid.MarkBoardDirty();
            RefreshReadouts();
        }

        private void RefreshReadouts()
        {
            if (backend == null)
                return;

            generationLabel.text = backend.Generation.ToString("0000");

            // Stage A has no GPU statistics. Showing "unavailable" is the honest
            // answer; a full-board readback every generation would not be.
            populationLabel.text = backend.TryGetPopulation(out int population)
                ? population.ToString("0000")
                : "—";
        }

        private void RefreshState()
        {
            playButton.text = running ? "Ⅱ 暂停" : "▶ 运行";

            // Target and achieved are different numbers as soon as the clock can be overloaded,
            // so the panel shows both instead of reporting the slider's value as if it were what
            // the board is doing.
            //
            // The achieved figure only exists once a window has closed. The initialised zero is
            // NOT a measurement, so the run says it is still sampling instead -- otherwise every
            // start and every resume would flash "实际 0/秒".
            if (asyncFailureLogged != null)
            {
                // A worker that threw is the one thing the readout must not paper over: the clock
                // is still asking for generations and none of them will arrive.
                stateLabel.text = "演算失败";
            }
            else if (!running)
            {
                // A single step is a computation like any other, and it is off the frame now:
                // saying so is the difference between "nothing is happening" and "this will land
                // in a moment".
                stateLabel.text = singleStepOutstanding ? "单步计算中…" : "已暂停";
            }
            else if (clockOverloaded)
            {
                // Overload is reported the frame it starts, without waiting for a window.
                stateLabel.text = achievedRateMeasured
                    ? $"演算中 · 实际 {achievedGenerationsPerSecond:0.#}/秒"
                    : "演算中 · 丢弃追赶欠账";
            }
            else if (!achievedRateMeasured)
            {
                stateLabel.text = "演算中 · 采样中";
            }
            else if (achievedGenerationsPerSecond + 0.5f < speedSlider.value)
            {
                stateLabel.text = $"演算中 · 实际 {achievedGenerationsPerSecond:0.#}/秒";
            }
            else
            {
                stateLabel.text = "演算中";
            }

            stateLabel.tooltip = asyncFailureLogged != null
                ? asyncFailureLogged
                : running ? StateTooltip() : string.Empty;

            if (!gpuAvailable)
            {
                statusLabel.text = "●  GPU 不可用 · CPU 模式";
                statusLabel.tooltip = gpuUnavailableReason;
            }
            else
            {
                statusLabel.text = running ? "●  演算进行中" : "●  待机中";
                statusLabel.tooltip = renderer == null ? "显示回退：逐格绘制" : string.Empty;
            }
        }

        /// <summary>
        /// Says which run the numbers belong to. A tooltip rather than the readout, because the
        /// state line is one short row and the distinction it has to make -- measured, still
        /// sampling, or measured but overloaded -- does not fit there.
        /// </summary>
        private string StateTooltip()
        {
            string measured = achievedRateMeasured
                ? $"实际 {achievedGenerationsPerSecond:0.#} 代/秒（最近一个 {RateWindowSeconds:0.0} 秒窗口）"
                : $"速率窗口 {RateWindowSeconds:0.0} 秒尚未形成，实际速率尚未测出";

            return $"目标 {speedSlider.value} 代/秒；{measured}。" +
                   (clockOverloaded
                       ? "后端跟不上目标速率，时钟正在丢弃追赶欠账（不跳过任何演化步骤，只是变慢）。"
                       : string.Empty);
        }

        /// <summary>
        /// Places a pattern's cells centred on the board, using the same
        /// bounding-box rule as LifeSimulation.LoadCentered so stage A's CPU/GPU
        /// comparison starts from identical data.
        /// </summary>
        private void PlaceCentered(byte[] board, LifeCell[] cells)
        {
            if (cells.Length == 0)
                return;

            int minX = cells[0].X, maxX = cells[0].X;
            int minY = cells[0].Y, maxY = cells[0].Y;
            for (int i = 1; i < cells.Length; i++)
            {
                minX = Math.Min(minX, cells[i].X);
                maxX = Math.Max(maxX, cells[i].X);
                minY = Math.Min(minY, cells[i].Y);
                maxY = Math.Max(maxY, cells[i].Y);
            }

            int offsetX = (gridWidth - (maxX - minX + 1)) / 2 - minX;
            int offsetY = (gridHeight - (maxY - minY + 1)) / 2 - minY;

            for (int i = 0; i < cells.Length; i++)
            {
                int x = cells[i].X + offsetX;
                int y = cells[i].Y + offsetY;
                if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight)
                    board[y * gridWidth + x] = 1;
            }
        }
    }
}
