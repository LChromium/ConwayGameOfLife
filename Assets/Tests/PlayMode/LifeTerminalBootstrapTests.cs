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
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");

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

            yield return StepOnce(root, controller);
            Assert.AreEqual("0001", generation.text, "single-step did not advance the generation counter");

            yield return StepOnce(root, controller);
            Assert.AreEqual("0002", generation.text, "single-step did not advance the generation counter again");
        }

        [UnityTest]
        public IEnumerator LoadingAPreset_UpdatesTheReadouts()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            ShowPresetsTab(root);
            SelectCpuBackend(root);
            yield return null;

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

            // Start the clock so we can prove an edit pauses it. The state readout carries the
            // run's rate as well now, and a run whose first rate window has not closed yet says
            // it is sampling rather than reporting the initialised zero as a measurement -- so
            // the assertion is on the running prefix.
            Press(FindButton(root, "▶ 运行"));
            Assert.IsTrue(state.text.StartsWith("演算中"),
                $"the clock should be running, got '{state.text}'");

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
        public IEnumerator Reset_RestoresTheSameInitialState_AndNeverReseeds()
        {
            // The stage-A brief is explicit: "reset" restores the board this
            // experiment started from. It must NOT roll a new random board, and a
            // second reset must reproduce the same one.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();

            // Pinned to CPU so the population readout is answerable; the reset
            // contract itself lives in the controller and is backend-independent.
            SelectBackend(root, gpu: false);
            yield return null;

            Press(FindButton(root, "随机播种"));
            yield return null;

            ILifeBackend live = ReadBackend(controller);
            uint[] startingBoard = ReadCells(live);
            int startingPopulation = PopulationOf(live);
            Assert.Greater(startingPopulation, 0,
                "the random board came out empty, which would make this test vacuous");

            for (int i = 0; i < 3; i++)
                yield return StepOnce(root, controller);

            Assert.AreEqual("0003", ReadoutValue(root, "GENERATION").text,
                "three steps should advance three generations");

            Press(FindButton(root, "↺ 重置"));
            yield return null;

            Assert.AreEqual("0000", ReadoutValue(root, "GENERATION").text,
                "reset must return to generation 0");
            Assert.AreEqual(startingPopulation, PopulationOf(live),
                "reset must restore the starting population");
            CollectionAssert.AreEqual(startingBoard, ReadCells(live),
                "reset must restore the exact starting board");

            // Run forward again and reset a second time: a reseeding reset would
            // produce a different board here.
            for (int i = 0; i < 2; i++)
                yield return StepOnce(root, controller);

            Press(FindButton(root, "↺ 重置"));
            yield return null;

            CollectionAssert.AreEqual(startingBoard, ReadCells(live),
                "a second reset must reproduce the same board, not reseed");
            Assert.AreEqual(startingPopulation, PopulationOf(live),
                "a second reset must reproduce the same population");
        }

        [UnityTest]
        public IEnumerator SwitchingBackend_PausesAndRestartsFromTheSameInitialState()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            DropdownField field = root.Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            if (!field.enabledSelf)
            {
                Assert.Ignore("compute shaders unavailable on this machine; the switch was not exercised");
            }

            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();

            SelectBackend(root, gpu: true);
            yield return null;

            Press(FindPreset(root, "PENTADECATHLON"));
            yield return null;

            ILifeBackend gpuBackend = ReadBackend(controller);
            Assert.AreEqual("GPU", gpuBackend.Name, "the selector did not switch the backend to GPU");
            uint[] startingBoard = ReadCells(gpuBackend);

            for (int i = 0; i < 3; i++)
                yield return StepOnce(root, controller);

            Assert.AreEqual(3, gpuBackend.Generation, "the GPU backend should have advanced three generations");

            SelectBackend(root, gpu: false);
            yield return null;

            ILifeBackend cpuBackend = ReadBackend(controller);
            Assert.AreEqual("CPU", cpuBackend.Name, "the selector did not switch the backend to CPU");
            Assert.AreEqual(0, cpuBackend.Generation,
                "switching backends must restart from the initial state, not continue mid-run");
            CollectionAssert.AreEqual(startingBoard, ReadCells(cpuBackend),
                "the second backend must start from the same board the first one started from");
            Assert.AreEqual("已暂停", ReadoutValue(root, "STATE").text,
                "switching backends must leave the clock paused");

            // Leave the interface on GPU for whatever runs next.
            SelectBackend(root, gpu: true);
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
            ShowPresetsTab(root);
            SelectCpuBackend(root);
            yield return null;

            ScrollView archive = FindByClass(root, "preset-scroll") as ScrollView;
            Assert.IsNotNull(archive, "missing the 'preset-scroll' ScrollView");

            VisualElement boundary = root.Q<DropdownField>("boundary-field");
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

            // Time the rule step itself while the board is genuinely live. The engine is created
            // here rather than borrowed from the terminal: since stage D the terminal's CPU path
            // computes off the frame, so stepping IT would time submissions. LifeSimulation is
            // still the single source of truth for the rules, and this is the same
            // CpuLifeBackend wrapper the comparison harness uses.
            ILifeBackend live = ReadBackend(controller);
            using var engine = new CpuLifeBackend(live.Width, live.Height);
            engine.WrapEdges = live.WrapEdges;
            const int timedSteps = 2000;
            var stepWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < timedSteps; i++)
            {
                engine.Step();
            }

            stepWatch.Stop();
            double msPerStep = stepWatch.Elapsed.TotalMilliseconds / timedSteps;
            Debug.Log($"[clock-running] CPU engine step cost while evolving: {msPerStep:F4} ms/generation " +
                      $"({timedSteps} steps, board {engine.Width}x{engine.Height} = {engine.Width * engine.Height} cells, " +
                      $"reference backend called directly, Unity Editor runtime)");

            // The accumulator drives generations from unscaledDeltaTime, so the achieved rate must
            // land near the requested rate (not below half, and never above double).
            Assert.Greater(achieved, requestedRate * 0.5f,
                $"the clock achieved only {achieved:F1} gen/s against a requested {requestedRate} gen/s");
            Assert.Less(achieved, requestedRate * 2f,
                $"the clock ran at {achieved:F1} gen/s, far above the requested {requestedRate} gen/s");

            Assert.LessOrEqual(worstFrameMs, 250f,
                $"worst frame was {worstFrameMs:F0} ms while evolving - a visible hitch");
        }

        // The synthetic `MicroBenchmark_StepCostInsideTheEditorRuntime` used to live here. It
        // measured "2000 synchronous Update() calls advance N generations" on the CPU backend, and
        // stage 1 cited its `[perf-microbench]` line as the source of a per-generation cost.
        //
        // Stage D made that shape meaningless: a synchronous Update on the CPU path now SUBMITS a
        // generation and the worker retires it later, so the same loop would measure submission
        // plus controller-call overhead and then divide by adoptions that happened to land. Pinning
        // it to the GPU (an interim fix) measured something else again, and the test's name and its
        // historical citations would have kept claiming a CPU step cost. It was therefore deleted
        // rather than renamed:
        //   * the CPU engine's own step cost is measured against a reference backend in
        //     Clock_RunsAtTheRequestedRateAcrossRealFrames, and in the stage-C record's evolution
        //     axis (and stage D compares that against the worker's step, §4.4);
        //   * the archived stage-1 figures that cite `[perf-microbench]` are marked as historical
        //     in TechnicalAnalysis.md §5.3 and StageArchive.md §3: they are records of one past run,
        //     no longer reproducible by a command.


        [UnityTest]
        public IEnumerator SeedingPanel_UsesThePageWidth_AndShowsItsWholeText()
        {
            // Two review findings, both about the seeding panel rather than its logic:
            // the stacked layout squeezed every control into a ~220-unit column with the
            // rest of the row empty, and three controls cut their own text (the seed
            // number, the backend name, and the mode selector's half-height line).
            //
            // "Fits" is measured here, not squinted at: every value's rendered text is
            // compared with the box that draws it, in both dimensions.
            yield return Settle();

            VisualElement root = GetRoot();
            Press(root.Q<Button>("tool-tab-seeding"));
            yield return null;

            // The PlayMode host is 640x480, which is the compact layout at ~1130 units --
            // wider than the 600x1000 portrait capture (~690). Narrowing the reference
            // resolution narrows the panel without touching the window, so the same checks
            // also run at the width the portrait capture actually has.
            PanelSettings settings = Document().panelSettings;
            Vector2Int original = settings.referenceResolution;
            try
            {
                settings.referenceResolution = new Vector2Int(800, 450);
                yield return null;
                yield return null;

                CheckSeedingPanelGeometry(root, "portrait-width");
            }
            finally
            {
                settings.referenceResolution = original;
            }

            yield return null;
            yield return null;
            CheckSeedingPanelGeometry(root, "host-width");
        }

        /// <summary>
        /// Asserts the seeding page uses the width it has, and that every value it shows
        /// fits the element that draws it. Runs at whatever panel width is current.
        /// </summary>
        private static void CheckSeedingPanelGeometry(VisualElement root, string label)
        {
            ScrollView page = root.Q<ScrollView>("tool-page-seeding");
            Assert.IsNotNull(page, "missing the seeding page");
            Assert.IsNotNull(page.contentContainer, "the seeding page has no content container");

            VisualElement content = page.contentContainer;
            float panelWidth = root.resolvedStyle.width;
            float contentWidth = content.worldBound.width;
            bool compact = root.ClassListContains("compact");

            Debug.Log($"[seeding-layout] {label}: panel={panelWidth:F0} page={page.worldBound.width:F0} " +
                      $"content={contentWidth:F0} compact={compact}");

            Assert.AreEqual(panelWidth < 1280f, compact,
                $"{label}: at a panel width of {panelWidth:F0} the stacked layout should " +
                $"{(panelWidth < 1280f ? string.Empty : "not ")}be active");
            Assert.Greater(contentWidth, 250f,
                $"{label}: the seeding page's content container is only {contentWidth:F0} units wide");

            Slider[] sliders =
            {
                root.Q<Slider>("seed-density"),
                root.Q<Slider>("seed-scale"),
                root.Q<Slider>("seed-warp"),
                root.Q<Slider>("seed-cluster"),
            };

            foreach (Slider slider in sliders)
                Assert.IsNotNull(slider, "a seeding slider is missing");

            if (compact)
            {
                // The page must actually use the width it has: four sliders one per line in a
                // ~220-unit column was the defect. Two sharing a line that reaches across the
                // page is what "uses the width" means.
                int sharedLines = 0;
                for (int i = 0; i < sliders.Length; i++)
                {
                    for (int j = i + 1; j < sliders.Length; j++)
                    {
                        if (Mathf.Abs(sliders[i].worldBound.yMin - sliders[j].worldBound.yMin) < 1f)
                            sharedLines++;
                    }
                }

                Assert.GreaterOrEqual(sharedLines, 2,
                    $"{label}: the seeding sliders are stacked one per line instead of using the page width " +
                    $"(panel {panelWidth:F0}, content {contentWidth:F0})");

                float rightMost = float.MinValue;
                foreach (Slider slider in sliders)
                    rightMost = Mathf.Max(rightMost, slider.worldBound.xMax);

                Assert.Greater(rightMost - content.worldBound.xMin, contentWidth * 0.8f,
                    $"{label}: the slider rows reach only {rightMost - content.worldBound.xMin:F0} of " +
                    $"{contentWidth:F0} available units");
            }
            else
            {
                // The side-by-side layout is a 268-unit column: it is used fully or the
                // controls are wasting it, which is the same defect mirrored.
                foreach (Slider slider in sliders)
                {
                    Assert.GreaterOrEqual(slider.worldBound.width, contentWidth * 0.9f,
                        $"{label}: slider '{slider.name}' is {slider.worldBound.width:F0} units wide in a " +
                        $"{contentWidth:F0}-unit column");
                }
            }

            // 2. Every value has to fit the element that renders it, in both directions.
            //    Measured rather than eyeballed: it was this comparison (10.7 units of box
            //    against 19.3 units of text) that found the cut-off dropdown values.
            DropdownField mode = root.Q<DropdownField>("seed-mode");
            DropdownField backend = root.Q<DropdownField>("backend-field");
            DropdownField boundary = root.Q<DropdownField>("boundary-field");
            IntegerField seed = root.Q<IntegerField>("seed-field");

            var fits = new List<TextFit>
            {
                Measure(mode, mode.value, $"{label}: seeding mode"),
                Measure(boundary, boundary.value, $"{label}: boundary"),
                Measure(seed, "20260915", $"{label}: seed value"),
            };

            foreach (string choice in backend.choices)
                fits.Add(Measure(backend, choice, $"{label}: backend choice"));

            foreach (TextFit fit in fits)
            {
                Assert.GreaterOrEqual(fit.AvailableWidth, fit.TextWidth - 0.5f,
                    $"{fit.What}: '{fit.Text}' needs {fit.TextWidth:F1} units of width but the field " +
                    $"provides {fit.AvailableWidth:F1}");
                Assert.GreaterOrEqual(fit.BoxHeight, fit.TextHeight - 0.5f,
                    $"{fit.What}: '{fit.Text}' needs {fit.TextHeight:F1} units of height but its line " +
                    $"box is {fit.BoxHeight:F1} tall, so the text is cut");
            }
        }

        /// <summary>One control's text measured against the box that would draw it.</summary>
        private sealed class TextFit
        {
            public string What;
            public string Text;
            public float TextWidth;
            public float TextHeight;
            public float BoxWidth;
            public float BoxHeight;
            public float AvailableWidth;
            public float FontSize;
        }

        private static TextFit Measure(VisualElement control, string text, string what)
        {
            // The element that actually draws the value. Not the caption: a field's label
            // is a Label, and Labels are TextElements too, so picking the first TextElement
            // would measure the caption instead of the value.
            TextElement input = null;
            foreach (TextElement candidate in control.Query<TextElement>().ToList())
            {
                if (candidate is Label)
                    continue;

                if (input == null || candidate.worldBound.width > input.worldBound.width)
                    input = candidate;
            }

            Assert.IsNotNull(input, $"{what}: no value text element found inside {control.GetType().Name}");

            Vector2 measured = input.MeasureTextSize(text, 0f, VisualElement.MeasureMode.Undefined,
                0f, VisualElement.MeasureMode.Undefined);
            IResolvedStyle style = input.resolvedStyle;

            var fit = new TextFit
            {
                What = what,
                Text = text,
                TextWidth = measured.x,
                TextHeight = measured.y,
                BoxWidth = input.worldBound.width,
                BoxHeight = input.worldBound.height,
                AvailableWidth = input.worldBound.width - style.paddingLeft - style.paddingRight,
                FontSize = style.fontSize,
            };

            Debug.Log($"[seeding-text] {what}: '{text}' needs {measured.x:F1}x{measured.y:F1}, " +
                      $"font {style.fontSize:F1}; drawn in <{string.Join(".", input.GetClasses())}> " +
                      $"world {input.worldBound.width:F1}x{input.worldBound.height:F1} " +
                      $"resolved {style.width:F1}x{style.height:F1}");
            Debug.Log($"[seeding-text] {what}: ancestors " + AncestorBoxes(input, control));

            Assert.IsFalse(float.IsNaN(fit.AvailableWidth),
                $"{what}: the text element has no resolved width, so nothing can be measured");

            return fit;
        }

        /// <summary>Boxes from the text element up to the control, to locate a squeezed line.</summary>
        private static string AncestorBoxes(VisualElement input, VisualElement control)
        {
            var chain = new List<string>();
            VisualElement current = input;
            while (current != null)
            {
                chain.Add($"{current.GetType().Name}({current.name})=" +
                          $"{current.worldBound.width:F1}x{current.worldBound.height:F1}");

                if (current == control)
                    break;

                current = current.parent;
            }

            return string.Join(" <- ", chain);
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

        /// <summary>
        /// Brings the specimen archive forward. The tool area has two pages now, and a
        /// test that inspects the archive's geometry has to make sure it is the page on
        /// screen: a hidden page lays out to zero height, which reads as a layout bug.
        /// </summary>
        private static void ShowPresetsTab(VisualElement root)
        {
            Button tab = root.Q<Button>("tool-tab-presets");
            Assert.IsNotNull(tab, "missing the specimen tab");
            Press(tab);
        }

        /// <summary>
        /// Pins the evolution backend. Tests that read the population readout need the
        /// CPU backend: stage A publishes no GPU population, so the readout is "—"
        /// there. Relying on whichever backend a previous test happened to leave
        /// selected made those tests pass or fail by execution order.
        /// </summary>
        private static void SelectCpuBackend(VisualElement root)
        {
            DropdownField field = root.Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            if (field.enabledSelf)
                field.value = "CPU（参考实现）";
        }

        /// <summary>Reads the controller's private backend via reflection.</summary>
        private static ILifeBackend ReadBackend(LifeTerminalController controller)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "backend", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeTerminalController.backend field not found");
            return (ILifeBackend)field.GetValue(controller);
        }

        /// <summary>
        /// Presses "▸ 单步" and waits for the generation to land.
        ///
        /// <para>Since stage D the CPU path computes off the frame, so "the button was pressed" and
        /// "the counter moved" are different moments: the press submits a generation and the
        /// controller takes it over when the worker finishes. The GPU path still advances inside the
        /// press, in which case this returns without yielding.</para>
        /// </summary>
        private static IEnumerator StepOnce(VisualElement root, LifeTerminalController controller)
        {
            ILifeBackend backend = ReadBackend(controller);
            int before = backend.Generation;

            Press(FindButton(root, "▸ 单步"));

            for (int frame = 0; frame < 300 && backend.Generation == before; frame++)
                yield return null;

            Assert.AreEqual(before + 1, backend.Generation,
                "单步 did not advance the generation counter");
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
