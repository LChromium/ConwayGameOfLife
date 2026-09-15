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
        /// <code>panelW = W / scale = 2880a / (1.8a + 1)</code>
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
        private CpuLifeBackend cpuBackend;
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

            cpuBackend = new CpuLifeBackend(gridWidth, gridHeight);
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

            if (HasFlag("-lifeSeedApply"))
                ApplySeeding();
            else if (HasFlag("-lifeSeedPreview"))
                PreviewSeeding();
        }

        /// <summary>
        /// Generates a candidate and puts it on screen in the preview colour. The real
        /// board is not touched: the candidate goes into the display path only.
        /// </summary>
        public void PreviewSeeding()
        {
            Stop();

            if (!Seeding.GenerateCandidate())
                return;

            LogSeedingCost("preview");
            grid.ShowPreview(Seeding.Candidate, gridWidth, gridHeight);
            sampleLabel.text = $"预览（未应用）/ {Seeding.Parameters}";
            RefreshReadouts();
        }

        /// <summary>
        /// Confirms the candidate: it becomes the experiment's initial state, the
        /// generation counter goes back to zero, and the clock stays paused.
        /// </summary>
        public void ApplySeeding()
        {
            if (!Seeding.HasCandidate && !Seeding.GenerateCandidate())
                return;

            LogSeedingCost("apply");
            byte[] board = Seeding.Apply();
            grid.ClearPreview();

            CaptureInitialState(board, $"播种 / {Seeding.AppliedParameters}");
            Restart();
        }

        /// <summary>
        /// Generating a large board on the CPU is not free, so the cost is reported
        /// rather than left implicit. Shown as the realised density too: the base
        /// density is a probability, not a population promise.
        /// </summary>
        private void LogSeedingCost(string what)
        {
            Debug.Log($"[Life] seeding {what}: {Seeding.LastGenerationMilliseconds:F1} ms for " +
                      $"{gridWidth}x{gridHeight} ({Seeding.CandidateAliveCount} alive, " +
                      $"realised density {Seeding.CandidateDensity:F4}) -- {Seeding.Parameters}");
        }

        /// <summary>Throws the candidate away. The board was never moved, so there is nothing to restore.</summary>
        public void CancelSeeding()
        {
            if (!Seeding.HasCandidate)
                return;

            Seeding.Cancel();
            grid.ClearPreview();
            sampleLabel.text = initialLabel;
            RefreshReadouts();
        }

        private void OnDestroy()
        {
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
            if (!running || backend == null)
                return;

            accumulator += Time.unscaledDeltaTime;
            float interval = 1f / speedSlider.value;

            bool advanced = false;
            while (accumulator >= interval)
            {
                accumulator -= interval;
                backend.Step();
                advanced = true;
            }

            // One repaint per frame, not one per generation: a fast clock can
            // advance several generations in a single frame and only the final
            // state is ever visible.
            if (advanced)
            {
                RefreshReadouts();
                grid.MarkBoardDirty();
            }
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
            library.Add(Label("样本档案", "section-title"));
            library.Add(Label($"SPECIMEN ARCHIVE / {LifePatterns.All.Length:00} ENTRIES", "archive-note"));

            // The archive is the only region that cannot shrink arbitrarily; scrolling it keeps
            // the machine inside the reference height so the controls are never clipped.
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

            library.Add(archive);

            library.Add(Label("边界条件", "field-label"));
            DropdownField boundary = new(new List<string> { "固定边界", "环绕边界" }, "固定边界");
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
            controls.Add(playButton);
            controls.Add(Button("▸ 单步", StepOnce, "control"));
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
            if (running)
                Stop();

            useGpu = gpu;
            ApplyBackend();
            RestoreInitialState();
            RefreshReadouts();
            RefreshState();
        }

        // -- commands ----------------------------------------------------------

        private void LoadPattern(int index)
        {
            selectedPattern = index;
            LifePattern pattern = LifePatterns.All[index];

            byte[] board = new byte[gridWidth * gridHeight];
            PlaceCentered(board, pattern.Cells);

            CaptureInitialState(board, $"样本 {index + 1:00} / {pattern.Name} · {pattern.EnglishName}");
            Restart();

            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].EnableInClassList("selected", i == index);
        }

        private void Randomize()
        {
            // Routed through the seeding session so there is one random path, not two.
            // Uniform mode with the same hash as the fBm path, so it is the control
            // group the brief asks for and it is reproducible from its seed.
            Stop();
            Seeding.SetParameters(new LifeNoiseParameters(
                LifeSeedingMode.Uniform, Environment.TickCount, 0.22f, 48f, 0f, 0f));
            Seeding.GenerateCandidate();

            byte[] board = Seeding.Apply();
            CaptureInitialState(board, $"播种 / 均匀随机 seed {Seeding.AppliedParameters.Seed}");
            Restart();
            SelectCustom(initialLabel);
        }

        private void Clear()
        {
            byte[] board = new byte[gridWidth * gridHeight];
            CaptureInitialState(board, "自由样本 / 空白");
            Restart();
            SelectCustom("自由样本 / 空白");
        }

        /// <summary>
        /// Restores the board this experiment started from. Never re-randomises:
        /// a random board is produced once, on the explicit "随机播种" command.
        /// </summary>
        private void ResetToInitialState()
        {
            Stop();
            RestoreInitialState();
            RefreshReadouts();
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

            // The seeding parameters only describe the board while that board is the
            // one they produced. Loading a specimen or clearing makes them stale.
            if (Seeding != null && !label.StartsWith("播种", StringComparison.Ordinal))
                Seeding.ForgetApplied();
        }

        private void Restart()
        {
            Stop();
            RestoreInitialState();
            RefreshReadouts();
        }

        private void ToggleRunning()
        {
            running = !running;
            accumulator = 0f;
            RefreshState();
        }

        private void Stop()
        {
            running = false;
            accumulator = 0f;
            RefreshState();
        }

        private void StepOnce()
        {
            Stop();
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
            stateLabel.text = running ? "演算中" : "已暂停";

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
