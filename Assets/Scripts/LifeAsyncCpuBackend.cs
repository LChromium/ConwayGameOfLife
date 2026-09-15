using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ConwayGameOfLife
{
    /// <summary>
    /// The CPU reference rules, run on a worker thread instead of the frame.
    ///
    /// <para><b>Why this exists.</b> A single CPU generation costs 2 ms at 256x256 and 650 ms at
    /// 4096x4096. Round 3 stopped the clock from amplifying that cost across frames, but a
    /// generation was still computed inside <c>Update</c>: the frame that contained it took as
    /// long as the generation, so the terminal could not be paused, panned or edited during a
    /// step. Here the rules move to a worker, the interface keeps its own board, and a finished
    /// generation is taken over by the main thread one at a time.</para>
    ///
    /// <para><b>This does not make the algorithm faster.</b> The same
    /// <see cref="LifeSimulation"/> does the same work on the same number of cells. It changes
    /// WHERE the work blocks: the frame no longer contains it. The throughput ceiling is
    /// unchanged -- one generation per step cost -- and the whole-board upload that follows an
    /// adopted generation is still paid on the main thread.</para>
    ///
    /// <para><b>Ownership, which is what the isolation rests on.</b> Four buffers exist, and
    /// each one has exactly one owner at any moment:</para>
    /// <list type="bullet">
    /// <item><c>display</c> -- the main thread's board. Every read the interface makes
    /// (<see cref="Generation"/>, <see cref="TryGetPopulation"/>, <see cref="TryReadAllCells"/>)
    /// answers from here, and the worker never sees this array.</item>
    /// <item><c>spare</c> -- the rotating second board. Handed to the worker as its output
    /// target (and, when the worker has to rebuild, as the input to rebuild from). The main
    /// thread does not touch it once it has been handed over.</item>
    /// <item><c>simulation</c>'s two buffers -- inside the worker, and never handed out.</item>
    /// </list>
    /// <para>Adoption swaps <c>display</c> and the finished board, so a generation costs no copy
    /// on the main thread; the buffer the interface was reading becomes the worker's next
    /// target.</para>
    ///
    /// <para><b>One generation at a time.</b> <see cref="Step"/> is refused while a generation is
    /// in flight or waiting to be adopted: there is no queue, so nothing accumulates behind a
    /// slow board, and the buffer of an unadopted result cannot be overwritten. A refused step is
    /// counted (<see cref="RefusedSubmissions"/>) rather than silently dropped.</para>
    ///
    /// <para><b>A replaced board invalidates results, by identity.</b> Every command that moves
    /// the board (<see cref="LoadBoard"/>, <see cref="Clear"/>, <see cref="SetCell"/>, a boundary
    /// change) bumps a session version. A result carries the version of the board it was computed
    /// from, and adoption requires it to match the current one AND to land immediately after the
    /// displayed generation. This is the stage-B request-identity rule applied to evolution: a
    /// reset, an edit, a loaded specimen or a backend switch must not be overwritten by a
    /// generation computed before it. Refusals are counted, so a discarded generation is visible
    /// in the record instead of vanishing.</para>
    ///
    /// <para><b>Pausing.</b> This class has no opinion about pauses. It keeps a finished
    /// generation in <see cref="HasCompletedGeneration"/> until somebody adopts it, so a paused
    /// caller simply stops adopting: the display freezes, the computation in flight finishes into
    /// the waiting slot, and the caller takes it when it resumes. Nothing is skipped.</para>
    ///
    /// <para>All members must be called from the main thread except the computation itself, which
    /// this class starts and never blocks on -- including <see cref="Dispose"/>, which is called
    /// from the destroy path and refuses everything from then on without waiting.</para>
    /// </summary>
    public sealed class LifeAsyncCpuBackend : ILifeAsyncBackend
    {
        private readonly object gate = new();
        private readonly int cells;

        // -- main-thread display state. The worker never reads or writes these. --
        private byte[] display;
        private int displayGeneration;
        private int displayPopulation;

        // -- the rotating board the worker is handed --
        private byte[] spare;

        // -- guarded by `gate`; touched from both threads --
        private int sessionVersion;
        private bool resyncRequired = true;
        private bool wrapEdges = true;
        private bool computing;
        private bool completed;
        private byte[] readyBoard;
        private LifeStepOutcome completedOutcome;
        private string failure;
        private bool disposed;

        // The worker's own simulation. Only the worker thread touches it, and only while a
        // generation is in flight -- the main thread has no path to it at all.
        private readonly LifeSimulation simulation;

        // The generation the simulation was rebuilt at, so a result can be expressed in the
        // interface's numbering. LifeSimulation's own counter starts at zero after a rebuild and
        // is the reference implementation's business, not something this class may set.
        private int generationBase;

        private int adoptedGenerations;
        private int refusedGenerations;
        private int refusedSubmissions;
        private double lastComputeMilliseconds;
        private double lastResultCopyMilliseconds;
        private double lastResyncCopyMilliseconds;

        /// <summary>How a submitted computation is started. Null means the thread pool.</summary>
        private readonly Action<Action> schedule;

        /// <summary>
        /// <paramref name="schedule"/> runs one submitted work item; null uses
        /// <see cref="Task.Run(Action)"/>.
        ///
        /// <para>The seam exists so tests can hold a computation in flight and decide exactly when
        /// it finishes. The questions this class has to answer -- pause, reset, single step,
        /// backend switch -- are about ORDER, and a real thread turns order into a race; the
        /// isolation property itself is tested against the real thread pool, where it belongs.</para>
        /// </summary>
        public LifeAsyncCpuBackend(int width, int height, Action<Action> schedule = null)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be positive.");

            Width = width;
            Height = height;
            cells = width * height;
            display = new byte[cells];
            spare = new byte[cells];
            simulation = new LifeSimulation(width, height);
            this.schedule = schedule ?? (work => Task.Run(work));
        }

        /// <summary>
        /// Deliberately the same label as the synchronous reference backend: these ARE the CPU
        /// rules, and the panel's "which backend" answer is still "CPU". That they now run off
        /// the frame is reported by the documentation and by the timing fields, not by renaming
        /// the backend under the user.
        /// </summary>
        public string Name => "CPU";

        public int Width { get; }
        public int Height { get; }

        /// <summary>The displayed generation: the last one that was adopted.</summary>
        public int Generation => displayGeneration;

        public bool WrapEdges
        {
            get { lock (gate) { return wrapEdges; } }
            set
            {
                lock (gate)
                {
                    if (disposed || wrapEdges == value)
                        return;

                    // The rules changed, so a generation in flight was computed under rules that
                    // no longer hold. Bumping the version refuses it.
                    wrapEdges = value;
                    sessionVersion++;
                }
            }
        }

        // -- backed by the adopted state only ---------------------------------

        public bool TryGetPopulation(out int population)
        {
            population = displayPopulation;
            return true;
        }

        public bool TryReadAllCells(Span<uint> destination)
        {
            if (destination.Length < cells)
                return false;

            // Main thread only, and `display` is never written by the worker: this reads a board
            // that is not being computed into.
            byte[] source = display;
            for (int i = 0; i < cells; i++)
                destination[i] = source[i] != 0 ? 1u : 0u;

            return true;
        }

        // -- commands: they move the display board and invalidate what is in flight --

        public void LoadBoard(ReadOnlySpan<byte> board)
        {
            if (board.Length != cells)
            {
                throw new ArgumentException(
                    $"Board must be {cells} cells, got {board.Length}.", nameof(board));
            }

            lock (gate)
            {
                if (disposed)
                    return;

                MoveBoardLocked();
                board.CopyTo(display);
                displayGeneration = 0;
                displayPopulation = CountAlive(display);
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                if (disposed)
                    return;

                MoveBoardLocked();
                Array.Clear(display, 0, display.Length);
                displayGeneration = 0;
                displayPopulation = 0;
            }
        }

        public void SetCell(int x, int y, bool alive)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return;

            lock (gate)
            {
                if (disposed)
                    return;

                int index = y * Width + x;
                bool wasAlive = display[index] != 0;
                if (wasAlive == alive)
                    return;

                MoveBoardLocked();
                display[index] = alive ? (byte)1 : (byte)0;
                displayPopulation += alive ? 1 : -1;
            }
        }

        /// <summary>
        /// Caller must hold <see cref="gate"/>. Marks the board as moved: the session version
        /// advances so a generation computed from the old board is refused, and the worker is told
        /// to rebuild its simulation from the display board before it steps again.
        ///
        /// <para>The rebuild is deliberately deferred to the next submission rather than done
        /// here: painting a cell must not cost a 16-million-cell load on the frame that paints
        /// it, and the worker is the only place allowed to touch the simulation anyway.</para>
        /// </summary>
        private void MoveBoardLocked()
        {
            sessionVersion++;
            resyncRequired = true;
        }

        // -- evolution ---------------------------------------------------------

        /// <summary>
        /// Submits one generation. Returns immediately; the result arrives through
        /// <see cref="TryAdoptCompletedGeneration"/>. Refused -- and counted -- when a generation
        /// is already in flight or waiting to be adopted.
        /// </summary>
        public void Step()
        {
            int version;
            bool wrap;
            int baseGeneration;
            byte[] target;
            bool rebuild;

            lock (gate)
            {
                if (disposed || computing || completed || spare == null)
                {
                    if (!disposed)
                        refusedSubmissions++;

                    return;
                }

                version = sessionVersion;
                wrap = wrapEdges;
                baseGeneration = displayGeneration;
                target = spare;
                spare = null;
                rebuild = resyncRequired;
                resyncRequired = false;
                computing = true;
            }

            if (rebuild)
            {
                // Hand the worker its own copy of the board to rebuild from. The copy happens on
                // the main thread precisely so that the worker never reads an array the main
                // thread can still write, and this buffer doubles as the output target: the
                // simulation consumes it before the result is written back into it.
                var watch = Stopwatch.StartNew();
                display.AsSpan().CopyTo(target);
                watch.Stop();
                lastResyncCopyMilliseconds = watch.Elapsed.TotalMilliseconds;
            }

            byte[] source = rebuild ? target : null;
            schedule(() => Compute(version, wrap, baseGeneration, target, source));
        }

        /// <summary>Worker thread. The only place the simulation is touched.</summary>
        private void Compute(int version, bool wrap, int baseGeneration, byte[] target, byte[] rebuildSource)
        {
            double loadMilliseconds = 0.0;
            double computeMilliseconds = 0.0;
            double copyMilliseconds = 0.0;
            int generation;
            int population;

            try
            {
                if (rebuildSource != null)
                {
                    var loadWatch = Stopwatch.StartNew();
                    RebuildSimulation(rebuildSource, baseGeneration, wrap);
                    loadWatch.Stop();
                    loadMilliseconds = loadWatch.Elapsed.TotalMilliseconds;
                }
                else
                {
                    simulation.WrapEdges = wrap;
                }

                var stepWatch = Stopwatch.StartNew();
                simulation.Step();
                stepWatch.Stop();
                computeMilliseconds = stepWatch.Elapsed.TotalMilliseconds;

                var copyWatch = Stopwatch.StartNew();
                for (int y = 0; y < Height; y++)
                {
                    int row = y * Width;
                    for (int x = 0; x < Width; x++)
                        target[row + x] = simulation.IsAlive(x, y) ? (byte)1 : (byte)0;
                }

                copyWatch.Stop();
                copyMilliseconds = copyWatch.Elapsed.TotalMilliseconds;

                generation = generationBase + simulation.Generation;
                population = simulation.Population;
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    computing = false;
                    spare = target;
                    failure = exception.Message;
                }

                return;
            }

            lock (gate)
            {
                computing = false;

                if (disposed)
                {
                    // Nobody will adopt anything again. The board is simply dropped.
                    spare = target;
                    return;
                }

                readyBoard = target;
                completedOutcome = new LifeStepOutcome(
                    version, generation, population, loadMilliseconds, computeMilliseconds, copyMilliseconds);
                completed = true;
            }
        }

        /// <summary>
        /// Rebuilds the worker's simulation from a board the main thread handed over. This mirrors
        /// <see cref="CpuLifeBackend.LoadBoard"/>, including its per-cell <c>SetCell</c> path, so
        /// the population the worker reports is maintained by the same reference code.
        /// </summary>
        private void RebuildSimulation(byte[] board, int baseGeneration, bool wrap)
        {
            simulation.Clear();

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                {
                    if (board[row + x] != 0)
                        simulation.SetCell(x, y, true);
                }
            }

            simulation.WrapEdges = wrap;
            generationBase = baseGeneration;
        }

        public bool IsComputing
        {
            get { lock (gate) { return computing; } }
        }

        public bool HasCompletedGeneration
        {
            get { lock (gate) { return completed; } }
        }

        public bool TryAdoptCompletedGeneration(out LifeStepOutcome outcome)
        {
            lock (gate)
            {
                outcome = default;

                if (disposed || !completed)
                    return false;

                LifeStepOutcome ready = completedOutcome;
                byte[] board = readyBoard;

                // Consumed either way: a result that was refused must not be adopted later just
                // because the numbering happens to fit again.
                completed = false;
                readyBoard = null;

                if (ready.Version != sessionVersion || ready.Generation != displayGeneration + 1)
                {
                    // The board was replaced while this ran, or it would not land immediately
                    // after the displayed generation. Visible, not silent.
                    refusedGenerations++;
                    spare = board;
                    return false;
                }

                // Swap, do not copy: the interface's old board becomes the worker's next target.
                (display, spare) = (board, display);
                displayGeneration = ready.Generation;
                displayPopulation = ready.Population;
                adoptedGenerations++;
                lastComputeMilliseconds = ready.ComputeMilliseconds;
                lastResultCopyMilliseconds = ready.ResultCopyMilliseconds;
                outcome = ready;
                return true;
            }
        }

        // -- reporting ---------------------------------------------------------

        public int AdoptedGenerations => adoptedGenerations;
        public int RefusedGenerations => refusedGenerations;
        public int RefusedSubmissions => refusedSubmissions;
        public double LastComputeMilliseconds => lastComputeMilliseconds;
        public double LastResultCopyMilliseconds => lastResultCopyMilliseconds;

        /// <summary>Cost of the last handover copy made on the main thread when rebuilding.</summary>
        public double LastResyncCopyMilliseconds => lastResyncCopyMilliseconds;
        public string FailureMessage
        {
            get { lock (gate) { return failure; } }
        }

        /// <summary>
        /// Refuses everything from here on. Deliberately does NOT wait for a running
        /// computation: this is called from the destroy path, and blocking the main thread to
        /// collect a board nobody will display would turn a clean exit into a stall. The
        /// computation finishes on its own and is refused by <see cref="disposed"/>.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                completed = false;
                readyBoard = null;
            }
        }

        private static int CountAlive(byte[] board)
        {
            int alive = 0;
            for (int i = 0; i < board.Length; i++)
                alive += board[i] != 0 ? 1 : 0;

            return alive;
        }
    }
}
