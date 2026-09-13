using System;
using System.Collections.Generic;
using System.Text;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Exhaustive search for a period-15 oscillator by augmenting a 10-cell line with 1-2 extra
    /// cells inside the standard 3x10 pentadecathlon bounding box.
    /// </summary>
    internal static class Search
    {
        private const int Grid = 48;
        private const int TargetPeriod = 15;

        /// <summary>
        /// Returns 0 if at least one period-15 configuration was found in the search window,
        /// 1 otherwise. (An empty result is a real negative finding, not a crash.)
        /// </summary>
        public static int Run()
        {
            int found = 0;
            var line = new List<LifeCell>();
            for (int y = 0; y < 10; y++)
            {
                line.Add(new LifeCell(1, y));
            }

            // Candidate extra cells: a window around the line, so we are not assuming the
            // canonical 3x10 bounding box.
            var candidates = new List<LifeCell>();
            for (int y = -2; y <= 11; y++)
            {
                for (int x = -2; x <= 3; x++)
                {
                    if (x == 1 && y >= 0 && y <= 9)
                    {
                        continue;
                    }

                    candidates.Add(new LifeCell(x, y));
                }
            }

            Console.WriteLine($"window candidates: {candidates.Count}");
            Console.WriteLine("period histogram (up to 2 extra cells):");
            var histogram = new SortedDictionary<int, int>();

            for (int i = 0; i < candidates.Count; i++)
            {
                int p1 = Period(new List<LifeCell>(line) { candidates[i] });
                Bump(histogram, p1);

                for (int j = i + 1; j < candidates.Count; j++)
                {
                    int p2 = Period(new List<LifeCell>(line) { candidates[i], candidates[j] });
                    Bump(histogram, p2);
                    if (p2 == TargetPeriod)
                    {
                        found++;
                        Console.WriteLine(Describe(new List<LifeCell>(line) { candidates[i], candidates[j] }, p2));
                    }
                }
            }

            foreach (KeyValuePair<int, int> entry in histogram)
            {
                Console.WriteLine($"  period {(entry.Key < 0 ? "none" : entry.Key.ToString()):>4} : {entry.Value}");
            }

            // Baseline line only, plus the rest of the shipped set, are reported by the main harness.
            Console.WriteLine($"baseline line period: {Period(line)}");
            Console.WriteLine($"period-{TargetPeriod} configurations found: {found}");
            return found > 0 ? 0 : 1;
        }

        private static void Bump(SortedDictionary<int, int> histogram, int period)
        {
            histogram.TryGetValue(period, out int count);
            histogram[period] = count + 1;
        }

        private static string Describe(List<LifeCell> cells, int period)
        {
            var probe = new LifeSimulation(Grid, Grid);
            probe.LoadCentered(cells.ToArray());
            var pops = new List<int> { probe.Population };
            for (int i = 0; i < 5; i++)
            {
                probe.Step();
                pops.Add(probe.Population);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"  period={period} cells={cells.Count} pops={string.Join(",", pops)}");
            sb.Append("    ");
            foreach (LifeCell cell in cells)
            {
                sb.Append($"new({cell.X}, {cell.Y}), ");
            }

            return sb.ToString();
        }

        private static int Period(List<LifeCell> cells)
        {
            var sim = new LifeSimulation(Grid, Grid);
            sim.LoadCentered(cells.ToArray());
            string initial = Snapshot(sim);
            for (int step = 1; step <= 40; step++)
            {
                sim.Step();
                if (Snapshot(sim) == initial)
                {
                    return step;
                }
            }

            return -1;
        }

        private static string Snapshot(LifeSimulation sim)
        {
            var sb = new StringBuilder(sim.Width * sim.Height);
            for (int y = 0; y < sim.Height; y++)
            {
                for (int x = 0; x < sim.Width; x++)
                {
                    sb.Append(sim.IsAlive(x, y) ? '1' : '0');
                }
            }

            return sb.ToString();
        }
    }
}
