using System;

namespace ConwayGameOfLife
{
    public sealed class LifeSimulation
    {
        private byte[] current;
        private byte[] next;

        public int Width { get; }
        public int Height { get; }
        public int Generation { get; private set; }
        public int Population { get; private set; }
        public bool WrapEdges { get; set; }

        public LifeSimulation(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be positive.");

            Width = width;
            Height = height;
            current = new byte[width * height];
            next = new byte[current.Length];
        }

        public bool IsAlive(int x, int y)
        {
            return IsInside(x, y) && current[IndexOf(x, y)] != 0;
        }

        public void SetCell(int x, int y, bool alive)
        {
            if (!IsInside(x, y))
                return;

            int index = IndexOf(x, y);
            bool wasAlive = current[index] != 0;
            if (wasAlive == alive)
                return;

            current[index] = alive ? (byte)1 : (byte)0;
            Population += alive ? 1 : -1;
        }

        public void Clear()
        {
            Array.Clear(current, 0, current.Length);
            Array.Clear(next, 0, next.Length);
            Generation = 0;
            Population = 0;
        }

        public void LoadCentered(ReadOnlySpan<LifeCell> cells)
        {
            Clear();
            if (cells.Length == 0)
                return;

            int minX = cells[0].X;
            int maxX = cells[0].X;
            int minY = cells[0].Y;
            int maxY = cells[0].Y;

            for (int i = 1; i < cells.Length; i++)
            {
                minX = Math.Min(minX, cells[i].X);
                maxX = Math.Max(maxX, cells[i].X);
                minY = Math.Min(minY, cells[i].Y);
                maxY = Math.Max(maxY, cells[i].Y);
            }

            int offsetX = (Width - (maxX - minX + 1)) / 2 - minX;
            int offsetY = (Height - (maxY - minY + 1)) / 2 - minY;
            for (int i = 0; i < cells.Length; i++)
                SetCell(cells[i].X + offsetX, cells[i].Y + offsetY, true);
        }

        public void Randomize(float probability, Random random)
        {
            if (random == null)
                throw new ArgumentNullException(nameof(random));

            Clear();
            probability = Math.Clamp(probability, 0f, 1f);
            for (int i = 0; i < current.Length; i++)
            {
                current[i] = random.NextDouble() < probability ? (byte)1 : (byte)0;
                Population += current[i];
            }
        }

        public void Step()
        {
            int population = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int index = IndexOf(x, y);
                    int neighbours = CountNeighbours(x, y);
                    byte alive = (byte)(neighbours == 3 || (current[index] != 0 && neighbours == 2) ? 1 : 0);
                    next[index] = alive;
                    population += alive;
                }
            }

            (current, next) = (next, current);
            Generation++;
            Population = population;
        }

        private int CountNeighbours(int x, int y)
        {
            int count = 0;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                        continue;

                    int neighbourX = x + offsetX;
                    int neighbourY = y + offsetY;
                    if (WrapEdges)
                    {
                        neighbourX = (neighbourX + Width) % Width;
                        neighbourY = (neighbourY + Height) % Height;
                    }

                    if (IsInside(neighbourX, neighbourY))
                        count += current[IndexOf(neighbourX, neighbourY)];
                }
            }

            return count;
        }

        private bool IsInside(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
        private int IndexOf(int x, int y) => y * Width + x;
    }

    public readonly struct LifeCell
    {
        public int X { get; }
        public int Y { get; }

        public LifeCell(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}
