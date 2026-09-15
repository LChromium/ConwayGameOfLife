using System;

namespace ConwayGameOfLife
{
    /// <summary>
    /// One generation computed off the main thread, with everything the main thread needs to
    /// decide whether it may be shown. Produced by an <see cref="ILifeAsyncBackend"/> and
    /// consumed by <see cref="ILifeAsyncBackend.TryAdoptCompletedGeneration"/>.
    ///
    /// <para><b>Why the version is in here and not just the board.</b> A generation is computed
    /// from a definite board; if that board was replaced while the computation ran, the result
    /// describes a board that no longer exists. The version identifies the board the result
    /// belongs to, the generation says where it belongs in the sequence, and the population
    /// travels with it so the readout never has to scan a board the worker may be writing.</para>
    /// </summary>
    public readonly struct LifeStepOutcome
    {
        public LifeStepOutcome(int version, int generation, int population,
            double boardLoadMilliseconds, double computeMilliseconds, double resultCopyMilliseconds)
        {
            Version = version;
            Generation = generation;
            Population = population;
            BoardLoadMilliseconds = boardLoadMilliseconds;
            ComputeMilliseconds = computeMilliseconds;
            ResultCopyMilliseconds = resultCopyMilliseconds;
        }

        /// <summary>The board identity this generation was computed from.</summary>
        public int Version { get; }

        /// <summary>The generation the board is at once this result is displayed.</summary>
        public int Generation { get; }

        /// <summary>Live cells in this generation, counted by the worker.</summary>
        public int Population { get; }

        /// <summary>Worker-side cost of rebuilding its simulation from the board (0 when not needed).</summary>
        public double BoardLoadMilliseconds { get; }

        /// <summary>Worker-side cost of the rule step itself.</summary>
        public double ComputeMilliseconds { get; }

        /// <summary>Worker-side cost of reading the finished board out for handover.</summary>
        public double ResultCopyMilliseconds { get; }
    }

    /// <summary>
    /// A backend that retires generations on a worker thread.
    ///
    /// <para><b>The contract, in the order it matters.</b></para>
    /// <list type="number">
    /// <item>The worker owns its simulation state exclusively while a generation is in flight.
    /// The main thread never reads a board the worker is writing: <see cref="ILifeBackend.TryReadAllCells"/>,
    /// <see cref="ILifeBackend.Generation"/> and <see cref="ILifeBackend.TryGetPopulation"/>
    /// answer from the last ADOPTED generation, and those answers only change in
    /// <see cref="TryAdoptCompletedGeneration"/>.</item>
    /// <item>At most one generation is in flight, and a completed one waiting to be adopted is
    /// never overwritten. A <see cref="ILifeBackend.Step"/> that cannot be honoured is refused
    /// and counted rather than queued.</item>
    /// <item>Adoption happens only through <see cref="TryAdoptCompletedGeneration"/>, which
    /// refuses a result whose board has been replaced or which would not land immediately after
    /// the displayed generation. A refusal is counted, never silent.</item>
    /// </list>
    ///
    /// <para>Callers must be on the main thread.</para>
    /// </summary>
    public interface ILifeAsyncBackend : ILifeBackend
    {
        /// <summary>True while a generation is being computed on the worker thread.</summary>
        bool IsComputing { get; }

        /// <summary>
        /// True when a computed generation is waiting to be adopted. While this is true the
        /// worker is idle and its buffer is reserved: nothing overwrites a result nobody has
        /// received yet.
        /// </summary>
        bool HasCompletedGeneration { get; }

        /// <summary>
        /// Takes the completed generation into the display state, swapping its board in without
        /// copying. Returns false when nothing is ready, or when the result was computed for a
        /// board that has since been replaced or does not follow the displayed generation.
        /// Either way the result is consumed.
        /// </summary>
        bool TryAdoptCompletedGeneration(out LifeStepOutcome outcome);

        /// <summary>Generations taken into the display state.</summary>
        int AdoptedGenerations { get; }

        /// <summary>
        /// Generations computed and then refused because a command replaced the board they
        /// belonged to. Counted so a discarded generation is visible rather than silent.
        /// </summary>
        int RefusedGenerations { get; }

        /// <summary>
        /// Steps refused because a generation was already in flight or waiting to be adopted.
        /// The pipeline never queues work behind itself.
        /// </summary>
        int RefusedSubmissions { get; }

        /// <summary>
        /// Steps refused because the pipeline is in a failed state that nobody has cleared.
        /// Refusing is what makes "a failure stops automatic submission" a property of the BACKEND
        /// rather than of a caller's check: the worker can fail between a caller's test and its
        /// submission, and a submission accepted in that window would clear an error the user has
        /// not seen.
        /// </summary>
        int RefusedWhileFailed { get; }

        /// <summary>Compute cost of the most recently adopted generation, in milliseconds.</summary>
        double LastComputeMilliseconds { get; }

        /// <summary>Result read-out cost of the most recently adopted generation, in milliseconds.</summary>
        double LastResultCopyMilliseconds { get; }

        /// <summary>
        /// Cost of the last board handover the MAIN THREAD paid, in milliseconds, when the worker
        /// had to rebuild its simulation after the board changed. Zero on a generation that reused
        /// the worker's own state.
        /// </summary>
        double LastResyncCopyMilliseconds { get; }

        /// <summary>
        /// Why the last computation failed, or null. A worker that throws must be reported:
        /// otherwise the clock would keep asking for generations that never arrive.
        ///
        /// <para>Only a failure that belongs to the CURRENT session is reported: a task that threw
        /// for a board which has since been replaced reclaims its buffer and says nothing, the same
        /// identity rule a successful result follows. A failure also leaves the worker's state
        /// untrustworthy (the simulation may have advanced before it threw), so the next submission
        /// rebuilds from the displayed board.</para>
        ///
        /// <para>While this is non-null the backend REFUSES submissions -- see
        /// <see cref="RefusedWhileFailed"/>. That refusal is the guarantee, not a caller's check:
        /// it happens in the same lock that would accept the work, so a failure that lands between
        /// a caller's test and its submission cannot be silently retried away.</para>
        /// </summary>
        string FailureMessage { get; }

        /// <summary>
        /// Forgets a failure so the pipeline can be used again. This and a command that replaces
        /// the board are the ONLY things that lift a failed state; nothing clears it automatically.
        /// The next submission rebuilds the worker's simulation from the displayed board (a failure
        /// always leaves that required), so a retry never continues from a state that may already
        /// be ahead.
        /// </summary>
        void ClearFailure();
    }
}
