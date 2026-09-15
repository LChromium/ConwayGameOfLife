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
    /// and an applied candidate must land identically on both backends.
    ///
    /// The board is read back through the backend, not through the display, so these
    /// tests say something about the real state rather than about what is on screen.
    /// </summary>
    public sealed class LifeSeedingIntegrationTests
    {
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

        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

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

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 4242, 0.35f, 24f, 5f, 0.7f));
            controller.PreviewSeeding();
            yield return null;

            Assert.IsTrue(grid.PreviewActive, "the candidate should be on screen");
            CollectionAssert.AreEqual(before, ReadCells(backend),
                "a preview must not change a single cell of the live board");
            Assert.AreEqual(generationBefore, backend.Generation,
                "a preview must not touch the generation counter");
            Assert.IsTrue(controller.Seeding.HasCandidate, "the candidate should still be pending");
        }

        [UnityTest]
        public IEnumerator CancelSeeding_LeavesTheBoardExactlyAsItWas()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            ILifeBackend backend = ReadBackend(controller);
            uint[] before = ReadCells(backend);

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 77, 0.4f, 20f, 6f, 0.8f));
            controller.PreviewSeeding();
            yield return null;

            controller.CancelSeeding();
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "cancelling must take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate, "cancelling must drop the candidate");
            CollectionAssert.AreEqual(before, ReadCells(backend),
                "cancelling must leave the board exactly where it was");
        }

        [UnityTest]
        public IEnumerator ApplySeeding_ReplacesTheBoard_ResetsGeneration_AndStaysPaused()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            // Advance first, so "reset to zero" is a real change.
            ILifeBackend backend = ReadBackend(controller);
            backend.Step();
            backend.Step();
            yield return null;
            Assert.AreEqual(2, backend.Generation);

            var parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 31337, 0.3f, 32f, 7f, 0.75f);
            controller.Seeding.SetParameters(parameters);
            controller.PreviewSeeding();
            yield return null;

            byte[] candidate = controller.Seeding.Candidate.ToArray();
            controller.ApplySeeding();
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
            Assert.AreEqual(parameters.Seed, controller.Seeding.AppliedParameters.Seed);
        }

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

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 20260915, 0.32f, 40f, 6f, 0.65f));
            controller.PreviewSeeding();
            yield return null;
            controller.ApplySeeding();
            yield return null;

            ILifeBackend gpu = ReadBackend(controller);
            Assert.AreEqual("GPU", gpu.Name, "expected the GPU backend to be live");
            uint[] onGpu = ReadCells(gpu);

            // Switch backends: the controller rebuilds from the same saved initial
            // state, so whatever comes out on CPU is the same definite board.
            SelectBackend(root, gpu: false);
            yield return null;

            ILifeBackend cpu = ReadBackend(controller);
            Assert.AreEqual("CPU", cpu.Name, "expected the CPU backend to be live");
            uint[] onCpu = ReadCells(cpu);

            CollectionAssert.AreEqual(onCpu, onGpu,
                "the seeded initial state must be cell-identical on both backends");
        }

        [UnityTest]
        public IEnumerator Reset_AfterApplyingSeeding_RestoresTheSeededBoard_NotANewOne()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 8080, 0.3f, 28f, 5f, 0.7f));
            controller.PreviewSeeding();
            yield return null;
            controller.ApplySeeding();
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
            // The seeding produces a definite array and then gets out of the way. If
            // the generator were still being consulted during evolution, a still life
            // would not stay still and an oscillator would not return to its start.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();

            SelectBackend(root, gpu: false);
            yield return null;

            // Uniform seeding at the extremes gives an empty board and a full board,
            // neither of which may move at all under B3/S23.
            controller.Seeding.SetParameters(
                new LifeNoiseParameters(LifeSeedingMode.Uniform, 5, 0f, 32f, 0f, 0f));
            controller.PreviewSeeding();
            yield return null;
            controller.ApplySeeding();
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

        [UnityTest]
        public IEnumerator SeedingControls_DrivePreviewApplyAndCancel()
        {
            // Drives the actual panel controls. The other tests call the controller's
            // commands directly, which would still pass if a button were wired to
            // nothing at all.
            yield return Settle();

            VisualElement root = GetRoot();
            LifeTerminalController controller = Controller();
            LifeGridElement grid = Grid(root);

            DropdownField mode = root.Q<DropdownField>("seed-mode");
            IntegerField seed = root.Q<IntegerField>("seed-field");
            Slider density = root.Q<Slider>("seed-density");
            Slider scale = root.Q<Slider>("seed-scale");
            Slider warp = root.Q<Slider>("seed-warp");
            Slider cluster = root.Q<Slider>("seed-cluster");
            Label readout = root.Q<Label>("seed-density-readout");
            Button preview = FindButton(root, "预览");
            Button apply = FindButton(root, "应用");
            Button cancel = FindButton(root, "取消");

            Assert.IsNotNull(mode, "missing the seeding mode control");
            Assert.IsNotNull(seed, "missing the seed control");
            Assert.IsNotNull(density, "missing the density control");
            Assert.IsNotNull(scale, "missing the scale control");
            Assert.IsNotNull(warp, "missing the warp control");
            Assert.IsNotNull(cluster, "missing the cluster control");
            Assert.IsNotNull(readout, "missing the realised-density readout");
            Assert.IsNotNull(preview, "missing the preview button");
            Assert.IsNotNull(apply, "missing the apply button");
            Assert.IsNotNull(cancel, "missing the cancel button");

            Assert.IsFalse(apply.enabledSelf, "应用 must be unavailable before a candidate exists");
            Assert.IsFalse(cancel.enabledSelf, "取消 must be unavailable before a candidate exists");

            // Drive the controls the way a user would.
            mode.value = "fBm 团簇";
            seed.value = 123456;
            density.value = 0.30f;
            scale.value = 32f;
            warp.value = 6f;
            cluster.value = 0.60f;
            yield return null;

            Assert.AreEqual(123456, controller.Seeding.Parameters.Seed, "the seed control did not reach the session");
            Assert.AreEqual(0.30f, controller.Seeding.Parameters.Density, 1e-4f, "density control not wired");
            Assert.AreEqual(32f, controller.Seeding.Parameters.Scale, 1e-3f, "scale control not wired");
            Assert.AreEqual(6f, controller.Seeding.Parameters.WarpStrength, 1e-3f, "warp control not wired");
            Assert.AreEqual(0.60f, controller.Seeding.Parameters.ClusterStrength, 1e-3f, "cluster control not wired");

            ILifeBackend backend = ReadBackend(controller);
            uint[] before = ReadCells(backend);

            Press(preview);
            yield return null;

            Assert.IsTrue(grid.PreviewActive, "预览 should put the candidate on screen");
            CollectionAssert.AreEqual(before, ReadCells(backend), "预览 must not touch the live board");
            Assert.IsTrue(apply.enabledSelf, "应用 should become available once a candidate exists");
            Assert.IsTrue(cancel.enabledSelf, "取消 should become available once a candidate exists");
            // Constraint form rather than StringAssert: the classic helper takes
            // (expected, actual), which is the opposite of what it reads like.
            Assert.That(readout.text, Does.StartWith("实际 0."),
                $"the readout should report the realised density, got '{readout.text}'");

            // Editing a control while a candidate is showing regenerates the candidate
            // and still must not disturb the board.
            byte[] firstCandidate = controller.Seeding.Candidate.ToArray();
            density.value = 0.45f;
            yield return null;

            CollectionAssert.AreNotEqual(firstCandidate, controller.Seeding.Candidate.ToArray(),
                "editing a parameter should refresh the candidate");
            CollectionAssert.AreEqual(before, ReadCells(backend),
                "editing a parameter must still not touch the live board");

            Press(cancel);
            yield return null;

            Assert.IsFalse(grid.PreviewActive, "取消 should take the candidate off screen");
            Assert.IsFalse(controller.Seeding.HasCandidate, "取消 should drop the candidate");
            CollectionAssert.AreEqual(before, ReadCells(backend), "取消 must leave the board exactly as it was");
            Assert.IsFalse(apply.enabledSelf, "应用 should go back to unavailable");

            Press(preview);
            yield return null;
            Press(apply);
            yield return null;

            Assert.AreEqual(0, ReadBackend(controller).Generation, "应用 must reset the generation counter");
            Assert.IsFalse(grid.PreviewActive, "应用 must take the preview off screen");
            Assert.IsTrue(controller.Seeding.HasAppliedParameters);
            Assert.AreEqual(123456, controller.Seeding.AppliedParameters.Seed);
            Assert.IsFalse(apply.enabledSelf, "there is no candidate left to apply");
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
    }
}
