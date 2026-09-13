using System;
using System.Text;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Investigates why Analyze() reports no period for the oscillator patterns while the
    /// RLE-decoded probe reports the expected periods.
    /// </summary>
    internal static class Diagnose
    {
        /// <summary>
        /// Verifies the shipped PULSAR against the canonical RLE in two independent ways:
        ///   1. its cell set matches the RLE-decoded set exactly, and
        ///   2. its measured period equals the declared period.
        /// Returns 0 only when BOTH hold; 1 otherwise. (Previously it checked coordinates only,
        /// so the period claim in the summary line was never actually verified.)
        /// </summary>
        public static int Run()
        {
            LifePattern pulsar = Find("PULSAR");
            Console.WriteLine($"PULSAR declared: kind={pulsar.Kind} period={pulsar.Period} cells={pulsar.Cells.Length}");

            foreach (int grid in new[] { 48, 64 })
            {
                var sim = new LifeSimulation(grid, grid);
                sim.LoadCentered(pulsar.Cells);
                string initial = Snapshot(sim);

                Console.WriteLine($"  grid {grid}x{grid}: population={sim.Population}");

                for (int step = 1; step <= 5; step++)
                {
                    sim.Step();
                    bool same = Snapshot(sim) == initial;
                    Console.WriteLine($"    gen {step}: pop={sim.Population,3}  equalsGen0={same}");
                }
            }

            // Compare the shipped cells against the RLE-decoded canonical set.
            var decoded = Rle.Decode(
                "#N Pulsar\n" +
                "x = 13, y = 13, rule = B3/S23\n" +
                "2b3o3b3o2b2$o4bobo4bo$o4bobo4bo$o4bobo4bo$2b3o3b3o2b2$2b3o3b3o2b$o4bob\n" +
                "o4bo$o4bobo4bo$o4bobo4bo2$2b3o3b3o!");

            Console.WriteLine($"  shipped cells={pulsar.Cells.Length}  decoded cells={decoded.Count}");

            var shipped = new System.Collections.Generic.HashSet<string>();
            foreach (LifeCell cell in pulsar.Cells)
            {
                shipped.Add($"{cell.X},{cell.Y}");
            }

            int missing = 0;
            foreach ((int X, int Y) in decoded)
            {
                if (!shipped.Contains($"{X},{Y}"))
                {
                    missing++;
                    if (missing <= 10)
                    {
                        Console.WriteLine($"    decoded cell ({X},{Y}) is MISSING from shipped pattern");
                    }
                }
            }

            int extra = 0;
            var decodedSet = new System.Collections.Generic.HashSet<string>();
            foreach ((int X, int Y) in decoded)
            {
                decodedSet.Add($"{X},{Y}");
            }

            foreach (LifeCell cell in pulsar.Cells)
            {
                if (!decodedSet.Contains($"{cell.X},{cell.Y}"))
                {
                    extra++;
                    if (extra <= 10)
                    {
                        Console.WriteLine($"    shipped cell ({cell.X},{cell.Y}) is NOT in decoded pattern");
                    }
                }
            }

            Console.WriteLine($"  summary: missing={missing} extra={extra}");

            // Second, independent verification: the declared period must be the measured one.
            int measuredPeriod = MeasurePeriod(pulsar.Cells, 40);
            bool periodOk = measuredPeriod == pulsar.Period;
            Console.WriteLine($"  period: declared={pulsar.Period} measured={(measuredPeriod < 0 ? "none" : measuredPeriod.ToString())} " +
                              $"-> {(periodOk ? "match" : "MISMATCH")}");

            return (missing == 0 && extra == 0 && periodOk) ? 0 : 1;
        }

        /// <summary>First generation at which the board repeats its initial state, or -1.</summary>
        private static int MeasurePeriod(LifeCell[] cells, int maxGenerations)
        {
            var sim = new LifeSimulation(64, 64);
            sim.LoadCentered(cells);
            string initial = Snapshot(sim);

            for (int generation = 1; generation <= maxGenerations; generation++)
            {
                sim.Step();
                if (Snapshot(sim) == initial)
                {
                    return generation;
                }
            }

            return -1;
        }

        private static LifePattern Find(string name)
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.EnglishName == name)
                {
                    return pattern;
                }
            }

            throw new InvalidOperationException(name);
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
