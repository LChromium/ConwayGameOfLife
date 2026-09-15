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
        public IEnumerator GridElement_PaintsAndRaisesEdited_OnItsOwnObject()
        {
            // Independent grid test: uses a detached LifeGridElement and its own simulation, so it
            // cannot disturb the live interface. Painting must mutate that simulation and raise
            // Edited exactly once per real state change.
            yield return Settle();

            var grid = new LifeGridElement();
            var backend = new CpuLifeBackend(24, 24);
            grid.Bind(backend);

            int editedCount = 0;
            grid.Edited = () => editedCount++;

            PaintCellForTest(grid, 3, 4);

            Assert.AreEqual(1, editedCount, "painting a cell must raise Edited once");
            Assert.AreEqual(1u, ReadCells(backend)[4 * 24 + 3], "the painted cell should be alive");
            Assert.AreEqual(1, PopulationOf(backend), "painting one cell should raise the population to 1");

            // Painting the same cell again toggles it back off and notifies again.
            PaintCellForTest(grid, 3, 4);
            Assert.AreEqual(2, editedCount, "toggling a cell must raise Edited again");
            Assert.AreEqual(0u, ReadCells(backend)[4 * 24 + 3], "the second paint should clear the cell");
            Assert.AreEqual(0, PopulationOf(backend));

            // Out-of-range coordinates must be ignored silently, without a notification.
            PaintCellForTest(grid, -1, 0);
            PaintCellForTest(grid, 24, 24);
            Assert.AreEqual(2, editedCount, "out-of-range paints must not notify");
        }

        [UnityTest]
        public IEnumerator EditingTheRealBoard_PausesTheClockAndRefreshesTheReadout()
        {
            // Integration test: keeps the CONTROLLER'S OWN simulation and its own Edited callback.
            // The previous version of this test bound a second simulation and overwrote grid.Edited,
            // which replaced the very wiring it claimed to verify.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            LifeGridElement grid = FindByClass(root, "life-grid") as LifeGridElement;
            Assert.IsNotNull(grid, "missing LifeGridElement");

            // Stage A only publishes a population from the CPU backend, so this test
            // pins the backend to CPU. The GPU readout is covered by its own test.
            SelectBackend(root, gpu: false);
            yield return null;

            // Deterministic starting point: load a known pattern explicitly rather than assuming
            // whichever specimen the previous test happened to leave selected.
            Press(FindPreset(root, "PENTADECATHLON"));
            yield return null;

            Label population = ReadoutValue(root, "POPULATION");
            Label state = ReadoutValue(root, "STATE");
            Label generation = ReadoutValue(root, "GENERATION");

            ILifeBackend live = ReadBackend(controller);
            Assert.AreSame(live, ReadBoundBackend(grid),
                "the grid must be bound to the controller's own backend");
            Assert.AreEqual(12, PopulationOf(live), "expected the pentadecathlon's 12 cells");
            Assert.AreEqual("0012", population.text);

            // Start the clock so we can prove an edit pauses it.
            Press(FindButton(root, "▶ 运行"));
            Assert.AreEqual("演算中", state.text, "the clock should be running");

            // Edit one cell through the real element, leaving the controller's callback in place.
            PaintCellForTest(grid, 0, 0);
            yield return null;

            Assert.IsFalse(ReadRunning(controller), "editing the board must pause the clock");
            Assert.AreEqual("已暂停", state.text, "the state readout should report paused");
            Assert.AreEqual("0000", generation.text, "editing should not advance the generation counter");
            Assert.AreEqual(13, PopulationOf(live), "the edit should have added one live cell to the real board");
            Assert.AreEqual("0013", population.text, "the population readout must reflect the real board");
            Assert.AreEqual("自由样本 / 手动编辑", ReadSampleCaption(root),
                "the sample caption should switch to the custom-edit label");

            // Restore the interface for the tests that follow.
            Press(FindButton(root, "↺ 重置"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator GpuBackend_ReportsUnavailableStatisticsInsteadOfGuessing()
        {
            // Stage A implements no GPU statistics. The population readout has to
            // say so rather than show an invented or stale number.
            yield return Settle();

            VisualElement root = GetRoot();
            DropdownField field = root.Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");

            if (!field.enabledSelf)
            {
                Assert.Ignore("compute shaders unavailable on this machine; the GPU path was not exercised");
            }

            SelectBackend(root, gpu: true);
            yield return null;

            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            ILifeBackend live = ReadBackend(controller);

            Assert.AreEqual("GPU", live.Name, "the selector did not switch the backend to GPU");
            Assert.IsFalse(live.TryGetPopulation(out _),
                "stage A expects the GPU backend to refuse a population query");

            Assert.AreEqual("—", ReadoutValue(root, "POPULATION").text,
                "the population readout must read as unavailable, not as a number");

            // The generation counter is tracked CPU-side and still works.
            Press(FindButton(root, "▸ 单步"));
            yield return null;
            Assert.AreEqual("0001", ReadoutValue(root, "GENERATION").text,
                "stepping on the GPU backend should advance the generation counter");

            // Leave the interface on CPU for whatever runs next.
            SelectBackend(root, gpu: false);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PresetArchive_ScrollsWithoutOverlappingTheBoundaryControl()
        {
            // The archive is a ScrollView, so off-screen entries are SUPPOSED to be clipped - an
            // assertion that every button sits inside the panel would contradict the scroll itself.
            // What must hold instead: the scroll viewport never covers the boundary dropdown, and
            // every entry can be scrolled into view and is then actually selectable.
            yield return Settle();

            VisualElement root = GetRoot();
            ScrollView archive = FindByClass(root, "preset-scroll") as ScrollView;
            Assert.IsNotNull(archive, "missing the 'preset-scroll' ScrollView");

            VisualElement boundary = FindByClass(root, "dropdown");
            Assert.IsNotNull(boundary, "missing the boundary-condition dropdown");

            // The viewport must sit entirely above the dropdown (panel space: y grows downward).
            Assert.LessOrEqual(archive.worldBound.yMax, boundary.worldBound.yMin + 0.5f,
                "the preset scroll viewport overlaps the boundary-condition dropdown");

            List<Button> presets = archive.Query<Button>(className: "preset").ToList();
            Assert.AreEqual(LifePatterns.All.Length, presets.Count,
                "the archive should hold one button per pattern");

            Label population = ReadoutValue(root, "POPULATION");
            Rect viewport = archive.worldBound;

            for (int i = 0; i < presets.Count; i++)
            {
                Button preset = presets[i];

                // A button that starts off-screen is fine; scroll it in and require it to arrive.
                archive.scrollOffset = new Vector2(0f, Mathf.Max(0f, preset.layout.yMax - viewport.height));
                yield return null;

                Rect bounds = preset.worldBound;
                Assert.GreaterOrEqual(bounds.yMin, viewport.yMin - 0.5f,
                    $"'{preset.text}' could not be scrolled fully into the top of the viewport");
                Assert.LessOrEqual(bounds.yMax, viewport.yMax + 0.5f,
                    $"'{preset.text}' could not be scrolled fully into the viewport");
                Assert.Greater(bounds.height, 0f, $"'{preset.text}' has no height");
                Assert.Greater(bounds.width, 0f, $"'{preset.text}' has no width");

                // Scrolled into view, it must be selectable and load its own pattern.
                Press(preset);
                yield return null;

                Assert.IsTrue(preset.ClassListContains("selected"),
                    $"'{preset.text}' did not become the selected specimen after being pressed");
                Assert.AreEqual(LifePatterns.All[i].Cells.Length.ToString("0000"), population.text,
                    $"selecting '{preset.text}' did not load its pattern");
            }
        }

        [UnityTest]
        public IEnumerator Controls_AreAllInsideThePanelViewport()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            VisualElement panelTree = root.panel.visualTree;

            // Two spaces are in play and mixing them is a category error:
            //   worldBound   -> panel space (the design resolution, currently 1600x900)
            //   Screen.*     -> physical pixels, after the panel's fit transform
            // Containment is therefore checked in panel space, against the viewport that is
            // actually visible (on a short window the panel can extend past the viewport).
            Rect viewport = panelTree.worldBound;
            Assert.Greater(viewport.width, 0f, "the panel has no width");
            Assert.Greater(viewport.height, 0f, "the panel has no height");

            float visibleTop = -panelTree.worldBound.yMin; // panel space starts at its own origin
            _ = visibleTop;
            foreach (string region in new[] { "header", "machine", "controls", "footer", "display", "library" })
            {
                Rect bounds = FindByClass(root, region).worldBound;

                Assert.GreaterOrEqual(bounds.xMin, viewport.xMin - 0.5f, $"'{region}' starts left of the panel");
                Assert.LessOrEqual(bounds.xMax, viewport.xMax + 0.5f, $"'{region}' extends past the right edge");
                Assert.GreaterOrEqual(bounds.yMin, -0.5f, $"'{region}' starts above the panel origin");
                Assert.LessOrEqual(bounds.yMax, viewport.height + 0.5f, $"'{region}' extends past the bottom edge");
            }

            // Regression guard for a real defect: in the narrow layout the controls row ran off the
            // right edge and clipped "清空" completely (visible in the 600x1000 Player capture). The
            // region check above cannot catch that, because what overflows is a button INSIDE the
            // row - the row's own box still fits.
            List<Button> controls = FindByClass(root, "controls").Query<Button>(className: "control").ToList();
            Assert.GreaterOrEqual(controls.Count, 5,
                "expected the transport controls (run / step / reset / randomize / clear)");

            foreach (Button control in controls)
            {
                Rect bounds = control.worldBound;

                Assert.Greater(bounds.width, 0f, $"control '{control.text}' has no width");
                Assert.GreaterOrEqual(bounds.xMin, -0.5f, $"control '{control.text}' starts left of the panel");
                Assert.LessOrEqual(bounds.xMax, viewport.xMax + 0.5f,
                    $"control '{control.text}' extends past the right edge and would be clipped");
                Assert.LessOrEqual(bounds.yMax, viewport.height + 0.5f,
                    $"control '{control.text}' extends past the bottom edge and would be clipped");
            }

            Button clearButton = FindButton(root, "清空");
            Assert.IsNotNull(clearButton, "missing the '清空' button");
            Assert.LessOrEqual(clearButton.worldBound.xMax, viewport.xMax + 0.5f,
                "the '清空' button is clipped by the right edge");

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

            // The design space is 1600x900 and is scaled to fit the target viewport. These figures
            // are from the PlayMode host, which runs at 640x480 - a resolution the layout was not
            // authored for - so they only prove the fit transform behaves; the 1280x720 and
            // 1920x1080 cases are covered by PanelScreenFit unit tests and by Player captures.
            Debug.Log($"[layout-fit] screen={Screen.width}x{Screen.height} panel={viewport.width:F0}x{viewport.height:F0} " +
                      $"scale={k:F4} machineBottomOnScreen={machineBottomOnScreen:F0}");
        }

        [UnityTest]
        public IEnumerator Clock_RunsAtTheRequestedRateAcrossRealFrames()
        {
            // Real elapsed time, real frames, the controller's own Update(). No SendMessage, no
            // repeated reuse of one frame's deltaTime. The ground truth is the GENERATION COUNTER,
            // not a stopwatch around synthetic calls.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();

            Press(FindButton(root, "↺ 重置"));
            yield return null;

            FieldInfo speedField = typeof(LifeTerminalController).GetField(
                "speedSlider", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(speedField, "LifeTerminalController.speedSlider field not found");
            SliderInt speedSlider = (SliderInt)speedField.GetValue(controller);

            const int requestedRate = 20; // the slider's documented maximum
            speedSlider.value = requestedRate;
            yield return null;

            Label generation = ReadoutValue(root, "GENERATION");
            Button play = FindButton(root, "▶ 运行");

            Press(play);
            int genAtStart = ReadGeneration(controller);
            float start = Time.realtimeSinceStartup;
            int startFrame = Time.frameCount;

            const float window = 2.0f;
            float worstFrame = 0f;
            while (Time.realtimeSinceStartup - start < window && Time.frameCount - startFrame < 2000)
            {
                yield return null;
                float frameSeconds = Time.unscaledDeltaTime;
                if (frameSeconds > worstFrame)
                {
                    worstFrame = frameSeconds;
                }
            }

            float elapsed = Time.realtimeSinceStartup - start;
            int frames = Time.frameCount - startFrame;
            int advanced = ReadGeneration(controller) - genAtStart;
            Press(play); // pause before reporting

            float achieved = advanced / elapsed;
            float meanFrameMs = elapsed * 1000f / Mathf.Max(frames, 1);
            float worstFrameMs = worstFrame * 1000f;

            // This is measured WHILE the board is evolving, which is the case the earlier Player
            // sample missed entirely (it sampled a paused terminal). The figures are frame DELTAS,
            // so they include presentation and any pacing - they are not "compute cost".
            Debug.Log($"[clock-running] requested={requestedRate} gen/s  achieved={achieved:F1} gen/s  " +
                      $"advanced={advanced} generations  elapsed={elapsed:F2}s over {frames} frames  " +
                      $"meanFrameMs={meanFrameMs:F3}  worstFrameMs={worstFrameMs:F3}  " +
                      $"runtime=Unity Editor PlayMode (NOT a Player build)");

            Assert.Greater(frames, 10, "not enough frames elapsed to judge the clock");
            Assert.Greater(advanced, 0, "the clock advanced no generations across real frames");

            // Time the rule step itself while the board is genuinely live. Pinned to
            // the CPU backend so the figure stays a CPU rule-engine number, which is
            // what stage 1 published. GPU rule cost is measured by the stage-A
            // comparison harness, not here.
            SelectBackend(GetRoot(), gpu: false);
            yield return null;

            ILifeBackend live = ReadBackend(controller);
            const int timedSteps = 2000;
            var stepWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < timedSteps; i++)
            {
                live.Step();
            }

            stepWatch.Stop();
            double msPerStep = stepWatch.Elapsed.TotalMilliseconds / timedSteps;
            Debug.Log($"[clock-running] CPU engine step cost while evolving: {msPerStep:F4} ms/generation " +
                      $"({timedSteps} steps, board {live.Width}x{live.Height} = {live.Width * live.Height} cells, " +
                      $"Unity Editor runtime)");

            // The accumulator drives generations from unscaledDeltaTime, so the achieved rate must
            // land near the requested rate (not below half, and never above double).
            Assert.Greater(achieved, requestedRate * 0.5f,
                $"the clock achieved only {achieved:F1} gen/s against a requested {requestedRate} gen/s");
            Assert.Less(achieved, requestedRate * 2f,
                $"the clock ran at {achieved:F1} gen/s, far above the requested {requestedRate} gen/s");

            Assert.LessOrEqual(worstFrameMs, 250f,
                $"worst frame was {worstFrameMs:F0} ms while evolving - a visible hitch");
        }

        [UnityTest]
        public IEnumerator MicroBenchmark_StepCostInsideTheEditorRuntime()
        {
            // EXPLICITLY A SYNTHETIC MICRO-BENCHMARK, not a picture of real gameplay:
            //   * every call reuses the same frame's unscaledDeltaTime, and
            //   * SendMessage adds reflection dispatch overhead per call.
            // It is reported as a labelled micro-benchmark only, and is deliberately NOT used to
            // characterise real-time performance. Cross-frame clock behaviour is covered by
            // Clock_RunsAtTheRequestedRateAcrossRealFrames; grid repaint needs the Unity Profiler.
            yield return Settle();

            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller);

            FieldInfo runningField = typeof(LifeTerminalController).GetField(
                "running", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(runningField, "LifeTerminalController.running field not found");

            // Warm up.
            for (int i = 0; i < 200; i++)
            {
                controller.SendMessage("StepOnce", SendMessageOptions.DontRequireReceiver);
            }

            yield return null;

            const int calls = 2000;
            int genBefore = ReadGeneration(controller);

            runningField.SetValue(controller, true);
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
            for (int i = 0; i < calls; i++)
            {
                controller.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
            }

            sw.Stop();
            runningField.SetValue(controller, false);

            int advanced = ReadGeneration(controller) - genBefore;
            Assert.Greater(advanced, 0, "the micro-benchmark advanced no generations");

            double msPerGeneration = sw.Elapsed.TotalMilliseconds / advanced;
            ILifeBackend benchBackend = ReadBackend(controller);
            Debug.Log($"[perf-microbench] SYNTHETIC micro-benchmark - NOT real-time gameplay. " +
                      $"Board {benchBackend.Width}x{benchBackend.Height} = {benchBackend.Width * benchBackend.Height} cells, " +
                      $"backend {benchBackend.Name}. {calls} synchronous Update() calls advanced {advanced} " +
                      $"generations in {sw.Elapsed.TotalMilliseconds:F1} ms => {msPerGeneration:F4} ms/generation. " +
                      $"Caveats: every call reuses one frame's unscaledDeltaTime, and SendMessage adds " +
                      $"reflection dispatch overhead per call. Runtime: Unity Editor 6000.6.0f1 PlayMode. " +
                      $"Cite this line as the source for any per-generation figure.");
        }

        private static int ReadGeneration(LifeTerminalController controller)
        {
            return ReadBackend(controller).Generation;
        }

        // --- helpers ----------------------------------------------------------

        private static int ParseReadout(Label label)
        {
            return int.TryParse(label.text, out int value) ? value : -1;
        }

        /// <summary>Reads the controller's private backend via reflection.</summary>
        private static ILifeBackend ReadBackend(LifeTerminalController controller)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "backend", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeTerminalController.backend field not found");
            return (ILifeBackend)field.GetValue(controller);
        }

        /// <summary>Reads the backend a LifeGridElement is bound to, via its private field.</summary>
        private static ILifeBackend ReadBoundBackend(LifeGridElement grid)
        {
            FieldInfo field = typeof(LifeGridElement).GetField(
                "backend", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeGridElement.backend field not found");
            return (ILifeBackend)field.GetValue(grid);
        }

        /// <summary>
        /// Full state readback. Used by presentation tests to check that a click
        /// really reached the board; the dedicated CPU/GPU equivalence evidence
        /// lives in the stage-A comparison harness, not here.
        /// </summary>
        private static uint[] ReadCells(ILifeBackend backend)
        {
            var cells = new uint[backend.Width * backend.Height];
            Assert.IsTrue(backend.TryReadAllCells(cells), "backend refused a full readback");
            return cells;
        }

        private static int PopulationOf(ILifeBackend backend)
        {
            Assert.IsTrue(backend.TryGetPopulation(out int population),
                $"{backend.Name} backend cannot report a population");
            return population;
        }

        /// <summary>Drives the backend selector the way a user would.</summary>
        private static void SelectBackend(VisualElement root, bool gpu)
        {
            DropdownField field = root.Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            field.value = gpu ? "GPU（Compute Shader）" : "CPU（参考实现）";
        }

        /// <summary>Reads the controller's private running flag via reflection.</summary>
        private static bool ReadRunning(LifeTerminalController controller)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "running", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeTerminalController.running field not found");
            return (bool)field.GetValue(controller);
        }

        /// <summary>
        /// The specimen caption currently shown in the screen bar. Several labels share the "micro"
        /// class, so this picks the one that starts with a known caption prefix.
        /// </summary>
        private static string ReadSampleCaption(VisualElement root)
        {
            foreach (VisualElement element in root.Query<VisualElement>(className: "micro").ToList())
            {
                if (element is Label label &&
                    (label.text.StartsWith("样本 ") || label.text.StartsWith("自由样本")))
                {
                    return label.text;
                }
            }

            return null;
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
