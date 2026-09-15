using System;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage-B session semantics. These are the rules that keep "adjust, then decide"
    /// honest: editing parameters may only move the candidate, and cancelling must
    /// leave nothing behind.
    /// </summary>
    public sealed class LifeSeedingSessionTests
    {
        private const int Width = 128;
        private const int Height = 96;

        private static LifeSeedingSession Session() => new(Width, Height);

        private static LifeNoiseParameters Fbm(int seed, float cluster = 0.6f, float warp = 4f) =>
            new(LifeSeedingMode.Fbm, seed, 0.35f, 24f, warp, cluster);

        [Test]
        public void FreshSession_HasNoCandidateAndNothingApplied()
        {
            LifeSeedingSession session = Session();
            Assert.IsFalse(session.HasCandidate, "a new session must not start with a candidate");
            Assert.IsFalse(session.HasAppliedParameters, "a new session must not claim anything was applied");
        }

        [Test]
        public void SetParameters_WithoutACandidate_OnlyStoresThem()
        {
            LifeSeedingSession session = Session();
            session.SetParameters(Fbm(seed: 5));

            Assert.AreEqual(5, session.Parameters.Seed);
            Assert.IsFalse(session.HasCandidate,
                "changing parameters must not silently put a board on screen");
        }

        [Test]
        public void SetParameters_WithACandidate_RegeneratesIt()
        {
            LifeSeedingSession session = Session();
            session.GenerateCandidate();
            byte[] before = session.Candidate.ToArray();

            session.SetParameters(Fbm(seed: 99));
            byte[] after = session.Candidate.ToArray();

            Assert.IsTrue(session.HasCandidate, "the candidate should still be showing");
            CollectionAssert.AreNotEqual(before, after,
                "editing a parameter while a candidate is showing must update the preview");
        }

        [Test]
        public void GenerateCandidate_ReportsMeasuredDensity_NotTheRequestedOne()
        {
            LifeSeedingSession session = Session();
            var parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 3, 0.15f, 24f, 4f, 1f);
            session.SetParameters(parameters);
            session.GenerateCandidate();

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
            session.GenerateCandidate();

            Assert.Greater(session.LastGenerationMilliseconds, 0.0,
                "generation cost must be recorded, not assumed to be free");
        }

        [Test]
        public void Cancel_DropsTheCandidate()
        {
            LifeSeedingSession session = Session();
            session.GenerateCandidate();
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
            session.GenerateCandidate();

            byte[] applied = session.Apply();
            byte[] appliedSnapshot = (byte[])applied.Clone();

            session.SetParameters(Fbm(seed: 12));
            session.GenerateCandidate();

            CollectionAssert.AreEqual(appliedSnapshot, applied,
                "the applied board must not be aliased to the session's working buffer");
        }

        [Test]
        public void Apply_ConfirmsTheParametersAndClearsTheCandidate()
        {
            LifeSeedingSession session = Session();
            LifeNoiseParameters parameters = Fbm(seed: 21);
            session.SetParameters(parameters);
            session.GenerateCandidate();

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
            session.GenerateCandidate();
            session.Apply();
            session.GenerateCandidate();

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
