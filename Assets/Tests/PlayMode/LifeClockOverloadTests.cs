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
    /// Clock overload protection, and the clock state a run leaves behind when it ends.
    ///
    /// The failure the cap exists for is a cross-frame amplification, not a runaway inside one
    /// frame: the accumulator grows by one frame delta per frame, so a frame that took 650 ms
    /// (one CPU generation at 4096x4096) is followed by a frame that tries to retire thirteen
    /// generations at 650 ms each, which makes the next delta larger still. Round 2 hit it on
    /// a real board and the player stopped responding.
    ///
    /// These tests drive a controllable slow backend instead of a large board, because the
    /// property under test is "how much does one frame allow itself to do", and a fake backend
    /// makes that exact rather than a race against the machine.
    ///
    /// <para><b>The clock is driven through the interface, not around it.</b> Pause and resume
    /// both go through the play button's callback, which is where the run's clock state is
    /// ended. An earlier version of this fixture wrote the private <c>running</c> flag directly,
    /// which skipped that call -- so the test named "pausing clears the debt" could not fail for
    /// the reason it claimed, and the assertion it used (at most four generations in two frames
    /// after a resume) was already guaranteed by the per-frame cap.</para>
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

        // The play button's two labels, as the panel writes them.
        private const string RunButtonText = "▶ 运行";
        private const string PauseButtonText = "Ⅱ 暂停";

        // -- plumbing ----------------------------------------------------------

        private static LifeTerminalController Controller()
        {
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");
            return controller;
        }

        private static VisualElement Root()
        {
            UIDocument document = Controller().GetComponent<UIDocument>();
            Assert.IsNotNull(document, "controller has no UIDocument");
            return document.rootVisualElement;
        }

        private static LifeGridElement Grid()
        {
            return Root().Q(name: null, className: "life-grid") as LifeGridElement;
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

        /// <summary>
        /// Presses a button the way the runtime does. Clickable.click() is internal to
        /// UnityEngine.UIElements, so the public path is to dispatch the submit event that a
        /// Button listens for.
        /// </summary>
        private static void Press(Button button)
        {
            Assert.IsNotNull(button, "cannot press a missing button");
            using NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }

        /// <summary>
        /// Presses the play control by the label it currently carries. Finding it by text is
        /// deliberate: it proves the panel is offering the action the test is about to take
        /// (运行 while paused, 暂停 while running) instead of the fixture assuming it.
        /// </summary>
        private static void PressPlay(string expectedText)
        {
            Button play = null;
            foreach (Button button in Root().Query<Button>().ToList())
            {
                if (button.text == expectedText)
                {
                    play = button;
                    break;
                }
            }

            Assert.IsNotNull(play,
                $"no button reads '{expectedText}', so the clock is not in the state this test assumes");
            Press(play);
        }

        /// <summary>
        /// The controller's own stop command -- the same method "↺ 重置", the boundary selector
        /// and "▸ 单步" call. Used as fixture setup and teardown, and as the second real way to
        /// pause the clock, so no test ever writes the private running flag.
        /// </summary>
        private static void StopClock(LifeTerminalController controller)
        {
            MethodInfo stop = typeof(LifeTerminalController).GetMethod(
                "Stop", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(stop, "LifeTerminalController.Stop not found");
            stop.Invoke(controller, null);
        }

        private static bool IsRunning(LifeTerminalController controller) =>
            ReadField<bool>(controller, "running");

        private static float PendingSeconds(LifeTerminalController controller) =>
            ReadField<float>(controller, "accumulator");

        private static double WindowSeconds(LifeTerminalController controller) =>
            ReadField<double>(controller, "rateWindowSeconds");

        private static int WindowGenerations(LifeTerminalController controller) =>
            ReadField<int>(controller, "rateWindowGenerations");

        private static string StateText(LifeTerminalController controller) =>
            ReadField<Label>(controller, "stateLabel").text;

        // The injected backend replaces the live one on BOTH the controller and the grid, so
        // it has to be put back or every later test inherits a board of the wrong size. The
        // first version of this fixture did not restore it, and a seeding test two fixtures
        // later failed with "Board must be 4096 cells, got 65536" -- which is exactly the kind
        // of cross-test damage the restore exists to prevent.
        private ILifeBackend originalBackend;
        private SlowBackend injected;

        /// <summary>
        /// The clock is part of the fixture, not of the test. A test that inherited a running
        /// clock, a rate window part way through, or the previous test's published rate would be
        /// measuring the previous test -- which is how a passing fixture hides a stale-state bug.
        /// </summary>
        [UnitySetUp]
        public IEnumerator CaptureLiveBackendAndResetTheClock()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            originalBackend = ReadBackend(controller);
            Assert.IsNotNull(originalBackend, "the terminal has no live backend to restore later");

            StopClock(controller);

            Assert.IsFalse(IsRunning(controller), "the fixture could not stop the clock before the test");
            Assert.AreEqual(0f, PendingSeconds(controller), 1e-6f,
                "the clock entered the test with pending time from an earlier test");
            Assert.IsFalse(controller.AchievedRateMeasured,
                "the clock entered the test publishing a rate measured by an earlier test");

            yield return null;
        }

        [UnityTearDown]
        public IEnumerator RestoreLiveBackend()
        {
            LifeTerminalController controller = Controller();
            StopClock(controller);

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
        /// pattern) keep working. The clock is left paused: each test starts it through the play
        /// button, which is the path that clears the run's clock state.
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
            injected = slow;

            yield return null;
        }

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

            SetSpeed(controller, 20);
            PressPlay(RunButtonText);
            Assert.IsTrue(IsRunning(controller), "the play button did not start the clock");

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

            StopClock(controller);
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
            SetSpeed(controller, 20);

            // Start and stop once so the debt is created by the long frame below and not by
            // whatever the fixture left in the accumulator.
            PressPlay(RunButtonText);
            yield return null;
            StopClock(controller);
            yield return null;

            PressPlay(RunButtonText);
            var slow = (SlowBackend)ReadBackend(controller);
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

            StopClock(controller);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SlowBackend_ReportsAchievedBelowRequested_AndAgreesWithTheWallClock()
        {
            // One 60 ms step against a 50 ms clock interval: the clock is not dropping whole
            // generations of debt here, it simply cannot reach the slider's rate. The interface
            // must say so rather than showing the requested number -- and the number it shows is
            // checked against the board's own generation counter over the same wall clock, since
            // a window estimate on its own is not evidence of anything.
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);
            SetSpeed(controller, 20);
            PressPlay(RunButtonText);

            var slow = (SlowBackend)ReadBackend(controller);

            // Same two instants for both figures: the generation counter and the clock are read
            // together at the start and again at the end.
            float startedAt = Time.realtimeSinceStartup;
            int startedGeneration = slow.Generation;

            float deadline = startedAt + 2.5f;
            while (Time.realtimeSinceStartup < deadline)
                yield return null;

            double elapsed = Time.realtimeSinceStartup - startedAt;
            int advanced = slow.Generation - startedGeneration;
            double wallClockRate = advanced / elapsed;

            Assert.Greater(advanced, 10,
                $"only {advanced} generations in {elapsed:0.00} s; too few to compare rates");
            Assert.Less(wallClockRate, 20.0,
                $"the board's own count is {wallClockRate:0.0} generations/s, not below the requested 20");

            Assert.IsTrue(controller.AchievedRateMeasured,
                "no rate window has closed, so the panel has nothing to report");
            Assert.Greater(controller.AchievedGenerationsPerSecond, 0f,
                "the clock is still advancing, just slower than asked");
            Assert.Less(controller.AchievedGenerationsPerSecond, 20f,
                "the achieved rate must be reported below the requested rate, not as equal to it");

            // The window estimate against the same-instant count. The tolerance is wide on
            // purpose: a 0.5 s window holding about eight frames moves by one frame when a
            // window boundary lands on a spike, and this test must not fail for that.
            Assert.That(controller.AchievedGenerationsPerSecond, Is.EqualTo(wallClockRate).Within(0.25 * wallClockRate),
                $"the panel reports {controller.AchievedGenerationsPerSecond:0.0} generations/s while the " +
                $"board's own generation counter says {wallClockRate:0.0} over the same {elapsed:0.00} s");

            Assert.IsTrue(StateText(controller).Contains("实际"),
                $"the state readout should distinguish target from achieved, got '{StateText(controller)}'");
            Assert.IsFalse(StateText(controller).Contains("采样中"),
                "a closed rate window must replace the sampling state");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// The pause button, the clock state it has to end, and what a resume may not replay.
        ///
        /// Why the test is shaped this way -- each part exists so that it can FAIL:
        ///
        /// <list type="bullet">
        /// <item>the clock is driven through the real play button, so the pause goes through
        /// ToggleRunning, which is the call that ends the run's clock state;</item>
        /// <item>the interval is one second and a step costs 60 ms, so the pending time can be
        /// OBSERVED and the test waits until the clock is over 0.8 s into its interval before
        /// pausing. That remainder is what makes the resume assertion able to fail: a clock that
        /// carried it owes its next generation within ~0.2 s of the resume, while a clock that
        /// starts from zero cannot owe one before a full second has passed. The earlier version
        /// paused at an arbitrary moment on a 50 ms interval and then asserted "at most four
        /// generations in two frames", which the per-frame cap guarantees whether or not the
        /// pause cleared anything;</item>
        /// <item>the cleared state is asserted directly, because with the caps in place the debt
        /// that can survive a frame boundary is always below one clock interval: whole
        /// generations of debt are discarded inside the frame that incurs them, so the only
        /// thing left to leak across a pause is the sub-interval remainder plus the run's rate
        /// report.</item>
        /// </list>
        /// </summary>
        [UnityTest]
        public IEnumerator PauseButton_ClearsTheClockState_SoResumingDoesNotReplayTheRemainder()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            const float interval = 1f;
            SetSpeed(controller, 1);

            PressPlay(RunButtonText);
            Assert.IsTrue(IsRunning(controller), "the play button did not start the clock");

            float deadline = Time.realtimeSinceStartup + 20f;
            while (PendingSeconds(controller) < interval * 0.8f && Time.realtimeSinceStartup < deadline)
                yield return null;

            // Read and pressed in the same frame, with no yield in between: this is the pending
            // time the pause sees.
            float pendingAtPause = PendingSeconds(controller);
            Assert.GreaterOrEqual(pendingAtPause, interval * 0.8f,
                $"the fixture could not bring the clock close to its next generation " +
                $"(pending {pendingAtPause:0.000} s of {interval:0.0} s), so there is nothing to test");

            // The run has been going for longer than one rate window, so it has a published rate
            // to lose. Without this precondition the "the pause cleared the report" assertions
            // below could pass against a report that was never there.
            Assert.IsTrue(controller.AchievedRateMeasured,
                "the run had not published a rate before the pause, so the pause cannot be shown to clear one");

            PressPlay(PauseButtonText);
            Assert.IsFalse(IsRunning(controller), "the pause button did not stop the clock");

            Assert.AreEqual(0f, PendingSeconds(controller), 1e-6f,
                "pausing left the clock's pending time in place, so a resume would replay it");
            Assert.AreEqual(0, WindowGenerations(controller),
                "the rate window that was in progress survived the pause");
            Assert.AreEqual(0.0, WindowSeconds(controller), 1e-9,
                "the rate window that was in progress survived the pause");
            Assert.AreEqual(0f, controller.AchievedGenerationsPerSecond,
                "the rate measured before the pause is still published after it");
            Assert.IsFalse(controller.AchievedRateMeasured,
                "the paused run still claims a measured rate");
            Assert.IsFalse(controller.ClockOverloaded,
                "the overload state from before the pause survived it");
            Assert.AreEqual("已暂停", StateText(controller),
                "the state readout does not describe the paused clock");

            var slow = (SlowBackend)ReadBackend(controller);
            int paused = slow.Generation;
            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.AreEqual(paused, slow.Generation, "a paused clock must not keep advancing");

            // Resume: the new run must not wear the old run's report...
            PressPlay(RunButtonText);
            Assert.IsTrue(IsRunning(controller), "the play button did not restart the clock");
            Assert.IsFalse(controller.AchievedRateMeasured,
                "the resumed run publishes a rate it has not measured yet");
            Assert.IsTrue(StateText(controller).Contains("采样中"),
                "the first frames of a new run have no measured rate, so the panel must say it is " +
                $"sampling rather than showing the initialised zero, got '{StateText(controller)}'");

            // ... and must not replay the remainder the pause interrupted. The observation
            // window sits between the two possibilities, with margin on both sides: with the
            // remainder kept, a generation is owed after ~0.2 s; without it, none can be owed
            // before 1.0 s.
            int beforeResume = slow.Generation;
            float observedUntil = Time.realtimeSinceStartup + 0.4f;
            while (Time.realtimeSinceStartup < observedUntil)
                yield return null;

            Assert.AreEqual(beforeResume, slow.Generation,
                "resuming replayed the clock time that was pending before the pause: a fresh run " +
                "cannot owe a generation before a full second has passed");

            StopClock(controller);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ResetClearsTheDebt_SoTheFreshBoardIsNotFastForwarded()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            yield return InstallSlowBackend(controller, stepCostMs: 60.0);

            SetSpeed(controller, 20);
            PressPlay(RunButtonText);

            // Let the clock accumulate both debt and a rate report.
            float deadline = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.IsTrue(controller.AchievedRateMeasured,
                "the run produced no rate window, so the reset below cannot be shown to clear one");

            // Reset goes through the controller's own command, which is what the "↺ 重置"
            // button calls: it stops the clock, restores the initial board and clears the state.
            MethodInfo reset = typeof(LifeTerminalController).GetMethod(
                "ResetToInitialState", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "ResetToInitialState not found");
            reset.Invoke(controller, null);
            yield return null;

            var slow = (SlowBackend)ReadBackend(controller);
            int afterReset = slow.Generation;
            Assert.AreEqual(0, afterReset, "reset returns the board to generation 0");
            Assert.IsFalse(IsRunning(controller), "reset stops the clock");
            Assert.AreEqual(0f, PendingSeconds(controller), 1e-6f,
                "reset left the previous run's pending clock time behind");
            Assert.IsFalse(controller.ClockOverloaded,
                "reset left the previous run's overload state behind");
            Assert.IsFalse(controller.AchievedRateMeasured,
                "reset left the previous run's measured rate behind");

            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.AreEqual(0, slow.Generation,
                "the clock must not fast-forward the restored board through old debt");
        }
    }
}
