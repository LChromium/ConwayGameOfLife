using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Fills a board from a parameter set. Injected so tests can decide exactly when a
    /// generation finishes; production uses <see cref="LifeNoiseSeeding.Generate"/>.
    /// </summary>
    public delegate void LifeBoardGenerator(
        LifeNoiseParameters parameters, int width, int height, byte[] destination);

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
    /// <see cref="PumpGeneration"/>.</para>
    ///
    /// <para><b>Parameters decide what may be adopted, not arrival order.</b> A result is
    /// taken only when the parameters it was built from still equal the parameters being
    /// edited. <see cref="SetParameters"/> therefore invalidates an outstanding request
    /// <i>immediately</i>: a task that was already running when a slider moved can finish,
    /// but what it produces is dropped instead of being uploaded a moment later. Debouncing
    /// only decides when the replacement computation is asked for; it does not decide
    /// whether the old one still counts.</para>
    ///
    /// <para><b>Two different questions, two different answers.</b>
    /// <see cref="IsGenerating"/> means "the panel is waiting for a candidate that matches
    /// the controls", and it is what the interface may use to lock the clock.
    /// <see cref="IsWorking"/> means "a background task is still running", which is true
    /// for a while after a cancel as well -- and must never lock anything.
    /// <see cref="HasPendingRequest"/> means "a request has been issued whose result has not
    /// been adopted". Keeping them apart is what stops a cancelled generation from
    /// re-disabling the controls on the next pump.</para>
    ///
    /// <para><b>Two buffers, swapped.</b> The worker writes into one while the main
    /// thread reads the other, and adoption swaps them. Nothing allocates a board
    /// per preview.</para>
    /// </summary>
    public sealed class LifeSeedingSession : IDisposable
    {
        private readonly object gate = new();
        private readonly LifeBoardGenerator generator;

        private byte[] candidate;
        private byte[] worker;

        // The pipeline. "wantCandidate" is the user's intent, "requestPending" is an
        // issued-and-unanswered request, "working" is a task actually running.
        private bool wantCandidate;
        private bool requestPending;
        private bool working;
        private bool finishedReady;
        private int finishedAlive;
        private double finishedMilliseconds;
        private LifeNoiseParameters finishedParameters;

        // The candidate on screen.
        private bool hasCandidate;
        private LifeNoiseParameters candidateParameters;
        private float candidateDensity;
        private int candidateAliveCount;
        private double lastGenerationMilliseconds;

        private bool hasAppliedParameters;
        private LifeNoiseParameters appliedParameters;
        private string failure;
        private bool disposed;

        public LifeSeedingSession(int width, int height, LifeBoardGenerator generator = null)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Board dimensions must be positive.");

            Width = width;
            Height = height;
            this.generator = generator ?? DefaultGenerator;
            candidate = new byte[width * height];
            worker = new byte[width * height];
            parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 1, 0.32f, 48f, 6f, 0.6f);
            candidateParameters = parameters;
        }

        private static void DefaultGenerator(
            LifeNoiseParameters parameters, int width, int height, byte[] destination) =>
            LifeNoiseSeeding.Generate(parameters, width, height, destination);

        public int Width { get; }
        public int Height { get; }

        private LifeNoiseParameters parameters;

        /// <summary>The parameters currently being edited.</summary>
        public LifeNoiseParameters Parameters
        {
            get { lock (gate) { return parameters; } }
        }

        public bool HasAppliedParameters
        {
            get { lock (gate) { return hasAppliedParameters; } }
        }

        /// <summary>The parameters behind the board that is actually loaded.</summary>
        public LifeNoiseParameters AppliedParameters
        {
            get { lock (gate) { return appliedParameters; } }
        }

        public bool HasCandidate
        {
            get { lock (gate) { return hasCandidate; } }
        }

        /// <summary>The parameters the candidate on screen was generated from.</summary>
        public LifeNoiseParameters CandidateParameters
        {
            get { lock (gate) { return candidateParameters; } }
        }

        /// <summary>
        /// True while the candidate on screen came from parameters that have since
        /// changed. The UI says "pending" rather than pretending the picture matches
        /// the controls.
        /// </summary>
        public bool CandidateIsStale
        {
            get { lock (gate) { return IsStaleLocked; } }
        }

        /// <summary>The candidate cells, row-major, values 0/1. Only valid while <see cref="HasCandidate"/>.</summary>
        public ReadOnlySpan<byte> Candidate
        {
            get { lock (gate) { return candidate; } }
        }

        /// <summary>Density actually realised by the candidate. The base density is not a population promise.</summary>
        public float CandidateDensity
        {
            get { lock (gate) { return candidateDensity; } }
        }

        public int CandidateAliveCount
        {
            get { lock (gate) { return candidateAliveCount; } }
        }

        /// <summary>Cost of the last adopted generation. Half a second is not free, so it is reported.</summary>
        public double LastGenerationMilliseconds
        {
            get { lock (gate) { return lastGenerationMilliseconds; } }
        }

        /// <summary>
        /// The panel is waiting for a candidate that describes the current parameters.
        /// This is the only state that may disable the clock, single-step and painting.
        /// </summary>
        public bool IsGenerating
        {
            get { lock (gate) { return IsGeneratingLocked; } }
        }

        /// <summary>
        /// A background task is running. True for a while after a cancel too -- an
        /// abandoned task still has to finish -- so it must never lock the interface.
        /// It exists so "the old task is winding down" and "the user is still waiting"
        /// can be told apart.
        /// </summary>
        public bool IsWorking
        {
            get { lock (gate) { return working; } }
        }

        /// <summary>True from <see cref="RequestCandidate"/> until its result has been adopted.</summary>
        public bool HasPendingRequest
        {
            get { lock (gate) { return requestPending; } }
        }

        /// <summary>
        /// Why the last generation failed, or null. A generator that throws must not leave
        /// the panel promising a candidate that will never arrive.
        /// </summary>
        public string FailureMessage
        {
            get { lock (gate) { return failure; } }
        }

        public bool IsDisposed
        {
            get { lock (gate) { return disposed; } }
        }

        private bool IsStaleLocked => hasCandidate && !candidateParameters.Equals(parameters);

        private bool IsGeneratingLocked => wantCandidate && !(hasCandidate && !IsStaleLocked);

        /// <summary>
        /// Replaces the parameters and invalidates anything already computed or in flight
        /// for the old ones. Generation is NOT started here: the caller debounces and then
        /// calls <see cref="RequestCandidate"/>, so a slider drag does not queue one
        /// generation per event.
        /// </summary>
        public void SetParameters(in LifeNoiseParameters parameters)
        {
            lock (gate)
            {
                if (this.parameters.Equals(parameters))
                    return;

                this.parameters = parameters;

                // Immediate invalidation. The task may still be running; whatever it
                // produces describes parameters the controls have already left behind,
                // so it is refused on arrival rather than uploaded a frame later.
                requestPending = false;
                finishedReady = false;
            }
        }

        /// <summary>Moves to a different seed. Kept separate because changing the seed is a deliberate act.</summary>
        public void Reseed(int seed)
        {
            LifeNoiseParameters current;
            lock (gate)
            {
                current = parameters;
            }

            SetParameters(new LifeNoiseParameters(
                current.Mode, seed, current.Density, current.Scale,
                current.WarpStrength, current.ClusterStrength));
        }

        /// <summary>Asks for a candidate built from the current parameters.</summary>
        public void RequestCandidate()
        {
            lock (gate)
            {
                if (disposed)
                    return;

                wantCandidate = true;
                requestPending = true;
                failure = null;
            }

            StartNextIfIdle();
        }

        /// <summary>
        /// Main-thread pump. Adopts a finished candidate when it still describes the
        /// parameters being edited, drops it when it does not, and starts a pending
        /// request as soon as the worker is free. Returns true when a new candidate
        /// was adopted.
        /// </summary>
        public bool PumpGeneration()
        {
            bool adopted = false;

            lock (gate)
            {
                if (finishedReady)
                {
                    // The comparison is against the live parameters, not against which
                    // task finished last: "only the newest parameters win" has to hold
                    // when a slow old task overtakes a fast new one.
                    if (wantCandidate && finishedParameters.Equals(parameters))
                    {
                        // Swap rather than copy. The buffer the main thread was reading
                        // becomes the worker's next target, so a steady stream of
                        // previews allocates nothing.
                        (candidate, worker) = (worker, candidate);
                        candidateAliveCount = finishedAlive;
                        candidateDensity = candidate.Length > 0
                            ? (float)finishedAlive / candidate.Length
                            : 0f;
                        lastGenerationMilliseconds = finishedMilliseconds;
                        candidateParameters = finishedParameters;
                        hasCandidate = true;
                        requestPending = false;
                        adopted = true;
                    }

                    // Either way it is consumed: a result that was refused must not be
                    // adopted later just because it happens to fit again.
                    finishedReady = false;
                }
            }

            StartNextIfIdle();
            return adopted;
        }

        /// <summary>Caller must hold <see cref="gate"/>.</summary>
        private void StartNextIfIdle()
        {
            LifeNoiseParameters snapshot;
            byte[] buffer;

            lock (gate)
            {
                // Three separate reasons to hold off, and they are not interchangeable:
                // nothing is wanted (the user cancelled), nothing was asked for (the
                // debounce has not elapsed), or the worker is busy. A pending request
                // survives a busy worker, so a long drag coalesces into one extra
                // generation instead of one per event or none at all.
                if (disposed || !wantCandidate || !requestPending || working || finishedReady)
                    return;

                working = true;
                snapshot = parameters;
                buffer = worker;
            }

            Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                int alive;

                try
                {
                    generator(snapshot, Width, Height, buffer);
                    alive = LifeNoiseSeeding.CountAlive(buffer);
                }
                catch (Exception exception)
                {
                    watch.Stop();

                    lock (gate)
                    {
                        working = false;
                        requestPending = false;

                        // Stop waiting for something that will never arrive: leaving
                        // wantCandidate set would keep the clock disabled for good.
                        wantCandidate = false;
                        failure = exception.Message;
                    }

                    return;
                }

                watch.Stop();

                lock (gate)
                {
                    working = false;

                    if (disposed)
                        return;

                    finishedReady = true;
                    finishedAlive = alive;
                    finishedMilliseconds = watch.Elapsed.TotalMilliseconds;
                    finishedParameters = snapshot;
                }
            });
        }

        /// <summary>
        /// Drops the candidate and stops the pipeline. Anything already in flight is
        /// abandoned -- it may run to completion, but nothing it produces is wanted -- and
        /// no replacement is started. The interface is released in the same call: the
        /// task winding down behind the scenes must not keep the clock disabled.
        /// </summary>
        public void Cancel()
        {
            lock (gate)
            {
                hasCandidate = false;
                wantCandidate = false;
                requestPending = false;
                finishedReady = false;
                failure = null;
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
                if (!hasCandidate || IsStaleLocked)
                    return null;

                var confirmed = (byte[])candidate.Clone();

                appliedParameters = candidateParameters;
                hasAppliedParameters = true;
                hasCandidate = false;

                // The candidate has been consumed; do not build another one behind
                // the user's back.
                wantCandidate = false;
                requestPending = false;

                return confirmed;
            }
        }

        /// <summary>
        /// The confirmed board came from somewhere else (a specimen, or a manual
        /// clear), so the seeding parameters no longer describe what is loaded.
        /// </summary>
        public void ForgetApplied()
        {
            lock (gate) { hasAppliedParameters = false; }
        }

        /// <summary>
        /// Refuses everything from here on. Deliberately does NOT wait for a running task:
        /// this is called from the destroy path on the main thread, and blocking there to
        /// collect a result nobody will use would turn a clean exit into a stall. The task
        /// finishes on its own and its result is dropped by the <see cref="disposed"/> check.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                wantCandidate = false;
                requestPending = false;
                finishedReady = false;
                hasCandidate = false;
                failure = null;
            }
        }
    }
}
