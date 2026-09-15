using System;

namespace ConwayGameOfLife
{
    /// <summary>
    /// One evolution backend. Stage A has exactly two: the CPU reference, which
    /// wraps the untouched <see cref="LifeSimulation"/>, and the compute-shader
    /// backend.
    ///
    /// This is a thin adapter for the terminal and the correctness harness, not
    /// a general simulation framework. It carries only:
    ///   * the board size and the current generation,
    ///   * the two commands that replace the board (load a definite state, clear),
    ///   * one evolution step,
    ///   * the two query paths the terminal and the harness need.
    ///
    /// It deliberately does NOT expose "give me a random board": both backends
    /// must be handed the *same* definite array, so that a CPU/GPU disagreement
    /// can never be explained away as "the seeds differed".
    /// </summary>
    public interface ILifeBackend : IDisposable
    {
        /// <summary>Short label for the status readout, e.g. "CPU" or "GPU".</summary>
        string Name { get; }

        int Width { get; }
        int Height { get; }
        int Generation { get; }

        /// <summary>False = fixed boundary (outside is dead), true = wrapping torus.</summary>
        bool WrapEdges { get; set; }

        /// <summary>
        /// Replaces the whole board from a definite source and resets the
        /// generation to 0. <paramref name="cells"/> must be Width * Height long
        /// and contain only 0 or 1. Both backends receive the identical array.
        /// </summary>
        void LoadBoard(ReadOnlySpan<byte> cells);

        void Clear();

        /// <summary>
        /// Paints one cell. Out-of-range coordinates are ignored, matching
        /// LifeSimulation.SetCell.
        /// </summary>
        void SetCell(int x, int y, bool alive);

        void Step();

        /// <summary>
        /// Population, but only when the backend can answer without a full-board
        /// round trip. The GPU backend returns false in stage A rather than
        /// stalling the pipeline; the terminal then shows "unavailable" instead
        /// of inventing a number.
        /// </summary>
        bool TryGetPopulation(out int population);

        /// <summary>
        /// Full state readback, row-major, <c>destination[y * Width + x]</c>,
        /// values 0 or 1.
        ///
        /// Correctness evidence only. This is never on the display path, and its
        /// cost is excluded from every timing number the project reports.
        /// </summary>
        bool TryReadAllCells(Span<uint> destination);
    }
}
