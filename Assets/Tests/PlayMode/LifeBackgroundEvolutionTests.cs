using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage D: CPU evolution on a worker, and the interleavings that decide what the interface
    /// is allowed to show.
    ///
    /// Two kinds of test, deliberately kept apart:
    ///
    /// <list type="bullet">
    /// <item><b>Ownership and identity against the real thread pool.</b>
    /// <see cref="LifeAsyncCpuBackend"/> is driven directly on a board large enough that a
    /// generation takes long enough to observe from the main thread: every read the interface can
    /// make must still describe the last ADOPTED generation while the worker is computing, a
    /// second step must be refused rather than queued, an unadopted result must not be
    /// overwritten, a replaced board must refuse the result computed before it, and the answer
    /// that finally lands must be the synchronous reference's own answer cell for cell.</item>
    /// <item><b>Operation order through the controller with a controllable task.</b> The same
    /// backend, with the work item held by the test instead of the thread pool
    /// (<see cref="LifeAsyncCpuBackend"/> takes its scheduler as a constructor parameter), so
    /// "the pause lands while the generation is in flight" is a fact rather than a race. This is
    /// where the pause semantics are pinned down: the display freezes, the finished generation is
    /// kept, and resuming or single-stepping takes it over in order.</item>
    /// </list>
    /// </summary>
    public sealed class LifeBackgroundEvolutionTests
    {
        /// <summary>
        /// Holds a submitted computation until the test runs it, on the main thread, at a moment
        /// the test chose. The backend under test is the real one -- only the thread is missing.
        /// </summary>
        private sealed class ManualScheduler
        {
            private Action pending;

            public int SubmitCount { get; private set; }

            public bool HasPending => pending != null;

            public void Schedule(Action work)
            {
                Assert.IsNull(pending, "the backend submitted a second computation while one was still held");
                pending = work;
                SubmitCount++;
            }

            public void RunPending()
            {
                Action work = pending;
                Assert.IsNotNull(work, "no computation was submitted to run");
                pending = null;
                work();
            }
        }

        private const string RunButtonText = "▶ 运行";
        private const string PauseButtonText = "Ⅱ 暂停";
        private const string StepButtonText = "▸ 单步";
        private const string ResetButtonText = "↺ 重置";
        private const string BlinkerButtonPrefix = "闪烁器";

        // -- plumbing ----------------------------------------------------------

        private static LifeTerminalController Controller()
        {
            LifeTerminalController controller = UnityEngine.Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");
            return controller;
        }

        private static VisualElement Root() => Controller().GetComponent<UIDocument>().rootVisualElement;

        private static LifeGridElement Grid() =>
            Root().Q(name: null, className: "life-grid") as LifeGridElement;

        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

        private static void Press(Button button)
        {
            Assert.IsNotNull(button, "cannot press a missing button");
            using NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }

        private static void PressButton(string textPrefix)
        {
            Button button = null;
            foreach (Button candidate in Root().Query<Button>().ToList())
            {
                if (candidate.text.StartsWith(textPrefix))
                {
                    button = candidate;
                    break;
                }
            }

            Assert.IsNotNull(button, $"no button reads '{textPrefix}'");
            Press(button);
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

        private static void StopClock(LifeTerminalController controller)
        {
            MethodInfo stop = typeof(LifeTerminalController).GetMethod(
                "Stop", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(stop, "LifeTerminalController.Stop not found");
            stop.Invoke(controller, null);
        }

        private static void SetSpeed(LifeTerminalController controller, int generationsPerSecond)
        {
            SliderInt slider = ReadField<SliderInt>(controller, "speedSlider");
            slider.value = generationsPerSecond;
        }

        private static ILifeBackend ReadBackend(LifeTerminalController controller) =>
            ReadField<ILifeBackend>(controller, "backend");

        private static uint[] ReadCells(ILifeBackend backend)
        {
            var cells = new uint[backend.Width * backend.Height];
            Assert.IsTrue(backend.TryReadAllCells(cells), "the backend refused a full readback");
            return cells;
        }

        private static int Population(ILifeBackend backend)
        {
            Assert.IsTrue(backend.TryGetPopulation(out int population),
                "the CPU path must answer a population query");
            return population;
        }

        private static string StateText() => ReadField<Label>(Controller(), "stateLabel").text;

        /// <summary>
        /// Yields frames until the condition holds, and fails loudly if it never does. The
        /// condition is checked before the first yield, so an already-true condition costs nothing.
        /// </summary>
        private static IEnumerator WaitFor(Func<bool> condition, int frameBudget, string what)
        {
            for (int frame = 0; frame < frameBudget; frame++)
            {
                if (condition())
                    yield break;

                yield return null;
            }

            Assert.Fail($"waited {frameBudget} frames for {what}");
        }

        /// <summary>Runs a fixed number of frames, so a pump that should do nothing gets the chance to.</summary>
        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
                yield return null;
        }

        /// <summary>Turns a 0/1 readback into a board a backend can be loaded with.</summary>
        private static byte[] CellsToBytes(uint[] cells)
        {
            var board = new byte[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                board[i] = cells[i] != 0 ? (byte)1 : (byte)0;

            return board;
        }

        private static byte[] RandomBoard(int width, int height, int seed, double density)        {
            var board = new byte[width * height];
            var random = new System.Random(seed);
            for (int i = 0; i < board.Length; i++)
                board[i] = random.NextDouble() < density ? (byte)1 : (byte)0;

            return board;
        }

        // -- fixture state -----------------------------------------------------

        private ILifeBackend originalBackend;
        private LifeAsyncCpuBackend originalCpuBackend;
        private LifeAsyncCpuBackend injected;

        [UnitySetUp]
        public IEnumerator CaptureLiveBackend()
        {
            yield return Settle();

            LifeTerminalController controller = Controller();
            originalBackend = ReadBackend(controller);
            originalCpuBackend = ReadField<LifeAsyncCpuBackend>(controller, "cpuBackend");
            Assert.IsNotNull(originalBackend, "the terminal has no live backend to restore later");

            StopClock(controller);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator RestoreLiveBackend()
        {
            LifeTerminalController controller = Controller();
            StopClock(controller);

            if (injected != null)
            {
                // Both fields, and the selector: the controller reads `backend` while running, and
                // `cpuBackend` when the backend selector switches back to the CPU path.
                SetField(controller, "backend", originalBackend);
                SetField(controller, "cpuBackend", originalCpuBackend);

                DropdownField field = Root().Q<DropdownField>("backend-field");
                if (field != null && field.enabledSelf && field.value != BackendLabel(originalBackend))
                    field.value = BackendLabel(originalBackend);

                Grid().Bind(originalBackend);
                injected.Dispose();
                injected = null;
            }

            yield return null;
            yield return null;
        }

        private static string BackendLabel(ILifeBackend backend) =>
            backend.Name == "GPU" ? "GPU（Compute Shader）" : "CPU（参考实现）";

        /// <summary>
        /// Puts a controllable background CPU backend in place of the live one, sized like the live
        /// board so the controller's own commands keep working, and loads the blinker through the
        /// specimen archive: three live cells, period 2, so a generation changes the shape while the
        /// population stays countable.
        /// </summary>
        private IEnumerator InstallControllableBackend(ManualScheduler scheduler) =>
            InstallControllableBackend(scheduler, stepRule: null);

        /// <summary>
        /// The same, with the rule step injected. A failing step is how a worker failure is
        /// produced on purpose: it happens where failures actually happen (inside the computation),
        /// and it can be made to throw AFTER advancing the simulation, which is the case a retry
        /// has to survive.
        /// </summary>
        private IEnumerator InstallControllableBackend(ManualScheduler scheduler, Action<LifeSimulation> stepRule)
        {
            LifeTerminalController controller = Controller();

            injected = new LifeAsyncCpuBackend(
                originalBackend.Width, originalBackend.Height, scheduler.Schedule, stepRule);
            SetField(controller, "cpuBackend", injected);

            DropdownField field = Root().Q<DropdownField>("backend-field");
            if (field != null && field.enabledSelf)
            {
                // The real switch, so the selector and the backend the test drives agree. It binds
                // the grid, restarts from the initial board and leaves the clock paused.
                field.value = "CPU（参考实现）";
            }
            else
            {
                // No compute shaders on this machine: the CPU path is already the active one, so
                // the live backend has to be patched directly.
                SetField(controller, "backend", injected);
                Grid().Bind(injected);
            }

            yield return null;
            Assert.AreSame(injected, ReadBackend(controller), "the CPU backend under test is not the live one");

            PressButton(BlinkerButtonPrefix);
            yield return null;

            Assert.AreEqual(3, Population(injected), "the blinker should be on the board with three live cells");
            Assert.AreEqual(0, injected.Generation, "loading a specimen restarts at generation 0");
            yield return null;
        }

        // -- ownership and identity, against the real thread pool --------------

        /// <summary>
        /// The question the requirement is written about: while a generation is being computed,
        /// does anything the interface can read come from the board being written? Every read must
        /// describe the last adopted generation.
        ///
        /// <para>The reads happen in the same frame as the check that the worker is running -- a
        /// PlayMode frame here is longer than a generation at these sizes, so reading once per frame
        /// would usually read after the worker had finished, which is no evidence at all. A full
        /// board read costs about as much as a generation, so ONE of them fits; the deterministic
        /// proof that a computed board stays invisible until it is adopted is
        /// <see cref="CompletedGeneration_IsWrittenByTheWorker_AndStillInvisibleUntilAdopted"/>,
        /// which does not depend on timing.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator WhileComputing_EveryReadDescribesTheAdoptedGenerationOnly()
        {
            var backend = new LifeAsyncCpuBackend(4096, 4096);
            try
            {
                byte[] board = RandomBoard(4096, 4096, seed: 20260915, density: 0.30);
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                uint[] before = ReadCells(backend);
                int generationBefore = backend.Generation;
                int populationBefore = Population(backend);
                Assert.AreEqual(0, generationBefore, "a loaded board starts at generation 0");

                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the step should be running on the worker");

                int counterReads = 0;
                int boardReads = 0;

                while (backend.IsComputing)
                {
                    // The cheap reads the terminal makes on every frame: the counter and the
                    // population. Neither may come from the board being computed.
                    Assert.AreEqual(generationBefore, backend.Generation,
                        "the generation counter moved while the worker was still computing");
                    Assert.AreEqual(populationBefore, Population(backend),
                        "the population readout came from a board that is still being computed");
                    counterReads++;

                    // One full read of the board the grid uploads, compared with the sequence
                    // comparison rather than NUnit's collection assert: the latter walks 16 million
                    // elements through an enumerator and takes longer than the generation itself.
                    uint[] during = ReadCells(backend);
                    Assert.IsTrue(during.AsSpan().SequenceEqual(before),
                        "the display read a board that the worker is writing");
                    boardReads++;
                }

                Assert.GreaterOrEqual(counterReads, 1,
                    "no read completed while the worker was computing, so this test proved nothing");
                Assert.GreaterOrEqual(boardReads, 1, "a full board read must have overlapped the computation");

                yield return WaitFor(() => backend.HasCompletedGeneration, 300, "the completed generation");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the completed generation should be adoptable");
                Assert.AreEqual(1, outcome.Generation, "the first adopted generation after a load is 1");
                Assert.AreEqual(1, backend.Generation, "adoption moves the displayed generation");
                Assert.Greater(outcome.ComputeMilliseconds, 0.0, "the worker must report its own compute cost");

                // Same board, same rules, synchronous reference: the answer has to match cell for
                // cell, or moving the work off the frame changed the simulation.
                using var reference = new CpuLifeBackend(4096, 4096);
                reference.WrapEdges = true;
                reference.LoadBoard(board);
                reference.Step();

                uint[] expected = ReadCells(reference);
                uint[] adopted = ReadCells(backend);
                Assert.IsTrue(adopted.AsSpan().SequenceEqual(expected),
                    "the background result differs from the synchronous reference");
                Assert.AreEqual(Population(reference), outcome.Population,
                    "the population travelling with the result is not the population of the new board");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// The ownership proof, with the computation held so that nothing about it depends on
        /// timing: the worker writes its own board and publishes it, and the interface STILL reads
        /// the generation it had adopted. The new board becomes readable only through adoption.
        ///
        /// <para>A plain test, not a UnityTest: the held scheduler runs the computation on this
        /// thread, so the whole sequence is synchronous and needs no frames.</para>
        /// </summary>
        [Test]
        public void CompletedGeneration_IsWrittenByTheWorker_AndStillInvisibleUntilAdopted()
        {
            var scheduler = new ManualScheduler();
            var backend = new LifeAsyncCpuBackend(256, 256, scheduler.Schedule);
            try
            {
                byte[] board = RandomBoard(256, 256, seed: 21, density: 0.30);
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                uint[] before = ReadCells(backend);
                int populationBefore = Population(backend);

                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the step should be in flight");

                // Nothing has been written yet, and nothing may be visible.
                Assert.AreEqual(0, backend.Generation, "a submitted generation must not move the counter");
                Assert.IsTrue(backend.TryGetPopulation(out int populationWhileComputing));
                Assert.AreEqual(populationBefore, populationWhileComputing,
                    "the population changed before anything was computed");
                Assert.IsTrue(ReadCells(backend).AsSpan().SequenceEqual(before),
                    "the board changed before anything was computed");

                // Now the worker writes the new board and publishes it.
                scheduler.RunPending();
                Assert.IsTrue(backend.HasCompletedGeneration, "the finished generation should be waiting");
                Assert.IsFalse(backend.IsComputing, "the worker is done");

                Assert.AreEqual(0, backend.Generation,
                    "a computed but unadopted generation moved the displayed counter");
                Assert.IsTrue(backend.TryGetPopulation(out int populationAfterComputing));
                Assert.AreEqual(populationBefore, populationAfterComputing,
                    "the population readout came from the board the worker just produced");
                Assert.IsTrue(ReadCells(backend).AsSpan().SequenceEqual(before),
                    "the display read the board the worker produced instead of the adopted one");

                // Adoption is the only way in, and what arrives is the reference rules' own answer.
                using var reference = new CpuLifeBackend(256, 256);
                reference.WrapEdges = true;
                reference.LoadBoard(board);
                reference.Step();

                uint[] expected = ReadCells(reference);
                Assert.IsTrue(backend.HasCompletedGeneration, "the result is still waiting to be adopted");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out _), "the result should be adoptable");
                Assert.AreEqual(1, backend.Generation, "adoption moves the displayed generation");

                uint[] adopted = ReadCells(backend);
                Assert.IsTrue(adopted.AsSpan().SequenceEqual(expected),
                    "the adopted board is not the reference rules' answer");
                Assert.IsFalse(adopted.AsSpan().SequenceEqual(before),
                    "the adopted board is the old board, so the computation produced nothing");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// One generation at a time: a step asked for while one is in flight, or while a finished one
        /// waits to be adopted, is refused and counted -- not queued, and not allowed to overwrite a
        /// result nobody has received.
        /// </summary>
        [UnityTest]
        public IEnumerator SecondStepIsRefused_AndAnUnadoptedResultIsNotOverwritten()
        {
            var backend = new LifeAsyncCpuBackend(512, 512);
            try
            {
                byte[] board = RandomBoard(512, 512, seed: 7, density: 0.35);
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the first step should be running");

                backend.Step();
                Assert.AreEqual(1, backend.RefusedSubmissions,
                    "a step submitted while one is in flight must be refused, not queued");

                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "the completed generation");

                // The buffer of a result nobody has received is reserved: a step here would have
                // nowhere to write that would not destroy it.
                backend.Step();
                Assert.AreEqual(2, backend.RefusedSubmissions,
                    "a step submitted while a result waits to be adopted must be refused");
                Assert.IsTrue(backend.HasCompletedGeneration, "the waiting result is still there");

                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the waiting result should still be adoptable");
                Assert.AreEqual(1, outcome.Generation);
                Assert.AreEqual(1, backend.AdoptedGenerations);

                using var reference = new CpuLifeBackend(512, 512);
                reference.WrapEdges = true;
                reference.LoadBoard(board);
                reference.Step();
                CollectionAssert.AreEqual(ReadCells(reference), ReadCells(backend),
                    "the refused submissions disturbed the result that was already waiting");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// A board replaced while a generation is in flight: the result is refused, counted, and the
        /// new board stands. The pipeline then keeps working, and the generation numbering continues
        /// from where the replacement put it.
        /// </summary>
        [UnityTest]
        public IEnumerator ReplacedBoardRefusesTheOldResult_AndKeepsItsOwnNumbering()
        {
            var backend = new LifeAsyncCpuBackend(512, 512);
            try
            {
                byte[] first = RandomBoard(512, 512, seed: 11, density: 0.30);
                byte[] second = RandomBoard(512, 512, seed: 12, density: 0.30);

                backend.WrapEdges = true;
                backend.LoadBoard(first);
                backend.Step();
                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "the first generation");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out _), "the first generation should be adopted");
                Assert.AreEqual(1, backend.Generation);

                // Replace the board while the next generation is in flight.
                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the second step should be running");
                backend.LoadBoard(second);
                Assert.AreEqual(0, backend.Generation, "loading a board resets the displayed generation");

                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "the stale generation");
                Assert.IsFalse(backend.TryAdoptCompletedGeneration(out _),
                    "a generation computed from the replaced board must not be adopted");
                Assert.AreEqual(1, backend.RefusedGenerations, "the refusal must be counted, not silent");
                Assert.AreEqual(0, backend.Generation, "the loaded board must still be displayed");

                // The pipeline works again, and the generation it produces follows the replaced
                // board's numbering rather than the old one's.
                backend.Step();
                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "the generation after the reload");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the generation computed from the new board should be adopted");
                Assert.AreEqual(1, outcome.Generation,
                    "the new board's first generation is 1, not the old board's next number");

                using var reference = new CpuLifeBackend(512, 512);
                reference.WrapEdges = true;
                reference.LoadBoard(second);
                reference.Step();
                CollectionAssert.AreEqual(ReadCells(reference), ReadCells(backend),
                    "the adopted board is not the new board advanced by one generation");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// An edit after a generation has been adopted must not renumber the board. The worker
        /// rebuilds its own simulation from the display board, and the generations after that
        /// rebuild continue from the displayed generation.
        /// </summary>
        [UnityTest]
        public IEnumerator EditThenStep_ContinuesTheDisplayedGenerationNumbering()
        {
            var backend = new LifeAsyncCpuBackend(256, 256);
            try
            {
                backend.WrapEdges = true;
                backend.LoadBoard(RandomBoard(256, 256, seed: 3, density: 0.30));

                backend.Step();
                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "generation 1");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out _));
                Assert.AreEqual(1, backend.Generation);

                backend.SetCell(0, 0, true);
                backend.SetCell(1, 0, true);
                backend.SetCell(2, 0, true);

                backend.Step();
                yield return WaitFor(() => backend.HasCompletedGeneration, 120, "the generation after the edit");
                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the generation after an edit should be adopted");
                Assert.AreEqual(2, outcome.Generation, "an edit must not restart the generation numbering");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// Disposing refuses the work in flight and does not wait for it: the destroy path runs on
        /// the main thread and must not turn a clean exit into a stall.
        /// </summary>
        [UnityTest]
        public IEnumerator Dispose_RefusesTheResultInFlight_AndDoesNotWait()
        {
            var scheduler = new ManualScheduler();
            var backend = new LifeAsyncCpuBackend(256, 256, scheduler.Schedule);
            backend.WrapEdges = true;
            backend.LoadBoard(RandomBoard(256, 256, seed: 5, density: 0.30));

            backend.Step();
            Assert.IsTrue(backend.IsComputing, "the step should be in flight");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            backend.Dispose();
            watch.Stop();

            Assert.Less(watch.Elapsed.TotalMilliseconds, 50.0,
                "disposing must not block the main thread waiting for the computation");

            scheduler.RunPending();

            Assert.IsFalse(backend.HasCompletedGeneration, "a disposed backend must publish nothing");
            Assert.IsFalse(backend.TryAdoptCompletedGeneration(out _), "a disposed backend must adopt nothing");
            Assert.AreEqual(0, backend.Generation);
            Assert.IsFalse(backend.IsComputing, "the worker's completion must still release the flag");
            yield return null;
        }

        // -- operation order, through the controller, with a controllable task --

        /// <summary>
        /// The pause semantics, end to end through the real controls: a generation that is in flight
        /// when the user pauses must not appear, must not be thrown away, and must not be skipped
        /// when the clock resumes.
        /// </summary>
        [UnityTest]
        public IEnumerator PauseDuringCompute_FreezesTheDisplay_AndResumeTakesTheResultInOrder()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");
            Assert.IsTrue(injected.IsComputing, "the submitted generation should be in flight");

            int frozen = injected.Generation;
            Assert.AreEqual(0, frozen, "nothing has been adopted yet");

            // The user pauses while the generation is still being computed.
            PressButton(PauseButtonText);
            Assert.IsFalse(ReadField<bool>(controller, "running"), "the pause button did not stop the clock");
            Assert.AreEqual("已暂停", StateText(), "a paused clock says so");

            // The computation finishes behind the pause.
            scheduler.RunPending();
            yield return Frames(5);

            Assert.AreEqual(frozen, injected.Generation,
                "a paused clock displayed a generation it had not taken over");
            Assert.IsTrue(injected.HasCompletedGeneration,
                "the finished generation must be kept for the resume, not thrown away");
            Assert.AreEqual(0, injected.RefusedGenerations, "nothing replaced the board, so nothing may be refused");

            // Resuming takes it over, in order, without computing it again.
            int submissions = scheduler.SubmitCount;
            PressButton(RunButtonText);
            Assert.IsTrue(ReadField<bool>(controller, "running"), "the play button did not restart the clock");

            yield return WaitFor(() => injected.Generation == frozen + 1, 10,
                "the resume to take over the generation that was computed");
            Assert.IsFalse(injected.HasCompletedGeneration, "the kept generation should have been taken over");
            Assert.AreEqual(1, injected.AdoptedGenerations, "exactly one generation was displayed");
            Assert.AreEqual(submissions, scheduler.SubmitCount,
                "the resume computed a second generation instead of taking over the one that was waiting");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// A running clock must not pile work up behind a generation that has not finished. It waits,
        /// says it is behind, and submits exactly one generation when the pipeline frees up.
        /// </summary>
        [UnityTest]
        public IEnumerator RunningClock_DoesNotPileUpWorkBehindAnUnfinishedGeneration()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");

            int submissions = scheduler.SubmitCount;

            // The clock keeps asking for a rate it cannot reach while the generation is unfinished.
            // Frames are not a fixed budget here: the condition is what the test is about.
            yield return WaitFor(() => controller.ClockOverloaded, 600,
                "the clock to report that it is dropping catch-up debt");

            Assert.AreEqual(submissions, scheduler.SubmitCount,
                "the clock submitted more work while a generation was still unfinished");
            Assert.AreEqual(0, injected.RefusedSubmissions,
                "the controller must not even ask while a generation is in flight");
            Assert.AreEqual(0, injected.Generation, "nothing has been displayed yet");

            // Freeing the pipeline lets the run continue: one generation is displayed, not forty.
            scheduler.RunPending();
            yield return WaitFor(() => injected.Generation == 1, 10, "the held generation to land");
            Assert.AreEqual(1, injected.AdoptedGenerations, "exactly the one generation that was computed");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// Single-stepping while a computed generation is waiting takes that generation over first.
        /// The user asked for one more generation on screen, not for a second computation.
        /// </summary>
        [UnityTest]
        public IEnumerator SingleStep_TakesOverTheWaitingGeneration_InsteadOfComputingAnother()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");

            PressButton(PauseButtonText);
            scheduler.RunPending();
            yield return Frames(2);

            int waiting = injected.Generation;
            int submissions = scheduler.SubmitCount;

            PressButton(StepButtonText);
            yield return WaitFor(() => injected.Generation == waiting + 1, 10,
                "单步 to take over the waiting generation");

            Assert.AreEqual(submissions, scheduler.SubmitCount,
                "单步 started a second computation while a finished generation was waiting");
            Assert.AreEqual("已暂停", StateText(), "with nothing left to compute, the clock is simply paused");
            yield return null;
        }

        /// <summary>
        /// 单步 with an empty pipeline: exactly one generation is submitted, and the interface says it
        /// is computing rather than pretending the board already moved.
        /// </summary>
        [UnityTest]
        public IEnumerator SingleStep_SubmitsExactlyOneGeneration_AndSaysItIsComputing()
        {
            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            int before = injected.Generation;
            PressButton(StepButtonText);
            yield return null;

            Assert.AreEqual(1, scheduler.SubmitCount, "单步 should submit exactly one generation");
            Assert.IsTrue(injected.IsComputing, "the generation should be in flight");
            Assert.AreEqual("单步计算中…", StateText(),
                "the interface must say the generation is still being computed");
            Assert.AreEqual(before, injected.Generation, "nothing has been displayed yet");

            // A second press while the first is in flight must not accumulate another generation.
            PressButton(StepButtonText);
            yield return null;

            Assert.AreEqual(1, scheduler.SubmitCount, "a second press queued a second generation");
            Assert.AreEqual(0, injected.RefusedSubmissions, "the pipeline was asked for work it cannot hold");

            scheduler.RunPending();
            yield return WaitFor(() => injected.Generation == before + 1, 10, "the single step to land");
            Assert.AreEqual("已暂停", StateText(), "with nothing in flight the clock is paused again");
            yield return null;
        }

        /// <summary>
        /// A reset while a generation is in flight: the board it computed belongs to a board that no
        /// longer exists. It must not appear, it must be counted, and the pipeline must keep
        /// delivering generations afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator ResetDuringCompute_RefusesTheOldGeneration_AndKeepsTheResetBoard()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            uint[] initialBoard = ReadCells(injected);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");

            PressButton(ResetButtonText);
            yield return null;

            Assert.AreEqual(0, injected.Generation, "reset returns the board to generation 0");
            CollectionAssert.AreEqual(initialBoard, ReadCells(injected),
                "reset must restore the board the experiment started from");

            // The generation computed before the reset finishes now. It is not displayed, and the
            // interface does not even have to adopt it to keep working -- but when it does try, the
            // refusal is counted rather than silent.
            scheduler.RunPending();
            yield return Frames(3);
            Assert.AreEqual(0, injected.Generation,
                "a generation computed before the reset overwrote the reset board");

            PressButton(StepButtonText);
            yield return null;

            Assert.AreEqual(1, injected.RefusedGenerations,
                "the generation computed for the replaced board must be refused and counted");
            Assert.IsTrue(scheduler.HasPending, "单步 after a refused generation must submit one");
            Assert.AreEqual(0, injected.RefusedSubmissions, "the refused result left the pipeline free");

            scheduler.RunPending();
            yield return WaitFor(() => injected.Generation == 1, 10, "the generation after the reset");
            Assert.AreEqual(3, Population(injected), "the blinker's population after one generation");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// Switching backends while the CPU generation is in flight: the result belongs to a backend
        /// that is no longer on screen, and switching back must show the restarted board, not the
        /// generation computed before the switch.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingBackend_DoesNotLetTheOldCpuResultLand()
        {
            LifeTerminalController controller = Controller();
            DropdownField field = Root().Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            if (!field.enabledSelf)
                Assert.Ignore("compute shaders unavailable on this machine; the switch was not exercised");

            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");

            // Switch to the GPU while the CPU generation is in flight.
            field.value = "GPU（Compute Shader）";
            yield return null;
            Assert.AreEqual("GPU", ReadBackend(controller).Name, "the selector did not switch to the GPU");

            scheduler.RunPending();
            yield return Frames(3);
            Assert.AreEqual(0, ReadBackend(controller).Generation,
                "the GPU board must not have been moved by the CPU result");

            // Back to the CPU: the restarted board stands, and the stale generation is refused rather
            // than landing on it.
            field.value = "CPU（参考实现）";
            yield return null;
            Assert.AreSame(injected, ReadBackend(controller), "the switch did not return to the CPU backend");
            Assert.AreEqual(0, injected.Generation, "switching back restarts from the initial state");

            PressButton(StepButtonText);
            yield return null;
            Assert.IsTrue(scheduler.HasPending, "单步 after the switch must submit one generation");
            Assert.AreEqual(1, injected.RefusedGenerations,
                "the generation computed before the switch must be refused, and counted");

            scheduler.RunPending();
            yield return WaitFor(() => injected.Generation == 1, 10, "the generation after the switch");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// The guarantee that a failure cannot be retried away by accident, tested where it has to
        /// hold: the backend itself, in the same lock that would accept the work.
        ///
        /// <para>The window this closes is not a controller bug that another controller check could
        /// shrink: the worker runs on another thread and can fail between a caller's "is anything
        /// wrong?" test and its "submit the next one" call. If the submission were accepted there,
        /// it would clear a failure nobody has seen and the system would have retried itself.
        /// Nothing but <see cref="LifeAsyncCpuBackend.ClearFailure"/> (an explicit retry) or a
        /// command that replaces the board may lift the failed state.</para>
        /// </summary>
        [Test]
        public void FailedBackend_RefusesSubmissions_UntilSomethingExplicitlyClearsTheFailure()
        {
            var scheduler = new ManualScheduler();
            int calls = 0;
            var backend = new LifeAsyncCpuBackend(8, 8, scheduler.Schedule, simulation =>
            {
                calls++;
                if (calls == 1)
                {
                    simulation.Step();
                    throw new InvalidOperationException("injected rule failure");
                }

                simulation.Step();
            });

            try
            {
                backend.WrapEdges = true;
                backend.LoadBoard(EdgeBlinker());

                backend.Step();
                Assert.AreEqual(1, scheduler.SubmitCount, "the first submission should be accepted");
                scheduler.RunPending();

                Assert.IsNotNull(backend.FailureMessage, "the failure must be reported");
                Assert.IsFalse(backend.IsComputing, "the worker released the pipeline when it failed");

                // The window: a submission arriving after the failure, with nobody having retried.
                int submissions = scheduler.SubmitCount;
                backend.Step();

                Assert.AreEqual(submissions, scheduler.SubmitCount,
                    "a failed backend accepted a submission, so the system retried itself");
                Assert.IsNotNull(backend.FailureMessage, "the refused submission cleared the failure");
                Assert.AreEqual(1, backend.RefusedWhileFailed, "the refusal must be counted");
                Assert.AreEqual(0, backend.RefusedSubmissions,
                    "a failure refusal is not a 'busy' refusal and must not be counted as one");
                Assert.AreEqual(0, backend.Generation, "nothing may be displayed by a failed pipeline");

                // Only an explicit clear reopens it -- and the retry rebuilds from the display.
                backend.ClearFailure();
                Assert.IsNull(backend.FailureMessage, "ClearFailure must release the failed state");

                backend.Step();
                Assert.AreEqual(submissions + 1, scheduler.SubmitCount, "an explicit retry must be accepted");
                scheduler.RunPending();

                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the retry should produce an adoptable generation");
                Assert.AreEqual(1, outcome.Generation, "the retry follows the displayed generation");
                Assert.Greater(outcome.BoardLoadMilliseconds, 0.0,
                    "the retry must rebuild from the displayed board, not continue from the failed state");
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// The interface error belongs to the backend in use. A CPU failure must not sit on the
        /// readout while the GPU is the one running, and switching back must describe the CPU's
        /// current state rather than reusing whatever was there before.
        /// </summary>
        [UnityTest]
        public IEnumerator CpuFailure_ThenSwitchingToGpu_ShowsTheGpuState_NotTheOldError()
        {
            LifeTerminalController controller = Controller();
            DropdownField field = Root().Q<DropdownField>("backend-field");
            Assert.IsNotNull(field, "missing the backend selector");
            if (!field.enabledSelf)
                Assert.Ignore("compute shaders unavailable on this machine; the switch was not exercised");

            var scheduler = new ManualScheduler();
            yield return InstallControllableBackend(scheduler, simulation =>
            {
                simulation.Step();
                throw new InvalidOperationException("injected rule failure");
            });

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");
            scheduler.RunPending();
            yield return WaitFor(() => injected.FailureMessage != null, 10, "the failure to be reported");

            Label state = ReadField<Label>(controller, "stateLabel");
            Assert.AreEqual("演算失败", state.text, "the CPU failure should be on the readout first");

            // Switch to the GPU: the readout describes the backend that is now running, in the SAME
            // frame as the switch. Waiting a frame first would let the pump repair the mirror and
            // hide the one-frame window this test exists to close.
            field.value = "GPU（Compute Shader）";

            Assert.AreEqual("GPU", ReadBackend(controller).Name, "the selector did not switch to the GPU");
            Assert.IsFalse(state.text.Contains("演算失败"),
                $"the readout still shows the CPU failure after switching to the GPU: '{state.text}'");
            Assert.AreEqual("已暂停", state.text, "the switched-to backend starts paused");
            Assert.IsFalse((state.tooltip ?? string.Empty).Contains("injected"),
                $"the tooltip still carries the CPU failure: '{state.tooltip}'");

            yield return null;
            Assert.IsFalse(state.text.Contains("演算失败"),
                $"the readout shows the CPU failure while the GPU is running: '{state.text}'");

            // And the GPU path really works.
            PressButton(StepButtonText);
            yield return WaitFor(() => ReadBackend(controller).Generation == 1, 40, "the GPU single step");
            Assert.IsFalse(state.text.Contains("演算失败"),
                $"the readout shows the CPU failure while the GPU is running: '{state.text}'");

            // Back to the CPU: synced to ITS current state, again in the same frame.
            field.value = "CPU（参考实现）";

            Assert.AreSame(injected, ReadBackend(controller), "the switch did not return to the CPU backend");
            Assert.IsFalse(state.text.Contains("演算失败"),
                $"switching back reused the old mirror instead of reading the backend: '{state.text}'");
            Assert.IsNull(injected.FailureMessage,
                "replacing the board on the switch should have cleared the CPU failure");

            yield return null;

            // Which means the CPU path runs again without any further ceremony.
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the CPU run to submit a generation");
            Assert.IsFalse(state.text.Contains("演算失败"), "the recovered CPU run still shows a failure");

            StopClock(controller);
            yield return null;
        }

        // -- a boundary change is a rule change: refuse, then rebuild -----------

        /// <summary>
        /// A boundary change while a generation is in flight. The result belongs to the old rule and
        /// must be refused -- but refusing alone is not enough: the worker's simulation is then one
        /// generation ahead of the display under the WRONG rule, so unless it is rebuilt from the
        /// displayed board every later result fails the same check and the display never moves
        /// again. This test pins both halves.
        /// </summary>
        [Test]
        public void BoundaryChangeWhileComputing_RefusesTheOldResult_AndRebuildsUnderTheNewRule()
        {
            var scheduler = new ManualScheduler();
            var backend = new LifeAsyncCpuBackend(8, 8, scheduler.Schedule);
            try
            {
                byte[] board = EdgeBlinker();
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the step should be in flight");

                // The user switches the boundary while that generation is being computed.
                backend.WrapEdges = false;

                scheduler.RunPending();
                Assert.IsTrue(backend.HasCompletedGeneration, "the old-rule generation finished");
                Assert.IsFalse(backend.TryAdoptCompletedGeneration(out _),
                    "a generation computed under the old boundary must not be adopted");
                Assert.AreEqual(1, backend.RefusedGenerations, "the refusal must be counted");
                Assert.AreEqual(0, backend.Generation, "the display keeps the generation it had");

                AssertNextGenerationMatchesTheReference(backend, scheduler, board, wrapEdges: false);
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// The same shape, with the result already finished and waiting when the boundary changes:
        /// it is refused, the worker is rebuilt, and the next generation is the new rule's answer
        /// for the DISPLAYED board -- generation 1, not the old board's next number.
        /// </summary>
        [Test]
        public void BoundaryChangeWithAWaitingResult_RefusesIt_AndRebuildsUnderTheNewRule()
        {
            var scheduler = new ManualScheduler();
            var backend = new LifeAsyncCpuBackend(8, 8, scheduler.Schedule);
            try
            {
                byte[] board = EdgeBlinker();
                backend.WrapEdges = true;
                backend.LoadBoard(board);

                backend.Step();
                scheduler.RunPending();
                Assert.IsTrue(backend.HasCompletedGeneration, "the generation should be waiting");

                backend.WrapEdges = false;

                Assert.IsFalse(backend.TryAdoptCompletedGeneration(out _),
                    "a waiting generation computed under the old boundary must not be adopted");
                Assert.AreEqual(1, backend.RefusedGenerations);
                Assert.AreEqual(0, backend.Generation);

                AssertNextGenerationMatchesTheReference(backend, scheduler, board, wrapEdges: false);
            }
            finally
            {
                backend.Dispose();
            }
        }

        /// <summary>
        /// Submits one generation, adopts it, and checks it is cell-identical with a synchronous
        /// reference run under the given boundary -- which also pins that the worker was rebuilt
        /// from the displayed board (the outcome reports the rebuild) rather than carrying on from
        /// its own, possibly diverged, state.
        /// </summary>
        private static void AssertNextGenerationMatchesTheReference(
            LifeAsyncCpuBackend backend, ManualScheduler scheduler, byte[] displayedBoard, bool wrapEdges)
        {
            backend.Step();
            Assert.IsTrue(scheduler.HasPending, "the generation after the refusal should be submitted");
            scheduler.RunPending();

            Assert.IsTrue(backend.HasCompletedGeneration, "the generation after the rebuild should be complete");
            Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                "the generation after the rebuild should be adopted");
            Assert.AreEqual(1, outcome.Generation,
                "the rebuilt generation must follow the displayed one, not the worker's own count");
            Assert.Greater(outcome.BoardLoadMilliseconds, 0.0,
                "the worker must have been rebuilt from the displayed board, not continued");

            using var reference = new CpuLifeBackend(8, 8);
            reference.WrapEdges = wrapEdges;
            reference.LoadBoard(displayedBoard);
            reference.Step();

            uint[] expected = ReadCells(reference);
            uint[] adopted = ReadCells(backend);
            Assert.IsTrue(adopted.AsSpan().SequenceEqual(expected),
                "the generation after the rebuild is not the reference answer under the new boundary");

            // And the two boundaries really do differ on this board, so the test above is not
            // vacuous: if they agreed, refusing the old result would have proved nothing.
            using var otherRule = new CpuLifeBackend(8, 8);
            otherRule.WrapEdges = !wrapEdges;
            otherRule.LoadBoard(displayedBoard);
            otherRule.Step();
            Assert.IsFalse(expected.AsSpan().SequenceEqual(ReadCells(otherRule)),
                "the two boundaries evolve this board identically, so the test would be vacuous");
        }

        /// <summary>
        /// Three live cells on the top row of an 8x8 board. Wrapping makes the row above them alive
        /// as well, so the wrapping and fixed answers differ -- which is what makes a boundary
        /// change observable at all.
        /// </summary>
        private static byte[] EdgeBlinker()
        {
            var board = new byte[8 * 8];
            board[0] = 1;
            board[1] = 1;
            board[2] = 1;
            return board;
        }

        // -- failure: same identity rule as success ----------------------------

        /// <summary>
        /// A failure that belongs to the current session stops the automatic submission: the display
        /// keeps the last complete generation, the clock stops, the readout says so, and nothing is
        /// submitted again until somebody acts.
        /// </summary>
        [UnityTest]
        public IEnumerator Failure_StopsTheClockAndKeepsTheLastCompleteGeneration()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            int calls = 0;

            // The rule advances the simulation and THEN throws: the worker's state is already ahead,
            // which is exactly the case a naive retry would continue from.
            yield return InstallControllableBackend(scheduler, simulation =>
            {
                calls++;
                simulation.Step();
                throw new InvalidOperationException("injected rule failure");
            });

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");

            scheduler.RunPending();
            yield return WaitFor(() => injected.FailureMessage != null, 10, "the failure to be reported");

            Assert.AreEqual(1, calls, "the injected rule should have run once");
            Assert.IsFalse(ReadField<bool>(controller, "running"),
                "a failed generation must stop the clock instead of submitting more work");
            Assert.AreEqual(0, injected.Generation, "the display keeps the last complete generation");
            Assert.AreEqual("演算失败", StateText(), "the readout must say the run failed");

            int submissions = scheduler.SubmitCount;
            yield return Frames(20);

            Assert.AreEqual(submissions, scheduler.SubmitCount,
                "the failed pipeline kept submitting on its own");
            Assert.AreEqual(1, calls, "the injected rule ran again after failing");
            Assert.AreEqual(0, injected.Generation, "a generation was displayed by a failed pipeline");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// The retry: starting the clock again clears the failure, and the backend rebuilds the
        /// worker from the DISPLAYED board before computing -- so the generation that lands follows
        /// the display, even though the failed attempt had already advanced its own simulation.
        /// </summary>
        [UnityTest]
        public IEnumerator RetryAfterAFailure_RebuildsFromTheDisplayBoard_AndKeepsTheNumbering()
        {
            LifeTerminalController controller = Controller();
            var scheduler = new ManualScheduler();
            int calls = 0;

            yield return InstallControllableBackend(scheduler, simulation =>
            {
                calls++;
                if (calls == 1)
                {
                    simulation.Step();
                    throw new InvalidOperationException("injected rule failure");
                }

                simulation.Step();
            });

            uint[] displayed = ReadCells(injected);

            SetSpeed(controller, 20);
            PressButton(RunButtonText);
            yield return WaitFor(() => scheduler.HasPending, 30, "the clock to submit a generation");
            scheduler.RunPending();
            yield return WaitFor(() => injected.FailureMessage != null, 10, "the failure to be reported");

            // Retry through the real control.
            PressButton(RunButtonText);
            Assert.IsTrue(ReadField<bool>(controller, "running"), "the play button did not restart the clock");
            yield return WaitFor(() => scheduler.HasPending, 30, "the retry to submit a generation");

            scheduler.RunPending();
            yield return WaitFor(() => injected.Generation == 1, 20, "the retry to land");

            Assert.IsNull(injected.FailureMessage, "the retry did not clear the failure");
            Assert.IsTrue(StateText().StartsWith("演算中"),
                $"the readout did not recover from the failure, got '{StateText()}'");

            using var reference = new CpuLifeBackend(injected.Width, injected.Height);
            reference.WrapEdges = injected.WrapEdges;
            reference.LoadBoard(CellsToBytes(displayed));
            reference.Step();

            uint[] expected = ReadCells(reference);
            uint[] adopted = ReadCells(injected);
            Assert.IsTrue(adopted.AsSpan().SequenceEqual(expected),
                "the retry continued from the failed worker's own board instead of the displayed one");

            StopClock(controller);
            yield return null;
        }

        /// <summary>
        /// A failure that belongs to a board which has since been replaced is refused by session
        /// identity, exactly like a stale success: it reports nothing, reclaims its buffer, and does
        /// not leave the pipeline blocked.
        ///
        /// <para>A plain test, not a UnityTest: the held scheduler runs the computation on this
        /// thread, so the whole sequence is synchronous and needs no frames.</para>
        /// </summary>
        [Test]
        public void StaleFailure_DoesNotPolluteTheReplacedBoard()
        {
            var scheduler = new ManualScheduler();
            int calls = 0;
            var backend = new LifeAsyncCpuBackend(8, 8, scheduler.Schedule, simulation =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("injected rule failure");

                simulation.Step();
            });

            try
            {
                backend.WrapEdges = true;
                backend.LoadBoard(EdgeBlinker());
                backend.Step();
                Assert.IsTrue(backend.IsComputing, "the failing step should be in flight");

                // The board is replaced while that generation is being computed.
                byte[] replacement = RandomBoard(8, 8, seed: 4, density: 0.25);
                backend.LoadBoard(replacement);
                Assert.AreEqual(0, backend.Generation, "loading a board resets the displayed generation");

                scheduler.RunPending();

                Assert.IsNull(backend.FailureMessage,
                    "a failure from a replaced board polluted the current session");
                Assert.IsFalse(backend.HasCompletedGeneration, "a failed task publishes nothing");

                // The pipeline is usable: the next submission runs and lands as generation 1.
                int submissions = scheduler.SubmitCount;
                backend.Step();
                Assert.AreEqual(submissions + 1, scheduler.SubmitCount,
                    "the stale failure left the pipeline blocked");
                scheduler.RunPending();

                Assert.IsTrue(backend.TryAdoptCompletedGeneration(out LifeStepOutcome outcome),
                    "the generation after a stale failure should be adoptable");
                Assert.AreEqual(1, outcome.Generation);

                using var reference = new CpuLifeBackend(8, 8);
                reference.WrapEdges = true;
                reference.LoadBoard(replacement);
                reference.Step();
                uint[] expected = ReadCells(reference);
                uint[] adopted = ReadCells(backend);
                Assert.IsTrue(adopted.AsSpan().SequenceEqual(expected),
                    "the board after a stale failure is not the reference answer");
            }
            finally
            {
                backend.Dispose();
            }
        }
    }
}
