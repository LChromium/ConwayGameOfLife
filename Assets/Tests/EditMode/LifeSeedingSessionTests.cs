using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage-B session semantics. These are the rules that keep "adjust, then decide"
    /// honest: editing parameters may only move the candidate, cancelling must leave
    /// nothing behind, and a background result may only land if it is still wanted.
    ///
    /// Generation runs on a thread pool thread, so these tests pump it the way the
    /// terminal's frame loop does, with a wall-clock bound so a hung task fails rather
    /// than hanging the suite. The tests that are about OVERLAPPING requests inject a
    /// generator they can hold open, because "the old task had not finished yet" cannot
    /// be arranged with a generator that runs as fast as the machine allows.
    /// </summary>
    public sealed class LifeSeedingSessionTests
    {
        private const int Width = 128;
        private const int Height = 96;
        private const int PumpTimeoutSeconds = 30;

        private static LifeSeedingSession Session() => new(Width, Height);

        private static LifeNoiseParameters Fbm(int seed, float cluster = 0.6f, float warp = 4f) =>
            new(LifeSeedingMode.Fbm, seed, 0.35f, 24f, warp, cluster);

        /// <summary>Requests a candidate and pumps until it lands, or fails on timeout.</summary>
        private static void GenerateBlocking(LifeSeedingSession session)
        {
            session.RequestCandidate();

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < PumpTimeoutSeconds)
            {
                if (session.PumpGeneration())
                    return;

                Thread.Sleep(2);
            }

            Assert.Fail($"candidate generation did not finish within {PumpTimeoutSeconds}s");
        }

        /// <summary>
        /// Pumps the way the frame loop does until a condition holds. Used instead of a
        /// bare sleep because adoption only happens inside the pump.
        /// </summary>
        private static void PumpUntil(LifeSeedingSession session, Func<bool> condition, string what)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < PumpTimeoutSeconds)
            {
                session.PumpGeneration();
                if (condition())
                    return;

                Thread.Sleep(2);
            }

            Assert.Fail($"timed out after {PumpTimeoutSeconds}s waiting for {what}");
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < PumpTimeoutSeconds)
            {
                if (condition())
                    return;

                Thread.Sleep(2);
            }

            Assert.Fail($"timed out after {PumpTimeoutSeconds}s waiting for {what}");
        }

        /// <summary>
        /// A generator that blocks each seed until the test releases it, so "a task is
        /// still running" and "that old task finally finished" become observable states
        /// rather than a race against the machine's speed.
        ///
        /// <para>A seed listed at construction throws on its <b>first</b> run and generates
        /// normally afterwards. Failing every run would make "the replacement was served by
        /// its own generation" impossible to observe: the replacement would fail too, and
        /// for the right reason.</para>
        /// </summary>
        private sealed class GatedGenerator
        {
            private readonly ConcurrentDictionary<int, ManualResetEventSlim> gates = new();
            private readonly ConcurrentDictionary<int, int> runs = new();
            private readonly HashSet<int> failingSeeds;
            private int failures;

            public GatedGenerator(params int[] failingSeeds) =>
                this.failingSeeds = new HashSet<int>(failingSeeds);

            public int Failures => Volatile.Read(ref failures);

            public void Run(LifeNoiseParameters parameters, int width, int height, byte[] destination)
            {
                int run = runs.AddOrUpdate(parameters.Seed, 1, (_, count) => count + 1);

                gates.GetOrAdd(parameters.Seed, _ => new ManualResetEventSlim(false))
                    .Wait(TimeSpan.FromSeconds(PumpTimeoutSeconds));

                if (failingSeeds.Contains(parameters.Seed) && run == 1)
                {
                    Interlocked.Increment(ref failures);
                    throw new InvalidOperationException($"deliberate failure for seed {parameters.Seed}");
                }

                LifeNoiseSeeding.Generate(parameters, width, height, destination);
            }

            /// <summary>Lets a seed's generation (and every later one for that seed) finish.</summary>
            public void Release(int seed) =>
                gates.GetOrAdd(seed, _ => new ManualResetEventSlim(false)).Set();

            public bool Started(int seed) => runs.ContainsKey(seed);

            /// <summary>
            /// How many times a seed has been generated. This is how "the session computed a
            /// board of its own instead of using the abandoned one" becomes observable: two
            /// runs of the same parameters produce identical boards, so comparing content
            /// could never tell them apart.
            /// </summary>
            public int RunCount(int seed) => runs.TryGetValue(seed, out int count) ? count : 0;
        }

        [Test]
        public void FreshSession_HasNoCandidateAndNothingApplied()
        {
            LifeSeedingSession session = Session();
            Assert.IsFalse(session.HasCandidate, "a new session must not start with a candidate");
            Assert.IsFalse(session.HasAppliedParameters, "a new session must not claim anything was applied");
            Assert.IsFalse(session.IsGenerating, "a new session must not be generating");
            Assert.IsFalse(session.IsWorking, "a new session must not have a task running");
        }

        // -- parameters and the candidate are separate concerns -----------------

        [Test]
        public void SetParameters_AloneDoesNotGenerate()
        {
            // The caller debounces and then requests. Generating inside the setter is
            // what made one slider drag cost several hundred milliseconds per event --
            // and, outside a preview, what produced a candidate nothing was displaying.
            int calls = 0;
            LifeSeedingSession session = new(Width, Height, (p, w, h, d) =>
            {
                Interlocked.Increment(ref calls);
                LifeNoiseSeeding.Generate(p, w, h, d);
            });

            session.SetParameters(Fbm(seed: 5));

            Assert.AreEqual(5, session.Parameters.Seed);
            Assert.IsFalse(session.IsGenerating, "setting parameters must not start a generation");
            Assert.IsFalse(session.IsWorking, "setting parameters must not start a task");
            Assert.IsFalse(session.HasPendingRequest, "setting parameters is not a request");
            Assert.IsFalse(session.HasCandidate, "setting parameters must not put a board on screen");

            Thread.Sleep(50);
            Assert.AreEqual(0, calls, "no generation may run without an explicit request");
        }

        [Test]
        public void EditingParameters_MarksTheCandidateStale()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 11));
            GenerateBlocking(session);

            Assert.IsTrue(session.HasCandidate);
            Assert.IsFalse(session.CandidateIsStale, "a fresh candidate matches the parameters that made it");

            session.SetParameters(Fbm(seed: 12));

            Assert.IsTrue(session.CandidateIsStale,
                "the candidate on screen no longer describes the controls");
            Assert.AreEqual(11, session.CandidateParameters.Seed,
                "the candidate must remember which parameters produced it");
        }

        [Test]
        public void Apply_RefusesAStaleCandidate()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 21));
            GenerateBlocking(session);

            session.SetParameters(Fbm(seed: 22));

            Assert.IsNull(session.Apply(),
                "applying a candidate that no longer matches the controls would put a board up " +
                "that the user never saw");
        }

        [Test]
        public void PumpGeneration_AdoptsTheNewestRequest_NotAnOlderOne()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 1));
            GenerateBlocking(session);
            byte[] first = session.Candidate.ToArray();

            session.SetParameters(Fbm(seed: 999));
            GenerateBlocking(session);

            Assert.AreEqual(999, session.CandidateParameters.Seed,
                "the adopted candidate must come from the newest request");
            Assert.IsFalse(session.CandidateIsStale);
            CollectionAssert.AreNotEqual(first, session.Candidate.ToArray(),
                "changing the seed must change the candidate");
        }

        // -- overlapping requests ----------------------------------------------

        [Test]
        public void ChangingParametersWhileAGenerationRuns_DropsItAndAdoptsTheNewerOne()
        {
            // The sequence the numbering scheme alone could not handle: a request for A is
            // already running when the user edits a parameter to B. A finishing LATER must
            // not put its board on screen; only B may be displayed.
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 1));
            session.RequestCandidate();
            WaitFor(() => generator.Started(1), "the first generation to start");

            Assert.IsTrue(session.IsWorking, "the first generation should be running");
            Assert.IsTrue(session.IsGenerating, "the panel is waiting for a candidate");

            // The edit happens while A is still running.
            session.SetParameters(Fbm(seed: 2));

            // The debounce elapses and the caller asks for a replacement. A is still
            // running, so the request waits for the worker instead of queueing a second
            // task on top of it.
            Thread.Sleep(250);
            session.RequestCandidate();
            Assert.IsTrue(session.HasPendingRequest, "the replacement request must be remembered");
            Assert.IsFalse(generator.Started(2),
                "a request issued while a task is running must not start a second task");

            generator.Release(1);

            // A's result is consumed by a pump and refused; the pending request then starts.
            PumpUntil(session, () => generator.Started(2), "the replacement generation to start");

            Assert.IsFalse(session.HasCandidate,
                "the result of the superseded generation must be dropped, not displayed");
            Assert.IsNull(session.Apply(),
                "there is nothing current to apply while the replacement is still generating");

            generator.Release(2);
            PumpUntil(session, () => session.HasCandidate, "the replacement candidate");

            Assert.AreEqual(2, session.CandidateParameters.Seed,
                "the displayed candidate must come from the newest parameters");
            Assert.IsFalse(session.CandidateIsStale);
            Assert.IsFalse(session.IsGenerating, "nothing is outstanding once the new candidate is up");

            byte[] applied = session.Apply();
            Assert.IsNotNull(applied, "the newest candidate must be applicable");
            Assert.AreEqual(2, session.AppliedParameters.Seed,
                "the apply must land on the newest parameters, not the superseded ones");
        }

        [Test]
        public void SetParameters_InvalidatesAnOutstandingRequestImmediately()
        {
            // "The newest parameters win" must not depend on which task finishes last:
            // the moment the parameters move, the request that was in flight stops counting.
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 1));
            session.RequestCandidate();
            WaitFor(() => generator.Started(1), "the generation to start");
            Assert.IsTrue(session.HasPendingRequest);

            session.SetParameters(Fbm(seed: 2));

            Assert.IsFalse(session.HasPendingRequest,
                "the request belonged to the parameters that were just replaced");
        }

        // -- cancel and dispose release the interface at once --------------------

        [Test]
        public void Cancel_ReleasesTheInterfaceWhileTheAbandonedTaskIsStillRunning()
        {
            // Cancelling cannot stop a thread pool task that is already running. What it
            // CAN do is stop waiting for it -- and keep saying so on every later pump,
            // which is what the previous state machine got wrong: it re-derived
            // "generating" from "a task exists" and locked the interface again.
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 7));
            session.RequestCandidate();
            WaitFor(() => generator.Started(7), "the generation to start");

            session.Cancel();

            Assert.IsFalse(session.IsGenerating, "the interface must be released at once");
            Assert.IsTrue(session.IsWorking, "the abandoned task is still finishing -- that is the point");
            Assert.IsFalse(session.HasPendingRequest, "a cancelled request is not outstanding");

            for (int i = 0; i < 20; i++)
            {
                Assert.IsFalse(session.PumpGeneration(), "a cancelled candidate must not be adopted");
                Assert.IsFalse(session.IsGenerating, "a later pump must not lock the interface again");
                Thread.Sleep(2);
            }

            generator.Release(7);
            WaitFor(() => !session.IsWorking, "the abandoned task to finish");

            for (int i = 0; i < 10; i++)
            {
                Assert.IsFalse(session.PumpGeneration());
                Assert.IsFalse(session.IsGenerating);
                Thread.Sleep(2);
            }

            Assert.IsFalse(session.HasCandidate,
                "a late result must not resurrect a cancelled candidate");
        }

        [Test]
        public void Cancel_ThenRequestingAgain_IsNotServedByTheAbandonedTask()
        {
            // The abandoned task was building the SAME parameters. Re-requesting after a
            // cancel must be a fresh, wanted request -- and the interface has to have been
            // released for the whole time in between.
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 8));
            session.RequestCandidate();
            WaitFor(() => generator.Started(8), "the generation to start");

            session.Cancel();
            Assert.IsFalse(session.IsGenerating);

            generator.Release(8);
            WaitFor(() => !session.IsWorking, "the abandoned task to finish");

            for (int i = 0; i < 10; i++)
            {
                session.PumpGeneration();
                Assert.IsFalse(session.HasCandidate, "no result may be adopted without a request");
                Thread.Sleep(2);
            }

            session.RequestCandidate();
            PumpUntil(session, () => session.HasCandidate, "a candidate for the new request");
            Assert.AreEqual(8, session.CandidateParameters.Seed);
            Assert.IsFalse(session.IsGenerating);
        }

        [Test]
        public void Dispose_WhileAGenerationRuns_RefusesTheLateResult()
        {
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 9));
            session.RequestCandidate();
            WaitFor(() => generator.Started(9), "the generation to start");

            session.Dispose();

            Assert.IsTrue(session.IsDisposed);
            Assert.IsFalse(session.IsGenerating, "a disposed session must not claim to be waiting");

            generator.Release(9);
            WaitFor(() => !session.IsWorking, "the task to finish");

            for (int i = 0; i < 10; i++)
            {
                Assert.IsFalse(session.PumpGeneration(), "a disposed session must not adopt anything");
                Thread.Sleep(2);
            }

            Assert.IsFalse(session.HasCandidate, "the late result of a disposed session must be refused");

            session.RequestCandidate();
            Assert.IsFalse(session.IsWorking, "a disposed session must not start new work");
            Assert.IsFalse(session.IsGenerating);
        }

        [Test]
        public void GeneratorFailure_ReleasesTheInterfaceInsteadOfWaitingForever()
        {
            // A generator that throws must not leave the panel promising a candidate that
            // will never arrive: that would disable the clock for good.
            int calls = 0;
            LifeSeedingSession session = new(Width, Height, (p, w, h, d) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("deliberate test failure");

                LifeNoiseSeeding.Generate(p, w, h, d);
            });

            session.SetParameters(Fbm(seed: 1));
            session.RequestCandidate();
            PumpUntil(session, () => session.FailureMessage != null, "the failure to be reported");

            Assert.IsFalse(session.IsGenerating, "a failed generation must not keep the interface locked");
            Assert.IsFalse(session.IsWorking);
            Assert.IsFalse(session.HasPendingRequest, "nothing is outstanding after a failure");

            // And the session recovers: a later request runs a fresh generation.
            session.RequestCandidate();
            PumpUntil(session, () => session.HasCandidate, "a candidate from the retry");
            Assert.IsNull(session.FailureMessage, "a successful retry must clear the old failure");
        }

        [Test]
        public void ASupersededTaskFailing_MustNotClearTheReplacementRequest()
        {
            // The failure path has to obey the same rule as the success path: a task whose
            // parameters have been replaced is already irrelevant. Clearing the request on
            // its way out cancelled the replacement the user had asked for -- the panel
            // then sat with a preview open, no candidate and nothing being computed.
            var generator = new GatedGenerator(1);
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 1));
            session.RequestCandidate();
            WaitFor(() => generator.Started(1), "the first generation to start");

            // The user moves on while the first task is still running, and the debounce
            // elapses: the replacement is requested but has to wait for the worker.
            session.SetParameters(Fbm(seed: 2));
            Thread.Sleep(250);
            session.RequestCandidate();
            Assert.IsTrue(session.HasPendingRequest, "the replacement request must be remembered");
            Assert.IsFalse(generator.Started(2), "the worker is still busy with the superseded task");

            generator.Release(1);

            // The superseded task now throws. Its request no longer exists, so its failure
            // must be dropped the way its result would have been.
            PumpUntil(session, () => generator.Started(2), "the replacement generation to start");

            Assert.AreEqual(1, generator.Failures, "the first generation was supposed to fail");
            Assert.IsTrue(session.HasPendingRequest, "the replacement request must survive the old failure");
            Assert.IsNull(session.FailureMessage,
                "the current request has not failed, so the panel must not report a failure");

            generator.Release(2);
            PumpUntil(session, () => session.HasCandidate, "the replacement candidate");

            Assert.AreEqual(2, session.CandidateParameters.Seed,
                "the replacement must be the one that lands");
            Assert.IsFalse(session.IsGenerating);
            Assert.IsFalse(session.CandidateIsStale);
        }

        // -- cancel does not change the parameters, so content cannot settle these ------

        [Test]
        public void Cancel_ThenTheAbandonedTaskFails_ReportsNothing()
        {
            // Cancelling leaves the parameters exactly as they were, so a content comparison
            // would let the abandoned task's failure straight through: the panel would
            // announce "生成失败" for a request the user had already withdrawn. Only the
            // request identity can refuse it.
            var generator = new GatedGenerator(4);
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 4));
            session.RequestCandidate();
            WaitFor(() => generator.Started(4), "the generation to start");

            session.Cancel();
            Assert.IsTrue(session.IsWorking, "the abandoned task must still be running for this to mean anything");

            generator.Release(4);
            WaitFor(() => !session.IsWorking, "the abandoned task to finish");

            for (int i = 0; i < 10; i++)
            {
                Assert.IsFalse(session.PumpGeneration(), "a withdrawn request must not adopt anything");
                Assert.IsFalse(session.IsGenerating, "a withdrawn request is not waiting for anything");
                Assert.IsNull(session.FailureMessage, "a withdrawn request cannot fail");
                Thread.Sleep(2);
            }

            Assert.IsFalse(session.HasCandidate);
        }

        [Test]
        public void Cancel_ThenAnIdenticalRequest_IsNotCancelledByTheAbandonedFailure()
        {
            // The user cancels and immediately asks for the same parameters again. Both
            // requests carry identical content, so this is the case that only identity can
            // get right: the abandoned task's failure belongs to the request that is over.
            var generator = new GatedGenerator(6);
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 6));
            session.RequestCandidate();
            WaitFor(() => generator.Started(6), "the first generation to start");

            session.Cancel();

            // The new request has to be issued BEFORE the abandoned task is let go:
            // otherwise this test would be about the state after it finished, not about
            // what its failure is allowed to touch.
            Assert.IsTrue(session.IsWorking, "the abandoned task must still be running when the new request goes out");
            session.RequestCandidate();
            Assert.IsTrue(session.HasPendingRequest, "the identical request must be outstanding");
            Assert.AreEqual(1, generator.RunCount(6), "a busy worker must not be handed a second task");

            generator.Release(6);

            // The abandoned task fails and the replacement is started in its place. No pump
            // happens here on purpose: a request can only be consumed by the main-thread
            // pump, so "still outstanding" is a stable state to assert.
            WaitFor(() => generator.RunCount(6) == 2, "the new request to start its own board");

            Assert.IsTrue(session.HasPendingRequest, "the new request must survive the abandoned failure");
            Assert.IsNull(session.FailureMessage, "the abandoned failure belongs to a request that is over");

            PumpUntil(session, () => session.HasCandidate, "the new request's candidate");
            Assert.AreEqual(6, session.CandidateParameters.Seed);
            Assert.IsFalse(session.IsGenerating);
        }

        [Test]
        public void Cancel_ThenAnIdenticalRequest_DoesNotAdoptTheAbandonedSuccess()
        {
            // The success side of the same rule. The abandoned task produces exactly the
            // board the new request is asking for, and it still may not be used: the new
            // request runs its own generation. Two runs of identical parameters produce
            // identical boards, so the run count is what makes this observable.
            var generator = new GatedGenerator();
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 12));
            session.RequestCandidate();
            WaitFor(() => generator.Started(12), "the first generation to start");

            session.Cancel();
            Assert.IsTrue(session.IsWorking, "the abandoned task must still be running when the new request goes out");
            session.RequestCandidate();
            Assert.AreEqual(1, generator.RunCount(12));

            generator.Release(12);

            // Let the abandoned task finish. Nothing can be adopted without a pump, so the
            // state is stable here and the next single pump is the one that decides.
            WaitFor(() => !session.IsWorking, "the abandoned task to finish");
            Assert.IsFalse(session.HasCandidate, "the abandoned result must not be adopted on its own");

            session.PumpGeneration();

            // That pump saw a finished result which belongs to a request that is over: it had
            // to refuse it and ask for a fresh generation instead.
            Assert.IsFalse(session.HasCandidate, "the abandoned result must not be adopted by the new request");

            WaitFor(() => generator.RunCount(12) == 2, "the new request to start its own board");
            Assert.IsFalse(session.HasCandidate, "the abandoned board must not be displayed");

            PumpUntil(session, () => session.HasCandidate, "the new request's candidate");
            Assert.AreEqual(12, session.CandidateParameters.Seed);
            Assert.IsFalse(session.IsGenerating);
            Assert.IsFalse(session.CandidateIsStale);
        }

        [Test]
        public void TheCurrentTaskFailing_StillReportsAndReleases()
        {
            // The other side of the same branch: when the task that fails IS the current
            // request, the session has to stop waiting and say so. Otherwise the fix above
            // could have been "never clear anything", which wedges the panel instead.
            var generator = new GatedGenerator(5);
            LifeSeedingSession session = new(Width, Height, generator.Run);

            session.SetParameters(Fbm(seed: 5));
            session.RequestCandidate();
            WaitFor(() => generator.Started(5), "the generation to start");
            generator.Release(5);

            PumpUntil(session, () => session.FailureMessage != null, "the failure to be reported");

            Assert.IsTrue(session.FailureMessage.Contains("seed 5"),
                $"the reported failure should name what failed, got '{session.FailureMessage}'");
            Assert.IsFalse(session.IsGenerating, "the interface must be released after a real failure");
            Assert.IsFalse(session.HasPendingRequest);
            Assert.IsFalse(session.IsWorking);
        }

        // -- reporting ----------------------------------------------------------

        [Test]
        public void GenerateCandidate_ReportsMeasuredDensity_NotTheRequestedOne()
        {
            LifeSeedingSession session = Session();
            var parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 3, 0.15f, 24f, 4f, 1f);
            session.SetParameters(parameters);
            GenerateBlocking(session);

            Assert.AreEqual(session.CandidateAliveCount, LifeNoiseSeeding.CountAlive(session.Candidate),
                "the reported alive count must match the candidate");
            Assert.That(session.CandidateDensity,
                Is.EqualTo((float)session.CandidateAliveCount / (Width * Height)).Within(1e-6f));
            Assert.AreNotEqual(parameters.Density, session.CandidateDensity,
                "clamping should move the realised density off the base probability");
        }

        [Test]
        public void GenerateCandidate_TimesItself()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 1));
            GenerateBlocking(session);

            Assert.Greater(session.LastGenerationMilliseconds, 0.0,
                "generation cost must be recorded, not assumed to be free");
        }

        // -- apply and cancel ---------------------------------------------------

        [Test]
        public void Cancel_DropsTheCandidate()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 3));
            GenerateBlocking(session);

            session.Cancel();

            Assert.IsFalse(session.HasCandidate, "cancelling must drop the candidate");
            Assert.IsFalse(session.HasAppliedParameters, "cancelling must not count as applying");
        }

        [Test]
        public void Apply_ReturnsACopy_NotTheInternalBuffer()
        {
            // The caller stores the returned array as the experiment's initial state.
            // If this handed back the session's own buffer, the next generation would
            // silently rewrite the board that "reset" restores.
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 11));
            GenerateBlocking(session);

            byte[] applied = session.Apply();
            byte[] appliedSnapshot = (byte[])applied.Clone();

            session.SetParameters(Fbm(seed: 12));
            GenerateBlocking(session);

            CollectionAssert.AreEqual(appliedSnapshot, applied,
                "the applied board must not be aliased to the session's working buffer");
        }

        [Test]
        public void Apply_ConfirmsTheParametersAndClearsTheCandidate()
        {
            LifeSeedingSession session = Session();
            LifeNoiseParameters parameters = Fbm(seed: 21);
            session.SetParameters(parameters);
            GenerateBlocking(session);

            byte[] applied = session.Apply();

            Assert.IsNotNull(applied);
            Assert.AreEqual(Width * Height, applied.Length);
            Assert.IsTrue(session.HasAppliedParameters);
            Assert.AreEqual(parameters.Seed, session.AppliedParameters.Seed);
            Assert.IsFalse(session.HasCandidate, "applying consumes the candidate");
        }

        [Test]
        public void Apply_WithNoCandidate_ReturnsNull()
        {
            LifeSeedingSession session = Session();
            Assert.IsNull(session.Apply(), "there is nothing to apply without a candidate");
        }

        [Test]
        public void Reseed_ChangesOnlyTheSeed()
        {
            LifeSeedingSession session = Session();
            LifeNoiseParameters before = Fbm(seed: 1, cluster: 0.42f, warp: 7f);
            session.SetParameters(before);

            session.Reseed(1234);
            LifeNoiseParameters after = session.Parameters;

            Assert.AreEqual(1234, after.Seed);
            Assert.AreEqual(before.Mode, after.Mode);
            Assert.AreEqual(before.Density, after.Density);
            Assert.AreEqual(before.Scale, after.Scale);
            Assert.AreEqual(before.WarpStrength, after.WarpStrength);
            Assert.AreEqual(before.ClusterStrength, after.ClusterStrength);
        }

        [Test]
        public void ForgetApplied_ClearsTheClaimWithoutTouchingTheCandidate()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 4));
            GenerateBlocking(session);
            session.Apply();
            GenerateBlocking(session);

            session.ForgetApplied();

            Assert.IsFalse(session.HasAppliedParameters, "the parameters no longer describe the loaded board");
            Assert.IsTrue(session.HasCandidate, "forgetting the applied board must not disturb a live candidate");
        }

        [Test]
        public void Constructor_RejectsBadSizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LifeSeedingSession(0, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LifeSeedingSession(8, -1));
        }
    }
}
