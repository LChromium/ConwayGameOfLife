using System;

namespace ConwayGameOfLife
{
    /// <summary>
    /// The CPU reference backend. It owns one <see cref="LifeSimulation"/> and
    /// forwards to it without modifying it -- LifeSimulation stays the single
    /// source of truth for the rules, and stage A deliberately does not touch it.
    ///
    /// Population is free here because LifeSimulation maintains it incrementally.
    /// </summary>
    public sealed class CpuLifeBackend : ILifeBackend
    {
        private readonly LifeSimulation simulation;

        public CpuLifeBackend(int width, int height)
        {
            simulation = new LifeSimulation(width, height);
        }

        public string Name => "CPU";

        public int Width => simulation.Width;
        public int Height => simulation.Height;
        public int Generation => simulation.Generation;

        public bool WrapEdges
        {
            get => simulation.WrapEdges;
            set => simulation.WrapEdges = value;
        }

        public void LoadBoard(ReadOnlySpan<byte> cells)
        {
            if (cells.Length != Width * Height)
            {
                throw new ArgumentException(
                    $"Board must be {Width * Height} cells, got {cells.Length}.", nameof(cells));
            }

            // Clear() resets both buffers, the generation and the population;
            // SetCell then maintains Population incrementally as we load.
            simulation.Clear();

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (cells[y * Width + x] != 0)
                        simulation.SetCell(x, y, true);
                }
            }
        }

        public void Clear() => simulation.Clear();

        public void SetCell(int x, int y, bool alive) => simulation.SetCell(x, y, alive);

        public void Step() => simulation.Step();

        public bool TryGetPopulation(out int population)
        {
            population = simulation.Population;
            return true;
        }

        public bool TryReadAllCells(Span<uint> destination)
        {
            if (destination.Length < Width * Height)
                return false;

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    destination[y * Width + x] = simulation.IsAlive(x, y) ? 1u : 0u;
            }

            return true;
        }

        public void Dispose()
        {
            // Nothing unmanaged to release; LifeSimulation is plain managed arrays.
        }
    }
}
