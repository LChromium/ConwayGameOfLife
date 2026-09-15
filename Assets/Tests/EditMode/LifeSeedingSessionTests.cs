using System;
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
    /// than hanging the suite.
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

        [Test]
        public void FreshSession_HasNoCandidateAndNothingApplied()
        {
            LifeSeedingSession session = Session();
            Assert.IsFalse(session.HasCandidate, "a new session must not start with a candidate");
            Assert.IsFalse(session.HasAppliedParameters, "a new session must not claim anything was applied");
            Assert.IsFalse(session.IsGenerating, "a new session must not be generating");
        }

        // -- parameters and the candidate are separate concerns -----------------

        [Test]
        public void SetParameters_AloneDoesNotGenerate()
        {
            // The caller debounces and then requests. Generating inside the setter is
            // what made one slider drag cost several hundred milliseconds per event.
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 5));

            Assert.AreEqual(5, session.Parameters.Seed);
            Assert.IsFalse(session.IsGenerating, "setting parameters must not start a generation");
            Assert.IsFalse(session.HasCandidate, "setting parameters must not put a board on screen");
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

        [Test]
        public void Cancel_DiscardsAResultThatArrivesAfterwards()
        {
            // The whole point of numbering requests: a task that was already running
            // when the user cancelled must not reappear.
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 77));
            session.RequestCandidate();

            session.Cancel();

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < PumpTimeoutSeconds)
            {
                session.PumpGeneration();
                if (!session.IsGenerating)
                    break;
                Thread.Sleep(2);
            }

            Assert.IsFalse(session.HasCandidate,
                "a late result must not resurrect a cancelled candidate");
        }

        [Test]
        public void Dispose_DiscardsLateResults()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 5));
            session.RequestCandidate();

            session.Dispose();

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 5)
            {
                session.PumpGeneration();
                Thread.Sleep(2);
            }

            Assert.IsFalse(session.HasCandidate, "a disposed session must never adopt a result");
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
