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
            panelSettings.referenceResolution = new Vector2Int(1440, 900);
            panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panelSettings.match = 0.5f;
            panelSettings.sortingOrder = 10;
            panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("LifeRuntimeTheme");

            UIDocument document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            BuildInterface(document.rootVisualElement);
            LoadPattern(selectedPattern);
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
            presetButtons = new Button[LifePatterns.All.Length];
            for (int i = 0; i < LifePatterns.All.Length; i++)
            {
                int index = i;
                LifePattern pattern = LifePatterns.All[i];
                Button button = new(() => LoadPattern(index));
                button.AddToClassList("preset");
                button.text = $"{pattern.Name}\n{KindName(pattern)}";
                library.Add(button);
                presetButtons[i] = button;
            }

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
