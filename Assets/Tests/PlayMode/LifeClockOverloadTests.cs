using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Clock overload protection.
    ///
    /// The failure this exists for is a cross-frame amplification, not a runaway inside one
    /// frame: the accumulator grows by one frame delta per frame, so a frame that took 650 ms
    /// (one CPU generation at 4096x4096) is followed by a frame that tries to retire thirteen
    /// generations at 650 ms each, which makes the next delta larger still. Round 2 hit it on
    /// a real board and the player stopped responding.
    ///
    /// These tests drive a controllable slow backend instead of a large board, because the
    /// property under test is "how much does one frame allow itself to do", and a fake backend
    /// makes that exact rather than a race against the machine.
    /// </summary>
    public sealed class LifeClockOverloadTests
    {
        /// <summary>
        /// A real CPU backend with an artificial per-step cost. Everything else is delegated,
        /// so the terminal drives it exactly as it drives a real one.
        /// </summary>
        private sealed class SlowBackend : ILifeBackend
        {
            private readonly CpuLifeBackend inner;
            private readonly double stepCostMilliseconds;

            public SlowBackend(int width, int height, double stepCostMilliseconds)
            {
                inner = new CpuLifeBackend(width, height);
                this.stepCostMilliseconds = stepCostMilliseconds;
            }

            public int StepCalls { get; private set; }

            public string Name => "SLOW";

            public int Width => inner.Width;
            public int Height => inner.Height;
            public int Generation => inner.Generation;

            public bool WrapEdges
            {
                get => inner.WrapEdges;
                set => inner.WrapEdges = value;
            }

            public void LoadBoard(ReadOnlySpan<byte> cells)
            {
                StepCalls = 0;
                inner.LoadBoard(cells);
            }

            public void Clear() => inner.Clear();
            public void SetCell(int x, int y, bool alive) => inner.SetCell(x, y, alive);
            public bool TryGetPopulation(out int population) => inner.TryGetPopulation(out population);
            public bool TryReadAllCells(Span<uint> destination) => inner.TryReadAllCells(destination);
            public void Dispose() => inner.Dispose();

            public void Step()
            {
                StepCalls++;

                if (stepCostMilliseconds >= 1.0)
                    Thread.Sleep((int)stepCostMilliseconds);

                inner.Step();
            }
        }

        private const int BoardWidth = 64;
        private const int BoardHeight = 64;

        // -- plumbing ----------------------------------------------------------

        private static LifeTerminalController Controller()
        {
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");
            return controller;
        }

        private static LifeGridElement Grid()
        {
            UIDocument document = Controller().GetComponent<UIDocument>();
            Assert.IsNotNull(document, "controller has no UIDocument");
            return document.rootVisualElement.Q(name: null, className: "life-grid") as LifeGridElement;
        }

        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} field not found");
            field.SetValue(target, value);
        }

        private static T ReadField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} field not found");
            return (T)field.GetValue(target);
        }

        // The injected backend replaces the live one on BOTH the controller and the grid, so
        // it has to be put back or every later test inherits a board of the wrong size. The
        // first version of this fixture did not restore it, and a seeding test two fixtures
        // later failed with "Board must be 4096 cells, got 65536" -- which is exactly the kind
        // of cross-test damage the restore exists to prevent.
        private ILifeBackend originalBackend;
        private SlowBackend injected;

        [UnitySetUp]
        public IEnumerator CaptureLiveBackend()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            originalBackend = ReadBackend(controller);
            Assert.IsNotNull(originalBackend, "the terminal has no live backend to restore later");
        }

        [UnityTearDown]
        public IEnumerator RestoreLiveBackend()
        {
            LifeTerminalController controller = Controller();
            SetRunning(controller, false);

            if (injected != null)
            {
                SetField(controller, "backend", originalBackend);
                Grid().Bind(originalBackend);
                injected.Dispose();
                injected = null;
            }

            yield return null;
            yield return null;
        }

        /// <summary>
        /// Installs a slow backend in place of whichever one is live, on the controller and on
        /// the grid, sized like the live board so the controller's own commands (reset, load a
        /// pattern) keep working. Leaves the clock running at the slider's maximum.
        /// </summary>
        private IEnumerator InstallSlowBackend(LifeTerminalController controller, double stepCostMs)
        {
            // Same dimensions as the board the terminal is already driving: a different size
            // makes the controller's own LoadBoard calls fail, which would be a test artefact
            // rather than the thing under test.
            ILifeBackend live = ReadBackend(controller);
            var slow = new SlowBackend(live.Width, live.Height, stepCostMs);
            slow.WrapEdges = true;
            slow.LoadBoard(new byte[live.Width * live.Height]);

            SetField(controller, "backend", slow);
            Grid().Bind(slow);
            SetSpeed(controller, 20);
            SetRunning(controller, true);
            injected = slow;

            yield return null;
        }

        private static void SetRunning(LifeTerminalController controller, bool value) =>
            SetField(controller, "running", value);

        private static void SetSpeed(LifeTerminalController controller, int generationsPerSecond)
        {
            SliderInt slider = ReadField<SliderInt>(controller, "speedSlider");
            slider.value = generationsPerSecond;
        }

        private static ILifeBackend ReadBackend(LifeTerminalController controller) =>
            ReadField<ILifeBackend>(controller, "backend");

        // -- the tests ---------------------------------------------------------

        [UnityTest]
        public IEnumerator SlowBackend_AdvancesAtMostThePerFrameCap()
        {
            // One step costs 60 ms against a 50 ms clock interval, so every step exceeds its
            // own interval and the debt would compound without a cap.
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            var slow = (SlowBackend)ReadBackend(controller);
            int frames = 0;
            int stepsAtStart = slow.StepCalls;

            float deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                frames++;
            }

            int steps = slow.StepCalls - stepsAtStart;

            Assert.Greater(frames, 5, "not enough frames elapsed to judge the cap");
            Assert.LessOrEqual(steps, frames * 4,
                $"the clock advanced {steps} generations in {frames} frames, above the per-frame cap");

            // With a 60 ms step the time budget should stop the loop after the first step, so
            // the tighter claim is one generation per frame.
            Assert.LessOrEqual(steps, frames + 1,
                $"a 60 ms step must not be chained: {steps} generations in {frames} frames");

            SetRunning(controller, false);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LongFrame_DoesNotTriggerCatchUpAmplification()
        {
            // A free backend plus one deliberately long frame: the next frame owes ~10
            // generations. Without the cap it retires all of them (and the frame after that
            // owes more, which is the amplification).
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 0);

            var slow = (SlowBackend)ReadBackend(controller);
            SetRunning(controller, false);
            yield return null;

            // Stop the clock so the debt is cleared, then create the debt the honest way: one
            // frame that takes far longer than the clock interval.
            SetRunning(controller, true);
            int before = slow.Generation;

            Thread.Sleep(600);
            yield return null;

            int advanced = slow.Generation - before;

            Assert.Greater(advanced, 0, "a 600 ms frame should still advance the board");
            Assert.LessOrEqual(advanced, 4,
                $"a single frame advanced {advanced} generations after a 600 ms stall; " +
                "the per-frame cap is 4");
            Assert.IsTrue(controller.ClockOverloaded,
                "the frame that owed more generations than the cap allows must report that it " +
                "dropped catch-up debt");

            SetRunning(controller, false);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SlowBackend_ReportsAchievedBelowRequested()
        {
            // One 60 ms step against a 50 ms clock interval: the clock is not dropping debt here
            // (no frame owes a whole extra generation), it simply cannot reach the slider's
            // rate. The interface must say so rather than showing the requested number.
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            float deadline = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.Greater(controller.AchievedGenerationsPerSecond, 0f,
                "the clock is still advancing, just slower than asked");
            Assert.Less(controller.AchievedGenerationsPerSecond, 20f,
                "the achieved rate must be reported below the requested rate, not as equal to it");

            Label state = ReadField<Label>(controller, "stateLabel");
            Assert.IsTrue(state.text.Contains("实际"),
                $"the state readout should distinguish target from achieved, got '{state.text}'");

            SetRunning(controller, false);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausingClearsTheDebt_SoResumingDoesNotBurst()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            // Let debt build up.
            float deadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < deadline)
                yield return null;

            var slow = (SlowBackend)ReadBackend(controller);
            SetRunning(controller, false);
            yield return null;
            yield return null;

            int paused = slow.Generation;
            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.AreEqual(paused, slow.Generation, "a paused clock must not keep advancing");

            // Resuming must start from now, not replay what was owed before the pause.
            SetRunning(controller, true);
            int beforeResume = slow.Generation;
            yield return null;
            yield return null;

            Assert.LessOrEqual(slow.Generation - beforeResume, 4,
                "resuming replayed clock debt that belonged to the time before the pause");

            SetRunning(controller, false);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ResetClearsTheDebt_SoTheFreshBoardIsNotFastForwarded()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            float deadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < deadline)
                yield return null;

            // Reset goes through the controller's own command, which restores the initial
            // state and stops the clock.
            MethodInfo reset = typeof(LifeTerminalController).GetMethod(
                "ResetToInitialState", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "ResetToInitialState not found");
            reset.Invoke(controller, null);
            yield return null;

            var slow = (SlowBackend)ReadBackend(controller);
            int afterReset = slow.Generation;
            Assert.AreEqual(0, afterReset, "reset returns the board to generation 0");

            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.AreEqual(0, slow.Generation,
                "the clock must not fast-forward the restored board through old debt");
        }
    }
}
