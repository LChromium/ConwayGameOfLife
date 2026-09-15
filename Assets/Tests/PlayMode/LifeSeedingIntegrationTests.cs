using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage-B integration: the seeding candidate must never disturb the live board,
    /// and no command may run underneath a candidate that is still on screen.
    ///
    /// The emphasis is on CROSS operations -- preview followed by run, step, paint,
    /// specimen selection, clear, reset, randomise or a backend switch -- because that
    /// is where the previous round's state machine was incomplete. Repeating
    /// "preview then apply" would not have caught any of it.
    /// </summary>
    public sealed class LifeSeedingIntegrationTests
    {
        private const float GenerationTimeoutSeconds = 30f;

        // -- plumbing ----------------------------------------------------------

        private static LifeTerminalController Controller()
        {
            LifeTerminalController controller = Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");
            return controller;
        }

        private static VisualElement GetRoot()
        {
            UIDocument document = Controller().GetComponent<UIDocument>();
            Assert.IsNotNull(document, "controller has no UIDocument");
            return document.rootVisualElement;
        }

        private static LifeGridElement Grid(VisualElement root)
        {
            LifeGridElement grid = root.Q(name: null, className: "life-grid") as LifeGridElement;
            Assert.IsNotNull(grid, "missing 'life-grid'");
            return grid;
        }

        private static ILifeBackend ReadBackend(LifeTerminalController controller)
        {
            FieldInfo field = typeof(LifeTerminalController).GetField(
                "backend", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "LifeTerminalController.backend field not found");
            return (ILifeBackend)field.GetValue(controller);
        }

        private static uint[] ReadCells(ILifeBackend backend)
        {
            var cells = new uint[backend.Width * backend.Height];
            Assert.IsTrue(backend.TryReadAllCells(cells), "backend refused a full readback");
            return cells;
        }

        private static void SelectBackend(VisualElement root, bool gpu)
        {
            DropdownField field = root.Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            field.value = gpu ? "GPU（Compute Shader）" : "CPU（参考实现）";
        }

        private static bool GpuAvailable(VisualElement root)
        {
            DropdownField field = root.Q<DropdownField>("backend-field");
            return field != null && field.enabledSelf;
        }

        private static Button FindButton(VisualElement root, string text)
        {
            foreach (Button button in root.Query<Button>().ToList())
            {
                if (button.text == text)
                    return button;
            }

            return null;
        }

        private static void Press(Button button)
        {
            Assert.IsNotNull(button, "cannot press a missing button");
            using NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }

        private static void PressButton(VisualElement root, string text)
        {
            Button button = FindButton(root, text);
            Assert.IsNotNull(button, $"no button labelled '{text}'");
            Press(button);
        }

        private static void PaintCell(LifeGridElement grid, int x, int y)
        {
            MethodInfo seam = typeof(LifeGridElement).GetMethod(
                "PaintForTest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(seam, "LifeGridElement.PaintForTest seam is missing");
            seam.Invoke(grid, new object[] { x, y });
        }

        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

        /// <summary>Waits for the background generation the preview asked for.</summary>
        private static IEnumerator WaitForCandidate(LifeTerminalController controller)
        {
            float deadline = Time.realtimeSinceStartup + GenerationTimeoutSeconds;
            while (controller.Seeding.IsGenerating && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.IsFalse(controller.Seeding.IsGenerating,
                $"candidate generation did not finish within {GenerationTimeoutSeconds}s");
            Assert.IsTrue(controller.Seeding.HasCandidate, "no candidate arrived");
        }

        private static IEnumerator PreviewWith(LifeTerminalController controller, int seed)
        {
            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, seed, 0.32f, 24f, 5f, 0.7f));
            controller.PreviewSeeding();
            yield return WaitForCandidate(controller);
        }

        // -- the candidate must not disturb the board ---------------------------

        [UnityTest]
        public IEnumerator PreviewSeeding_LeavesTheLiveBoardUntouched()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            ILifeBackend backend = ReadBackend(controller);
            uint[] before = ReadCells(backend);
            int generationBefore = backend.Generation;

            yield return PreviewWith(controller, 4242);

            Assert.IsTrue(grid.PreviewActive, "the candidate should be on screen");
            CollectionAssert.AreEqual(before, ReadCells(backend),
                "a preview must not change a single cell of the live board");
            Assert.AreEqual(generationBefore, backend.Generation,
                "a preview must not touch the generation counter");
        }

        // -- cross operations: the clock, stepping and painting are unavailable --

        [UnityTest]
        public IEnumerator WhilePreviewing_TheClockStepAndPaintingAreUnavailable()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            yield return PreviewWith(controller, 900);

            Button play = FindButton(root, "▶ 运行");
            Button step = FindButton(root, "▸ 单步");
            Assert.IsFalse(play.enabledSelf, "the clock must be unavailable while a candidate is on screen");
            Assert.IsFalse(step.enabledSelf, "single-stepping must be unavailable while a candidate is on screen");
            Assert.IsFalse(grid.EditingEnabled, "painting must be unavailable while a candidate is on screen");

            ILifeBackend backend = ReadBackend(controller);
            uint[] before = ReadCells(backend);
            int generationBefore = backend.Generation;

            // The buttons are disabled, so drive the commands directly: the entry
            // points have to refuse on their own, not only via the disabled state.
            Press(play);
            Press(step);
            PaintCell(grid, 0, 0);
            controller.SendMessage("ToggleRunning", SendMessageOptions.DontRequireReceiver);
            controller.SendMessage("StepOnce", SendMessageOptions.DontRequireReceiver);
            yield return null;

            Assert.AreEqual(generationBefore, ReadBackend(controller).Generation,
                "neither the clock nor a step may advance the board while previewing");
            CollectionAssert.AreEqual(before, ReadCells(ReadBackend(controller)),
                "painting must not reach the board while previewing");
            Assert.IsTrue(grid.PreviewActive, "the candidate must still be on screen");
        }

        [UnityTest]
        public IEnumerator WhilePreviewing_PanningAndZoomingStillWork()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            yield return PreviewWith(controller, 901);

            ILifeBackend backend = ReadBackend(controller);
            uint[] before = ReadCells(backend);
            int originBefore = grid.OriginX;
            int zoomBefore = grid.CellPixels;

            grid.PanBy(5, -3);
            grid.ZoomBy(1);
            yield return null;

            Assert.AreNotEqual(originBefore, grid.OriginX, "panning should move the view");
            Assert.AreNotEqual(zoomBefore, grid.CellPixels, "zooming should change the scale");
            CollectionAssert.AreEqual(before, ReadCells(backend),
                "moving the view must not touch the board");
            Assert.IsTrue(grid.PreviewActive, "the candidate must survive a pan");
        }

        // -- cross operations: commands that replace the board end the preview --

        [UnityTest]
        public IEnumerator SelectingASpecimen_EndsThePreview()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            yield return PreviewWith(controller, 902);
            Assert.IsTrue(grid.PreviewActive);

            PressButton(root, "方块\n稳定 / STILL LIFE");
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "choosing a specimen must take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate, "choosing a specimen must drop the candidate");
            Assert.IsTrue(ReadCaption(root).StartsWith("样本 "),
                $"the caption should name the specimen, got '{ReadCaption(root)}'");
        }

        [UnityTest]
        public IEnumerator ClearingTheBoard_EndsThePreview()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            yield return PreviewWith(controller, 903);
            PressButton(root, "清空");
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "clearing must take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate);

            ILifeBackend backend = ReadBackend(controller);
            foreach (uint cell in ReadCells(backend))
                Assert.AreEqual(0u, cell, "clearing must leave an empty board");
        }

        [UnityTest]
        public IEnumerator RandomSeeding_EndsThePreview()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            yield return PreviewWith(controller, 904);
            PressButton(root, "随机播种");
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "random seeding must take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate);
        }

        [UnityTest]
        public IEnumerator Reset_EndsThePreview()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            PressButton(root, "随机播种");
            yield return null;
            ILifeBackend backend = ReadBackend(controller);
            uint[] seeded = ReadCells(backend);

            yield return PreviewWith(controller, 905);
            PressButton(root, "↺ 重置");
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "reset must take the candidate off screen");
            Assert.AreEqual(0, ReadBackend(controller).Generation);
            CollectionAssert.AreEqual(seeded, ReadCells(ReadBackend(controller)),
                "reset must restore the confirmed board, not the candidate");
        }

        [UnityTest]
        public IEnumerator SwitchingBackend_EndsThePreview()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            if (!GpuAvailable(root))
                Assert.Ignore("compute shaders unavailable on this machine; the switch was not exercised");

            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            SelectBackend(root, gpu: true);
            yield return null;

            yield return PreviewWith(controller, 906);
            Assert.IsTrue(grid.PreviewActive);

            SelectBackend(root, gpu: false);
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "switching backends must take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate);

            SelectBackend(root, gpu: true);
            yield return null;
        }

        // -- cancel restores what was on screen, not the experiment label --------

        [UnityTest]
        public IEnumerator CancelSeeding_RestoresTheCaptionThatWasShowing()
        {
            // The board before the preview was hand-edited, so its caption is the
            // custom-edit label. Restoring the experiment's initial label here would
            // wrongly rename it back to the specimen.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            SelectBackend(root, gpu: false);
            yield return null;

            PressButton(root, "清空");
            yield return null;
            PaintCell(grid, 4, 4);
            PaintCell(grid, 5, 4);
            yield return null;

            string before = ReadCaption(root);
            Assert.AreEqual("自由样本 / 手动编辑", before);

            ILifeBackend backend = ReadBackend(controller);
            uint[] boardBefore = ReadCells(backend);

            yield return PreviewWith(controller, 907);
            Assert.AreNotEqual(before, ReadCaption(root), "the caption should say a preview is showing");

            PressButton(root, "取消");
            yield return null;

            Assert.AreEqual(before, ReadCaption(root),
                "cancelling must restore the caption that was showing before the preview");
            CollectionAssert.AreEqual(boardBefore, ReadCells(backend),
                "cancelling must leave the hand-edited board untouched");
            Assert.IsFalse(grid.PreviewActive);
        }

        // -- apply ---------------------------------------------------------------

        [UnityTest]
        public IEnumerator ApplySeeding_ReplacesTheBoard_ResetsGeneration_AndStaysPaused()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            ILifeBackend backend = ReadBackend(controller);
            backend.Step();
            backend.Step();
            yield return null;
            Assert.AreEqual(2, backend.Generation);

            yield return PreviewWith(controller, 31337);

            byte[] candidate = controller.Seeding.Candidate.ToArray();
            Button apply = FindButton(root, "应用");
            Assert.IsTrue(apply.enabledSelf, "应用 should be available once a candidate has landed");
            Press(apply);
            yield return null;

            ILifeBackend after = ReadBackend(controller);
            Assert.AreEqual(0, after.Generation, "applying a candidate must reset the generation");

            uint[] applied = ReadCells(after);
            Assert.AreEqual(candidate.Length, applied.Length);
            for (int i = 0; i < candidate.Length; i++)
            {
                if (applied[i] != (candidate[i] != 0 ? 1u : 0u))
                    Assert.Fail($"applied board differs from the candidate at index {i} " +
                                $"(x={i % after.Width}, y={i / after.Width})");
            }

            Assert.IsFalse(grid.PreviewActive, "the preview colour must give way once applied");
            Assert.IsTrue(controller.Seeding.HasAppliedParameters);
            Assert.AreEqual(31337, controller.Seeding.AppliedParameters.Seed);
        }

        // -- parameter and control agreement ------------------------------------

        [UnityTest]
        public IEnumerator EditingOneControl_DoesNotOverwriteTheOthers()
        {
            // The command line used to update the session without touching the widgets,
            // so the first slider drag read the stale widget values back into the
            // session and silently reverted everything else.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            DropdownField mode = root.Q<DropdownField>("seed-mode");
            IntegerField seed = root.Q<IntegerField>("seed-field");
            Slider density = root.Q<Slider>("seed-density");
            Slider scale = root.Q<Slider>("seed-scale");
            Slider warp = root.Q<Slider>("seed-warp");
            Slider cluster = root.Q<Slider>("seed-cluster");

            mode.value = "fBm 团簇";
            seed.value = 777;
            density.value = 0.28f;
            scale.value = 56f;
            warp.value = 9f;
            cluster.value = 0.66f;
            yield return null;

            // Now move only one control.
            density.value = 0.41f;
            yield return null;

            LifeNoiseParameters parameters = controller.Seeding.Parameters;
            Assert.AreEqual(777, parameters.Seed, "the seed was overwritten by an unrelated edit");
            Assert.AreEqual(56f, parameters.Scale, 1e-3f, "the scale was overwritten by an unrelated edit");
            Assert.AreEqual(9f, parameters.WarpStrength, 1e-3f, "warp was overwritten by an unrelated edit");
            Assert.AreEqual(0.66f, parameters.ClusterStrength, 1e-3f, "cluster was overwritten by an unrelated edit");
            Assert.AreEqual(0.41f, parameters.Density, 1e-4f, "the edited control must be the one that changed");
        }

        [UnityTest]
        public IEnumerator ControlsMatchTheSessionAfterACommandLineStyleChange()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            // Same path the command line uses.
            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Uniform, 20260915, 0.44f, 12f, 3f, 0.25f));
            controller.SendMessage("SyncSeedingControlsFromSession", SendMessageOptions.DontRequireReceiver);
            yield return null;

            Assert.AreEqual("均匀随机", root.Q<DropdownField>("seed-mode").value);
            Assert.AreEqual(20260915, root.Q<IntegerField>("seed-field").value);
            Assert.AreEqual(0.44f, root.Q<Slider>("seed-density").value, 1e-4f);
            Assert.AreEqual(12f, root.Q<Slider>("seed-scale").value, 1e-3f);
            Assert.AreEqual(3f, root.Q<Slider>("seed-warp").value, 1e-3f);
            Assert.AreEqual(0.25f, root.Q<Slider>("seed-cluster").value, 1e-3f);
        }

        [UnityTest]
        public IEnumerator ApplyingSeeding_ClearsTheSpecimenHighlight()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            PressButton(root, "方块\n稳定 / STILL LIFE");
            yield return null;

            bool anySelected = false;
            foreach (Button button in root.Query<Button>(className: "preset").ToList())
                anySelected |= button.ClassListContains("selected");
            Assert.IsTrue(anySelected, "choosing a specimen should highlight it");

            yield return PreviewWith(controller, 908);
            PressButton(root, "应用");
            yield return null;

            foreach (Button button in root.Query<Button>(className: "preset").ToList())
                Assert.IsFalse(button.ClassListContains("selected"),
                    "the board is no longer that specimen, so nothing should stay highlighted");
        }

        // -- backend agreement ---------------------------------------------------

        [UnityTest]
        public IEnumerator AppliedSeeding_IsCellIdenticalOnBothBackends()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            if (!GpuAvailable(root))
                Assert.Ignore("compute shaders unavailable on this machine; the GPU half was not exercised");

            LifeTerminalController controller = Controller();

            SelectBackend(root, gpu: true);
            yield return null;

            yield return PreviewWith(controller, 20260915);
            PressButton(root, "应用");
            yield return null;

            ILifeBackend gpu = ReadBackend(controller);
            Assert.AreEqual("GPU", gpu.Name, "expected the GPU backend to be live");
            uint[] onGpu = ReadCells(gpu);

            SelectBackend(root, gpu: false);
            yield return null;

            ILifeBackend cpu = ReadBackend(controller);
            Assert.AreEqual("CPU", cpu.Name, "expected the CPU backend to be live");
            CollectionAssert.AreEqual(ReadCells(cpu), onGpu,
                "the seeded initial state must be cell-identical on both backends");
        }

        [UnityTest]
        public IEnumerator Reset_AfterApplyingSeeding_RestoresTheSeededBoard_NotANewOne()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            yield return PreviewWith(controller, 8080);
            PressButton(root, "应用");
            yield return null;

            ILifeBackend backend = ReadBackend(controller);
            uint[] seeded = ReadCells(backend);

            for (int i = 0; i < 3; i++)
                backend.Step();
            yield return null;

            PressButton(root, "↺ 重置");
            yield return null;

            Assert.AreEqual(0, backend.Generation, "reset must return to generation 0");
            CollectionAssert.AreEqual(seeded, ReadCells(backend),
                "reset must restore the seeded board, not generate a fresh one");
        }

        [UnityTest]
        public IEnumerator EvolutionAfterSeeding_DoesNotConsultTheNoise()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            SelectBackend(root, gpu: false);
            yield return null;

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Uniform, 5, 0f, 32f, 0f, 0f));
            controller.PreviewSeeding();
            yield return WaitForCandidate(controller);
            PressButton(root, "应用");
            yield return null;

            ILifeBackend backend = ReadBackend(controller);
            uint[] empty = ReadCells(backend);
            foreach (uint cell in empty)
                Assert.AreEqual(0u, cell, "density 0 must seed an empty board");

            for (int i = 0; i < 5; i++)
                backend.Step();
            yield return null;

            CollectionAssert.AreEqual(empty, ReadCells(backend),
                "an empty board must stay empty: evolution cannot be reading the noise field");
        }

        // -- helpers -------------------------------------------------------------

        [UnityTest]
        public IEnumerator ToolTabs_SwitchBetweenTheArchiveAndTheSeedingPanel()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Button presetsTab = root.Q<Button>("tool-tab-presets");
            Button seedingTab = root.Q<Button>("tool-tab-seeding");
            VisualElement presetsPage = root.Q<VisualElement>("tool-page-presets");
            VisualElement seedingPage = root.Q<VisualElement>("tool-page-seeding");

            Assert.IsNotNull(presetsTab, "missing the specimen tab");
            Assert.IsNotNull(seedingTab, "missing the seeding tab");
            Assert.IsNotNull(presetsPage, "missing the specimen page");
            Assert.IsNotNull(seedingPage, "missing the seeding page");

            // Start from a known page: an earlier test may have brought the seeding
            // panel forward, and this test is about the switch, not about the default.
            Press(presetsTab);
            yield return null;

            Assert.AreEqual(DisplayStyle.Flex, presetsPage.resolvedStyle.display,
                "pressing the specimen tab should show the archive");
            Assert.AreEqual(DisplayStyle.None, seedingPage.resolvedStyle.display);

            Press(seedingTab);
            yield return null;

            Assert.AreEqual(DisplayStyle.None, presetsPage.resolvedStyle.display,
                "only one tool page may be visible at a time");
            Assert.AreEqual(DisplayStyle.Flex, seedingPage.resolvedStyle.display);

            Press(presetsTab);
            yield return null;

            Assert.AreEqual(DisplayStyle.Flex, presetsPage.resolvedStyle.display);
            Assert.AreEqual(DisplayStyle.None, seedingPage.resolvedStyle.display);
        }

        [UnityTest]
        public IEnumerator PreviewingABringsTheSeedingPanelForward()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            // Start from the archive page rather than assuming it: an earlier test may
            // have left the seeding panel showing.
            Press(root.Q<Button>("tool-tab-presets"));
            yield return null;

            Assert.AreEqual(DisplayStyle.None, root.Q<VisualElement>("tool-page-seeding").resolvedStyle.display);

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 4321, 0.32f, 24f, 5f, 0.7f));
            controller.PreviewSeeding();
            yield return null;

            Assert.AreEqual(DisplayStyle.Flex,
                root.Q<VisualElement>("tool-page-seeding").resolvedStyle.display,
                "asking for a candidate should bring its panel forward");
        }

        private static string ReadCaption(VisualElement root)
        {
            foreach (VisualElement element in root.Query<VisualElement>(className: "micro").ToList())
            {
                if (element is Label label &&
                    (label.text.StartsWith("样本 ") || label.text.StartsWith("自由样本") ||
                     label.text.StartsWith("预览") || label.text.StartsWith("播种")))
                {
                    return label.text;
                }
            }

            return null;
        }
    }
}
