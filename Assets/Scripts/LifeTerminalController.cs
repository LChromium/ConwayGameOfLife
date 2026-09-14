using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife
{
    public sealed class LifeTerminalController : MonoBehaviour
    {
        private const int GridWidth = 96;
        private const int GridHeight = 64;

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

        private LifeSimulation simulation;
        private LifeGridElement grid;
        private Label generationLabel;
        private Label populationLabel;
        private Label stateLabel;
        private Label statusLabel;
        private Label sampleLabel;
        private Button playButton;
        private SliderInt speedSlider;
        private Button[] presetButtons;
        private int selectedPattern = 2;
        private bool running;
        private float accumulator;

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
            simulation = new LifeSimulation(GridWidth, GridHeight);

            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "Life Terminal Panel Settings";
            panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;

            // 16:9, because that is the shape this project is reviewed at (1280x720, 1920x1080).
            // The VisiblePanelSize math in PanelScreenFit shows why the aspect ratio of the
            // reference matters more than its pixel count: against a 16:10 reference, a 16:9 window
            // exposes only ~854 panel units of height no matter how large the screen is, which
            // cannot fit the side-by-side layout and forced every 16:9 window into the stacked one.
            panelSettings.referenceResolution = new Vector2Int(1600, 900);
            panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panelSettings.match = 0.5f;
            panelSettings.sortingOrder = 10;
            panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("LifeRuntimeTheme");

            UIDocument document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            BuildInterface(document.rootVisualElement);

            // USS cannot express media queries, so the responsive switch is driven from code by
            // watching the root's resolved width.
            document.rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            ApplyCompactClass(document.rootVisualElement);

            LoadPattern(selectedPattern);
        }

        /// <summary>Toggles the stacked layout when the panel cannot host two columns.</summary>
        private static void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            ApplyCompactClass(evt.target as VisualElement);
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
            if (!running)
                return;

            accumulator += Time.unscaledDeltaTime;
            float interval = 1f / speedSlider.value;
            while (accumulator >= interval)
            {
                accumulator -= interval;
                simulation.Step();
                RefreshReadouts();
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
            topLine.Add(Label("CELLULAR AUTOMATA / B3 · S23", "micro"));
            machine.Add(topLine);

            VisualElement workspace = Element("workspace");
            VisualElement display = Element("display");
            VisualElement screenBar = Element("screen-bar");
            sampleLabel = Label(string.Empty, "micro");
            screenBar.Add(sampleLabel);
            screenBar.Add(Label($"{GridWidth} × {GridHeight} / LIVE FIELD", "micro"));
            display.Add(screenBar);

            grid = new LifeGridElement { name = "life-grid" };
            grid.AddToClassList("life-grid");
            grid.Bind(simulation);
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
            boundary.RegisterValueChangedCallback(evt => simulation.WrapEdges = evt.newValue == "环绕边界");
            library.Add(boundary);
            workspace.Add(library);
            machine.Add(workspace);

            VisualElement controls = Element("controls");
            playButton = Button("▶ 运行", ToggleRunning, "control", "primary");
            controls.Add(playButton);
            controls.Add(Button("▸ 单步", StepOnce, "control"));
            controls.Add(Button("↺ 重置", () => LoadPattern(selectedPattern), "control"));
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

        private void LoadPattern(int index)
        {
            selectedPattern = index;
            Stop();
            LifePattern pattern = LifePatterns.All[index];
            simulation.LoadCentered(pattern.Cells);
            sampleLabel.text = $"样本 {index + 1:00} / {pattern.Name} · {pattern.EnglishName}";
            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].EnableInClassList("selected", i == index);
            grid.MarkDirtyRepaint();
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
            simulation.Step();
            grid.MarkDirtyRepaint();
            RefreshReadouts();
        }

        private void Randomize()
        {
            Stop();
            simulation.Randomize(0.22f, new System.Random());
            SelectCustom("自由样本 / 随机播种");
        }

        private void Clear()
        {
            Stop();
            simulation.Clear();
            SelectCustom("自由样本 / 空白");
        }

        private void OnGridEdited()
        {
            Stop();
            SelectCustom("自由样本 / 手动编辑");
        }

        private void SelectCustom(string name)
        {
            sampleLabel.text = name;
            for (int i = 0; i < presetButtons.Length; i++)
                presetButtons[i].RemoveFromClassList("selected");
            grid.MarkDirtyRepaint();
            RefreshReadouts();
        }

        private void RefreshReadouts()
        {
            generationLabel.text = simulation.Generation.ToString("0000");
            populationLabel.text = simulation.Population.ToString("0000");
            grid.MarkDirtyRepaint();
        }

        private void RefreshState()
        {
            playButton.text = running ? "Ⅱ 暂停" : "▶ 运行";
            stateLabel.text = running ? "演算中" : "已暂停";
            statusLabel.text = running ? "●  演算进行中" : "●  待机中";
        }
    }
}
