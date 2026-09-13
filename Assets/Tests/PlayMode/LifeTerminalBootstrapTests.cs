using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// PlayMode coverage for the presentation layer: the runtime bootstrap must build a complete
    /// interface, and the real UI controls must actually drive the simulation.
    ///
    /// This is the layer EditMode tests cannot reach. A malformed style sheet, a missing resource,
    /// or a null reference inside Awake all surface here. Any logged error fails these tests
    /// automatically, so a silent regression cannot slip through.
    ///
    /// Note: elements are looked up by CSS class via Q(name: null, className: ...), because the
    /// controller tags regions with AddToClassList rather than assigning element names.
    /// </summary>
    public sealed class LifeTerminalBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_BuildsCompleteInterface()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Assert.Greater(root.childCount, 0, "the interface was never built (root has no children)");
            Assert.IsTrue(root.ClassListContains("app"), "root is missing the 'app' class");

            // Every layout region the controller assembles.
            foreach (string region in new[] { "header", "machine", "workspace", "display", "library", "controls", "footer" })
            {
                Assert.IsNotNull(FindByClass(root, region), $"missing layout region '{region}'");
            }

            // Title and specimen archive.
            Label title = FindByClass(root, "title") as Label;
            Assert.IsNotNull(title, "missing title label");
            Assert.AreEqual("生命演算所", title.text);

            VisualElement library = FindByClass(root, "library");
            Assert.AreEqual(LifePatterns.All.Length, library.Query<Button>(className: "preset").ToList().Count,
                "the specimen archive should expose one button per pattern");

            // The grid must exist and be a bound LifeGridElement.
            VisualElement grid = FindByClass(root, "life-grid");
            Assert.IsNotNull(grid, "missing 'life-grid'");
            Assert.IsInstanceOf<LifeGridElement>(grid);

            // Three readouts: generation, population, state.
            Assert.AreEqual(3, FindByClass(root, "readouts").Query<VisualElement>(className: "readout").ToList().Count,
                "expected generation / population / state readouts");
        }

        [UnityTest]
        public IEnumerator Bootstrap_LoadsTheRuntimeThemeAndAppliesStyles()
        {
            yield return Settle();

            UIDocument document = Document();
            Assert.IsNotNull(document.panelSettings, "UIDocument has no PanelSettings");

            ThemeStyleSheet theme = document.panelSettings.themeStyleSheet;
            Assert.IsNotNull(theme,
                "PanelSettings.themeStyleSheet is null - Resources/LifeRuntimeTheme.tss failed to load");
            Assert.AreEqual("LifeRuntimeTheme", theme.name,
                "PanelSettings is using an unexpected theme asset");

            // Layout is the real proof that styles applied: the grid only gets a non-zero rect if
            // LifeTerminal.uss loaded and the flex layout resolved. A broken style sheet leaves
            // every region at zero size, which would still pass a pure hierarchy check.
            VisualElement grid = FindByClass(GetRoot(), "life-grid");
            Assert.Greater(grid.worldBound.width, 0f, "the grid has no width - styles/layout did not apply");
            Assert.Greater(grid.worldBound.height, 0f, "the grid has no height - styles/layout did not apply");

            VisualElement display = FindByClass(GetRoot(), "display");
            Assert.Greater(display.worldBound.width, 0f, "the display panel collapsed to zero width");
        }

        [UnityTest]
        public IEnumerator Bootstrap_IsIdempotent()
        {
            yield return Settle();

            LifeTerminalController[] controllers = UnityEngine.Object.FindObjectsByType<LifeTerminalController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert.AreEqual(1, controllers.Length, "the runtime bootstrap created more than one controller");
        }

        [UnityTest]
        public IEnumerator SteppingThroughTheInterface_AdvancesTheGenerationCounter()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Label generation = ReadoutValue(root, "GENERATION");
            Assert.IsNotNull(generation, "could not find the GENERATION readout");

            // Reset first: the controller survives across PlayMode tests (DontDestroyOnLoad and the
            // bootstrap guard), so a previous test may have left the board mid-evolution. Tests must
            // establish their own baseline instead of assuming a pristine one.
            Press(FindButton(root, "↺ 重置"));
            yield return null;
            Assert.AreEqual("0000", generation.text, "reset should return the terminal to generation 0000");

            Button step = FindButton(root, "▸ 单步");
            Assert.IsNotNull(step, "could not find the single-step button");

            Press(step);
            Assert.AreEqual("0001", generation.text, "single-step did not advance the generation counter");

            Press(step);
            Assert.AreEqual("0002", generation.text, "single-step did not advance the generation counter again");
        }

        [UnityTest]
        public IEnumerator LoadingAPreset_UpdatesTheReadouts()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            List<Button> presets = FindByClass(root, "library").Query<Button>(className: "preset").ToList();
            Assert.AreEqual(LifePatterns.All.Length, presets.Count);

            Label population = ReadoutValue(root, "POPULATION");
            Label generation = ReadoutValue(root, "GENERATION");

            // Load each preset in turn; the population readout must match the pattern's cell count.
            for (int i = 0; i < LifePatterns.All.Length; i++)
            {
                Press(presets[i]);
                yield return null;

                LifePattern pattern = LifePatterns.All[i];
                Assert.AreEqual(pattern.Cells.Length.ToString("0000"), population.text,
                    $"loading '{pattern.EnglishName}' did not update the population readout");
                Assert.AreEqual("0000", generation.text,
                    $"loading '{pattern.EnglishName}' should reset the generation counter");
            }
        }

        [UnityTest]
        public IEnumerator RunButton_StartsAndStopsTheSimulation()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Label status = FindByClass(root, "status") as Label;
            Button play = FindButton(root, "▶ 运行");
            Assert.IsNotNull(status, "missing status label");
            Assert.IsNotNull(play, "missing play button");
            Assert.AreEqual("●  待机中", status.text);

            Press(play);
            Assert.AreEqual("●  演算进行中", status.text, "clicking play should start the simulation");
            Assert.AreEqual("Ⅱ 暂停", play.text, "the button should now offer to pause");

            Press(play);
            Assert.AreEqual("●  待机中", status.text, "clicking again should pause the simulation");
            Assert.AreEqual("▶ 运行", play.text, "the button should offer to play again");
        }

        [UnityTest]
        public IEnumerator Running_ActuallyAdvancesGenerationsAcrossFrames()
        {
            // The strongest available behavioural assertion: load a pattern that cannot be stable,
            // start the clock, wait real frames, and require the generation counter to grow.
            // A controller whose Update() never steps would pass a text-only check but fail here.
            yield return Settle();

            VisualElement root = GetRoot();
            Label generation = ReadoutValue(root, "GENERATION");
            Label population = ReadoutValue(root, "POPULATION");

            // Blinker: period 2, never stable, so any running clock must advance it.
            Press(FindPreset(root, "BLINKER"));
            yield return null;
            Assert.AreEqual("0003", population.text, "blinker should load 3 live cells");

            Button play = FindButton(root, "▶ 运行");
            Press(play);

            // Wait on wall-clock time, not a frame count: the clock runs at 5 generations/second
            // in unscaled time, so give it well over one generation period. The loop is bounded by
            // frameCount so a stalled editor fails the assertion instead of hanging the test run.
            float deadline = Time.realtimeSinceStartup + 4f;
            int startFrame = Time.frameCount;
            while (Time.realtimeSinceStartup < deadline
                   && ParseReadout(generation) == 0
                   && Time.frameCount - startFrame < 2000)
            {
                yield return null;
            }

            int frames = Time.frameCount - startFrame;
            Assert.Greater(ParseReadout(generation), 0,
                $"the generation counter stayed at 0 after {frames} frames / 4s - the clock is not advancing the board");

            // Pause, then confirm the counter stops moving.
            Press(play);
            Assert.AreEqual("●  待机中", (FindByClass(root, "status") as Label).text);

            int atPause = ParseReadout(generation);
            float pauseDeadline = Time.realtimeSinceStartup + 1.5f;
            int pauseFrames = Time.frameCount;
            while (Time.realtimeSinceStartup < pauseDeadline && Time.frameCount - pauseFrames < 600)
            {
                yield return null;
            }

            Assert.Greater(Time.frameCount - pauseFrames, 5, "not enough frames elapsed to judge pausing");
            Assert.AreEqual(atPause, ParseReadout(generation),
                "the generation counter kept advancing while paused");
            Assert.AreEqual("0003", population.text,
                "a blinker must still have exactly 3 live cells after oscillating");
        }

        [UnityTest]
        public IEnumerator EditingTheBoard_PaintsThroughTheRealElementAndRefreshesTheReadout()
        {
            // Drives the actual LifeGridElement.Paint path (the same one a mouse click reaches):
            // bind a simulation, paint a cell, and require the element to fire Edited and the
            // controller to refresh its readouts. Coordinate mapping itself is not asserted here
            // because UI Toolkit pointer events cannot be constructed from a test assembly
            // (the PointerEventBase setters are internal and GetPooled's overloads are internal);
            // this exercises the editor->controller wiring instead.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeGridElement grid = FindByClass(root, "life-grid") as LifeGridElement;
            Assert.IsNotNull(grid, "missing LifeGridElement");

            Label population = ReadoutValue(root, "POPULATION");
            Button randomize = FindButton(root, "随机播种");
            Button clear = FindButton(root, "清空");
            Assert.IsNotNull(randomize, "missing randomize button");
            Assert.IsNotNull(clear, "missing clear button");

            var sim = new LifeSimulation(24, 24);
            grid.Bind(sim);

            bool editedFired = false;
            grid.Edited = () => editedFired = true;

            PaintCellForTest(grid, 3, 4);
            Assert.IsTrue(editedFired, "painting a cell must notify the controller through Edited");
            Assert.IsTrue(sim.IsAlive(3, 4), "the painted cell should be alive");
            Assert.AreEqual(1, sim.Population, "painting one cell should raise the population to 1");

            // The controller must also stop running and refresh when an edit arrives.
            grid.Edited = () => { };
            Press(clear);
            yield return null;
            Assert.AreEqual("0000", population.text, "clearing must refresh the population readout");
        }

        [UnityTest]
        public IEnumerator EveryPresetButton_IsVisibleAndInsideItsLibraryPanel()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            VisualElement library = FindByClass(root, "library");
            List<Button> presets = library.Query<Button>(className: "preset").ToList();
            Assert.AreEqual(LifePatterns.All.Length, presets.Count);

            // Non-zero size is not enough: a control can be laid out but sit outside its panel
            // (clipped) or on top of the boundary dropdown. Check containment and stacking.
            VisualElement boundary = FindByClass(root, "dropdown");
            Assert.IsNotNull(boundary, "missing the boundary-condition dropdown");

            Rect panel = library.worldBound;
            Assert.Greater(panel.height, 0f, "the library panel collapsed");

            foreach (Button preset in presets)
            {
                Rect bounds = preset.worldBound;
                Assert.Greater(bounds.width, 0f, $"preset '{preset.text}' has no width");
                Assert.Greater(bounds.height, 0f, $"preset '{preset.text}' has no height");

                Assert.GreaterOrEqual(bounds.yMin, panel.yMin - 0.5f,
                    $"preset '{preset.text}' is above its panel");
                Assert.LessOrEqual(bounds.yMax, panel.yMax + 0.5f,
                    $"preset '{preset.text}' overflows the bottom of the library panel");

                Assert.LessOrEqual(bounds.yMax, boundary.worldBound.yMin + 0.5f,
                    $"preset '{preset.text}' overlaps the boundary-condition dropdown");
            }
        }

        [UnityTest]
        public IEnumerator Controls_AreAllInsideThePanelViewport()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            VisualElement panelTree = root.panel.visualTree;

            // Two spaces are in play and mixing them is a category error:
            //   worldBound   -> panel space (the 1440x900 reference resolution)
            //   Screen.*     -> physical pixels, after the panel's fit transform
            // Containment is therefore checked in panel space, against the viewport that is
            // actually visible (the full panel extent can exceed it on a short window).
            Rect viewport = panelTree.worldBound;
            Assert.Greater(viewport.width, 0f, "the panel has no width");
            Assert.Greater(viewport.height, 0f, "the panel has no height");

            float visibleTop = -panelTree.worldBound.yMin; // panel space starts at its own origin
            foreach (string region in new[] { "header", "machine", "controls", "footer", "display", "library" })
            {
                Rect bounds = FindByClass(root, region).worldBound;

                Assert.GreaterOrEqual(bounds.xMin, viewport.xMin - 0.5f, $"'{region}' starts left of the panel");
                Assert.LessOrEqual(bounds.xMax, viewport.xMax + 0.5f, $"'{region}' extends past the right edge");
                Assert.GreaterOrEqual(bounds.yMin, -0.5f, $"'{region}' starts above the panel origin");
                Assert.LessOrEqual(bounds.yMax, viewport.height + 0.5f, $"'{region}' extends past the bottom edge");
            }

            // The machine block must be fully inside the region of the panel that is actually
            // visible on screen. This is the real clipping test: on a short window the panel is
            // taller than the viewport (the fit transform pushes its bottom off-screen), and
            // worldBound alone cannot reveal that because it is expressed in panel space.
            Rect machineBounds = FindByClass(root, "machine").worldBound;
            float k = Screen.width / viewport.width; // panel units -> screen pixels
            float machineBottomOnScreen = machineBounds.yMax * k;

            Assert.LessOrEqual(machineBottomOnScreen, Screen.height + 0.5f,
                $"the machine block ends at {machineBottomOnScreen:F0}px on a {Screen.height}px-tall screen, " +
                "so the controls would be clipped");

            // The display must fit inside the machine so the grid is never cropped.
            Rect display = FindByClass(root, "display").worldBound;
            Assert.GreaterOrEqual(display.yMin, machineBounds.yMin - 0.5f, "the display starts above the machine");
            Assert.LessOrEqual(display.yMax, machineBounds.yMax + 0.5f, "the display overflows the machine");

            // Documented contract: the layout is authored for a reference height of 900px
            // (16:10). It is verified to fit 1280x720; the decorative footer is what gets squeezed
            // first on shorter windows, which is recorded in PROJECT_LOG.
            Debug.Log($"[layout-fit] screen={Screen.width}x{Screen.height} panel={viewport.width:F0}x{viewport.height:F0} " +
                      $"scale={k:F4} machineBottomOnScreen={machineBottomOnScreen:F0} contentHeight={machineBounds.yMax + FindByClass(root, "footer").worldBound.height:F0}");
        }

        [UnityTest]
        public IEnumerator Performance_MeasureTheClockInsideTheEditorRuntime()
        {
            // GPT's review point 5: the standalone .NET harness numbers are NOT Unity player numbers.
            // This measures the same work inside the editor runtime, driven through the controller's
            // own clock, so the figure is attributable to the runtime that actually ships.
            //
            // Scope: rule advancement plus the per-generation readout refresh. It does NOT measure
            // the Painter2D grid repaint, which is deferred to the panel update and is not reachable
            // from a test - the Unity Profiler is required for that (see PROJECT_LOG T2).
            yield return Settle();

            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller);

            FieldInfo runningField = typeof(LifeTerminalController).GetField(
                "running", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(runningField, "LifeTerminalController.running field not found");

            const float speed = 1000f; // generations/second: as fast as the accumulator allows
            const int steps = 2000;

            // Warm up so JIT and the readout labels are not part of the measurement.
            for (int i = 0; i < 200; i++)
            {
                controller.SendMessage("StepOnce", SendMessageOptions.DontRequireReceiver);
            }

            yield return null;

            FieldInfo speedField = typeof(LifeTerminalController).GetField(
                "speedSlider", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(speedField, "LifeTerminalController.speedSlider field not found");
            SliderInt speedSlider = (SliderInt)speedField.GetValue(controller);
            speedSlider.value = (int)speed;

            int genBefore = ReadGeneration(controller);

            runningField.SetValue(controller, true);

            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            for (int i = 0; i < steps; i++)
            {
                controller.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
            }

            sw.Stop();
            runningField.SetValue(controller, false);

            int advanced = ReadGeneration(controller) - genBefore;

            double perStepMs = sw.Elapsed.TotalMilliseconds / steps;
            double perGenerationMs = advanced > 0 ? sw.Elapsed.TotalMilliseconds / advanced : double.NaN;

            Debug.Log($"[perf] editor runtime, board 96x64 (6144 cells): " +
                      $"{steps} clock ticks advanced {advanced} generations " +
                      $"in {sw.Elapsed.TotalMilliseconds:F1} ms " +
                      $"({perStepMs:F4} ms/tick, {perGenerationMs:F4} ms/generation)");

            Assert.Greater(advanced, 0, "the clock did not advance any generation, so nothing was measured");

            // Sanity bound: the UI clock must stay far under a 60 fps frame budget (16.6 ms).
            // Allocation is deliberately not asserted here: GC.GetTotalAllocatedBytes is unavailable
            // in Unity's runtime and GC.GetTotalMemory is a coarse heap gauge, not an allocator, so
            // it cannot honestly bound per-generation garbage.
            Assert.Less(perStepMs, 8.0,
                $"{perStepMs:F4} ms per clock tick is too slow for a 60 fps budget");
        }

        private static int ReadGeneration(LifeTerminalController controller)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "simulation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeTerminalController.simulation field not found");
            return ((LifeSimulation)field.GetValue(controller)).Generation;
        }

        // --- helpers ----------------------------------------------------------

        private static int ParseReadout(Label label)
        {
            return int.TryParse(label.text, out int value) ? value : -1;
        }

        private static Button FindPreset(VisualElement root, string englishName)
        {
            LifePattern pattern = null;
            foreach (LifePattern candidate in LifePatterns.All)
            {
                if (candidate.EnglishName == englishName)
                {
                    pattern = candidate;
                    break;
                }
            }

            Assert.IsNotNull(pattern, $"pattern '{englishName}' not found");
            return FindButton(root, $"{pattern.Name}\n{KindCaption(pattern)}");
        }

        private static string KindCaption(LifePattern pattern)
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

        /// <summary>
        /// Invokes LifeGridElement's test seam reflectively.
        ///
        /// The production member is internal, so the test assembly cannot bind to it directly
        /// without an InternalsVisibleTo declaration. Reflection keeps the shipped API clean
        /// instead of widening a method to public purely for testing.
        /// </summary>
        private static void PaintCellForTest(LifeGridElement grid, int x, int y)
        {
            MethodInfo seam = typeof(LifeGridElement).GetMethod(
                "PaintForTest",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(seam, "LifeGridElement.PaintForTest seam is missing - the edit path is untestable");
            seam.Invoke(grid, new object[] { x, y });
        }

        /// <summary>Lets RuntimeInitializeOnLoadMethod run and the panel complete its first layout.</summary>
        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

        private static UIDocument Document()
        {
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");

            UIDocument document = controller.GetComponent<UIDocument>();
            Assert.IsNotNull(document, "controller has no UIDocument");
            return document;
        }

        private static VisualElement GetRoot()
        {
            VisualElement root = Document().rootVisualElement;
            Assert.IsNotNull(root, "UIDocument has no root visual element");
            return root;
        }

        private static VisualElement FindByClass(VisualElement scope, string className)
        {
            return scope.Q(name: null, className: className);
        }

        private static Button FindButton(VisualElement scope, string text)
        {
            foreach (Button button in scope.Query<Button>().ToList())
            {
                if (button.text == text)
                {
                    return button;
                }
            }

            return null;
        }

        /// <summary>
        /// Presses a button the way the runtime does. Clickable.click() is internal to
        /// UnityEngine.UIElements, so the public path is to dispatch the submit event that a
        /// Button listens for.
        /// </summary>
        private static void Press(Button button)
        {
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        /// <summary>Finds a readout's value label by its caption, e.g. "GENERATION / 世代".</summary>
        private static Label ReadoutValue(VisualElement root, string captionPrefix)
        {
            foreach (VisualElement readout in FindByClass(root, "readouts").Query<VisualElement>(className: "readout").ToList())
            {
                Label caption = FindByClass(readout, "readout-caption") as Label;
                if (caption != null && caption.text.StartsWith(captionPrefix))
                {
                    return readout.Query<Label>().ToList().Find(label => label != caption);
                }
            }

            return null;
        }
    }
}
