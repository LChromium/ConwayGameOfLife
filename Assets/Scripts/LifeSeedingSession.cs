using System;
using System.Diagnostics;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Stage-B seeding state: the parameters being edited, the candidate board on
    /// screen, and the parameters that were last confirmed.
    ///
    /// Pure C# -- no UnityEngine -- so EditMode tests drive exactly the same object
    /// the terminal does.
    ///
    /// <para><b>The rule this class exists to enforce.</b> Editing parameters may only
    /// change the candidate. Nothing here touches the live board: the confirmed board
    /// lives in <see cref="LifeTerminalController"/>, and only <see cref="Apply"/>
    /// hands it a replacement. <see cref="Cancel"/> throws the candidate away and the
    /// board is exactly where it was, because it was never moved.</para>
    ///
    /// <para><b>Changing a parameter while a candidate is showing regenerates it</b>,
    /// which is what "adjusting parameters only changes the candidate preview" means
    /// in practice. Changing a parameter with no candidate showing does nothing but
    /// store the value; a candidate appears only when one is asked for.</para>
    /// </summary>
    public sealed class LifeSeedingSession
    {
        private readonly byte[] candidate;
        private readonly Stopwatch stopwatch = new();

        public LifeSeedingSession(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Board dimensions must be positive.");

            Width = width;
            Height = height;
            candidate = new byte[width * height];
            Parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 1, 0.32f, 48f, 6f, 0.6f);
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>The parameters currently being edited.</summary>
        public LifeNoiseParameters Parameters { get; private set; }

        /// <summary>True once a candidate has been applied at least once this session.</summary>
        public bool HasAppliedParameters { get; private set; }

        /// <summary>The parameters behind the board that is actually loaded.</summary>
        public LifeNoiseParameters AppliedParameters { get; private set; }

        public bool HasCandidate { get; private set; }

        /// <summary>The candidate cells, row-major, values 0/1. Only valid while <see cref="HasCandidate"/>.</summary>
        public ReadOnlySpan<byte> Candidate => candidate;

        /// <summary>Density actually realised by the candidate. The base density is not a population promise.</summary>
        public float CandidateDensity { get; private set; }

        public int CandidateAliveCount { get; private set; }

        /// <summary>
        /// Cost of the last candidate generation. Reported because generating a
        /// 1024x1024 board on the CPU is not free and the number belongs in the open.
        /// </summary>
        public double LastGenerationMilliseconds { get; private set; }

        /// <summary>
        /// Replaces the parameters. If a candidate is on screen it is regenerated, so
        /// the preview always matches the controls.
        /// </summary>
        public void SetParameters(in LifeNoiseParameters parameters)
        {
            Parameters = parameters;

            if (HasCandidate)
                GenerateCandidate();
        }

        /// <summary>
        /// Moves to a different seed. Kept as its own call because changing the seed is
        /// meant to be a deliberate action, not something a stray drag can do.
        /// </summary>
        public void Reseed(int seed)
        {
            SetParameters(new LifeNoiseParameters(
                Parameters.Mode, seed, Parameters.Density, Parameters.Scale,
                Parameters.WarpStrength, Parameters.ClusterStrength));
        }

        /// <summary>Generates (or regenerates) the candidate from the current parameters.</summary>
        public bool GenerateCandidate()
        {
            stopwatch.Restart();
            LifeNoiseSeeding.Generate(Parameters, Width, Height, candidate);
            stopwatch.Stop();

            CandidateAliveCount = LifeNoiseSeeding.CountAlive(candidate);
            CandidateDensity = (float)CandidateAliveCount / candidate.Length;
            LastGenerationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            HasCandidate = true;
            return true;
        }

        /// <summary>Drops the candidate. The live board is untouched and stays as it was.</summary>
        public void Cancel()
        {
            HasCandidate = false;
        }

        /// <summary>
        /// Confirms the candidate. Returns a copy, not the internal buffer: the caller
        /// stores this as the experiment's initial state and the session will overwrite
        /// its own buffer on the next generation.
        /// </summary>
        public byte[] Apply()
        {
            if (!HasCandidate)
                return null;

            var confirmed = (byte[])candidate.Clone();

            AppliedParameters = Parameters;
            HasAppliedParameters = true;
            HasCandidate = false;

            return confirmed;
        }

        /// <summary>
        /// The confirmed board came from somewhere else (a specimen, or a manual
        /// clear), so the seeding parameters no longer describe what is loaded.
        /// </summary>
        public void ForgetApplied()
        {
            HasAppliedParameters = false;
        }
    }
}
