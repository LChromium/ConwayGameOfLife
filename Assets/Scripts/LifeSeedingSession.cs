using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Stage-B seeding state: the parameters being edited, the candidate board, and
    /// the parameters that were last confirmed.
    ///
    /// Pure C# -- no UnityEngine -- so EditMode tests drive exactly the same object
    /// the terminal does.
    ///
    /// <para><b>The rule this class exists to enforce.</b> Editing parameters may only
    /// change the candidate. Nothing here touches the live board: the confirmed board
    /// lives in <see cref="LifeTerminalController"/>, and only <see cref="Apply"/>
    /// hands it a replacement. Cancelling throws the candidate away and the board is
    /// exactly where it was, because it was never moved.</para>
    ///
    /// <para><b>Generation runs off the main thread.</b> A 1024x1024 fBm board takes
    /// roughly half a second; doing that inside a slider callback blocks the frame.
    /// <see cref="RequestCandidate"/> hands an immutable parameter snapshot to a
    /// background task and the main thread collects the result in
    /// <see cref="PumpGeneration"/>. Debouncing reduces how often that happens; it
    /// does not make the work cheaper, which is why the work moved off the main
    /// thread rather than merely being slowed down.</para>
    ///
    /// <para><b>Only the newest request is ever adopted.</b> Requests are numbered.
    /// A result whose number is no longer current is dropped -- whether the user
    /// changed a parameter again, cancelled, or moved to another specimen. At most
    /// one task runs at a time; if the parameters moved on while it ran, the next
    /// one starts as soon as it finishes, so a long drag coalesces instead of
    /// queueing one generation per event.</para>
    ///
    /// <para><b>Two buffers, swapped.</b> The worker writes into one while the main
    /// thread reads the other, and adoption swaps them. Nothing allocates a board
    /// per preview.</para>
    /// </summary>
    public sealed class LifeSeedingSession : IDisposable
    {
        private readonly object gate = new();

        private byte[] candidate;
        private byte[] worker;

        private int requestId;       // the newest request anybody still wants
        private int inFlightId = -1;
        private int finishedId = -1;
        private int satisfiedId = -1;  // the request whose result is currently on screen
        private bool wantCandidate;
        private bool finishedReady;
        private int finishedAlive;
        private double finishedMilliseconds;
        private LifeNoiseParameters finishedParameters;
        private bool disposed;

        public LifeSeedingSession(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Board dimensions must be positive.");

            Width = width;
            Height = height;
            candidate = new byte[width * height];
            worker = new byte[width * height];
            Parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 1, 0.32f, 48f, 6f, 0.6f);
            CandidateParameters = Parameters;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>The parameters currently being edited.</summary>
        public LifeNoiseParameters Parameters { get; private set; }

        public bool HasAppliedParameters { get; private set; }

        /// <summary>The parameters behind the board that is actually loaded.</summary>
        public LifeNoiseParameters AppliedParameters { get; private set; }

        public bool HasCandidate { get; private set; }

        /// <summary>The parameters the candidate on screen was generated from.</summary>
        public LifeNoiseParameters CandidateParameters { get; private set; }

        /// <summary>
        /// True while the candidate on screen came from parameters that have since
        /// changed. The UI says "pending" rather than pretending the picture matches
        /// the controls.
        /// </summary>
        public bool CandidateIsStale => HasCandidate && !CandidateParameters.Equals(Parameters);

        /// <summary>The candidate cells, row-major, values 0/1. Only valid while <see cref="HasCandidate"/>.</summary>
        public ReadOnlySpan<byte> Candidate => candidate;

        /// <summary>Density actually realised by the candidate. The base density is not a population promise.</summary>
        public float CandidateDensity { get; private set; }

        public int CandidateAliveCount { get; private set; }

        /// <summary>Cost of the last adopted generation. Half a second is not free, so it is reported.</summary>
        public double LastGenerationMilliseconds { get; private set; }

        /// <summary>True while a result for the current parameters is still outstanding.</summary>
        public bool IsGenerating { get; private set; }

        /// <summary>
        /// Replaces the parameters. Generation is NOT started here: the caller debounces
        /// and then calls <see cref="RequestCandidate"/>, so a slider drag does not
        /// queue one generation per event.
        /// </summary>
        public void SetParameters(in LifeNoiseParameters parameters) => Parameters = parameters;

        /// <summary>Moves to a different seed. Kept separate because changing the seed is a deliberate act.</summary>
        public void Reseed(int seed) => SetParameters(new LifeNoiseParameters(
            Parameters.Mode, seed, Parameters.Density, Parameters.Scale,
            Parameters.WarpStrength, Parameters.ClusterStrength));

        /// <summary>Asks for a candidate built from the current parameters.</summary>
        public void RequestCandidate()
        {
            lock (gate)
            {
                if (disposed)
                    return;

                requestId++;
                wantCandidate = true;
            }

            StartNextIfIdle();
        }

        /// <summary>
        /// Main-thread pump. Adopts a finished candidate when it is still the newest
        /// request, drops it when it is not, and starts the next one if the parameters
        /// moved on. Returns true when a new candidate was adopted.
        /// </summary>
        public bool PumpGeneration()
        {
            bool adopted = false;

            lock (gate)
            {
                if (finishedReady)
                {
                    if (finishedId == requestId && wantCandidate)
                    {
                        // Swap rather than copy. The buffer the main thread was reading
                        // becomes the worker's next target, so a steady stream of
                        // previews allocates nothing.
                        (candidate, worker) = (worker, candidate);
                        CandidateAliveCount = finishedAlive;
                        CandidateDensity = (float)finishedAlive / candidate.Length;
                        LastGenerationMilliseconds = finishedMilliseconds;
                        CandidateParameters = finishedParameters;
                        HasCandidate = true;
                        satisfiedId = finishedId;
                        adopted = true;
                    }

                    // Either way it is consumed: a stale result must not be adopted
                    // later just because the request numbers happen to line up again.
                    finishedReady = false;
                }

                IsGenerating = inFlightId >= 0 || (wantCandidate && satisfiedId != requestId);
            }

            StartNextIfIdle();
            return adopted;
        }

        /// <summary>Caller must hold <see cref="gate"/>.</summary>
        private bool HasCurrentResult() => finishedReady && finishedId == requestId && wantCandidate;

        private void StartNextIfIdle()
        {
            int id;
            LifeNoiseParameters snapshot;
            byte[] buffer;

            lock (gate)
            {
                // "wantCandidate" is what stops the pipeline: without it, cancelling
                // would immediately start a fresh generation for the same parameters
                // and the candidate the user just dismissed would reappear.
                if (disposed || !wantCandidate || inFlightId >= 0 || satisfiedId == requestId)
                    return;

                inFlightId = requestId;
                id = inFlightId;
                snapshot = Parameters;
                buffer = worker;
                IsGenerating = true;
            }

            Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                LifeNoiseSeeding.Generate(snapshot, Width, Height, buffer);
                int alive = LifeNoiseSeeding.CountAlive(buffer);
                watch.Stop();

                lock (gate)
                {
                    if (disposed)
                        return;

                    // The buffer may already have been superseded; the id decides,
                    // not the order in which tasks happen to finish.
                    if (id >= finishedId || !finishedReady)
                    {
                        finishedId = id;
                        finishedAlive = alive;
                        finishedMilliseconds = watch.Elapsed.TotalMilliseconds;
                        finishedParameters = snapshot;
                        finishedReady = true;
                    }

                    inFlightId = -1;
                }
            });
        }

        /// <summary>
        /// Drops the candidate and stops the pipeline. Anything already in flight is
        /// invalidated, and no replacement is started -- otherwise the candidate the
        /// user just dismissed would be regenerated from the same parameters and
        /// reappear a moment later.
        /// </summary>
        public void Cancel()
        {
            lock (gate)
            {
                HasCandidate = false;
                wantCandidate = false;
                requestId++;
                satisfiedId = requestId;   // the current request is settled by not generating
                finishedReady = false;
                finishedId = -1;

                // A task may still be running, but nothing it produces is wanted, so
                // the UI must not keep saying "generating".
                IsGenerating = false;
            }
        }

        /// <summary>
        /// Confirms the candidate. Returns a copy, not the internal buffer: the caller
        /// stores this as the experiment's initial state and the session will overwrite
        /// its own buffer on the next generation. Returns null when there is nothing
        /// current to confirm -- including a candidate whose parameters have moved on.
        /// </summary>
        public byte[] Apply()
        {
            lock (gate)
            {
                if (!HasCandidate || CandidateIsStale)
                    return null;

                var confirmed = (byte[])candidate.Clone();

                AppliedParameters = CandidateParameters;
                HasAppliedParameters = true;
                HasCandidate = false;

                // The candidate has been consumed; do not build another one behind
                // the user's back.
                wantCandidate = false;
                satisfiedId = requestId;
                IsGenerating = false;

                return confirmed;
            }
        }

        /// <summary>
        /// The confirmed board came from somewhere else (a specimen, or a manual
        /// clear), so the seeding parameters no longer describe what is loaded.
        /// </summary>
        public void ForgetApplied() => HasAppliedParameters = false;

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                wantCandidate = false;
                requestId++;
                satisfiedId = requestId;
                finishedReady = false;
                HasCandidate = false;
                IsGenerating = false;
            }
        }
    }
}
