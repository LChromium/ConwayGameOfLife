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
        public static void Run()
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
