using System;
using System.Collections.Generic;
using System.Text;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Derives canonical oscillator cell lists from authoritative RLE and verifies their
    /// periods against the real LifeSimulation.
    /// </summary>
    internal static class Probe
    {
        private const int Grid = 48;

        // Authoritative RLE (copy.sh/life examples), decoded rather than hand-transcribed.
        private const string PulsarRle =
            "#N Pulsar\n" +
            "x = 13, y = 13, rule = B3/S23\n" +
            "2b3o3b3o2b2$o4bobo4bo$o4bobo4bo$o4bobo4bo$2b3o3b3o2b2$2b3o3b3o2b$o4bob\n" +
            "o4bo$o4bobo4bo$o4bobo4bo2$2b3o3b3o!";

        private const string PentadecathlonRle =
            "#N Pentadecathlon\n" +
            "x = 10, y = 3, rule = B3/S23\n" +
            "2bo4bo2b$2ob4ob2o$2bo4bo!";

        /// <summary>Returns 0 when all decoded patterns match their expected period, 1 otherwise.</summary>
        public static int Run()
        {
            int failures = 0;
            failures += ProbeRle("PULSAR", PulsarRle, 3);
            failures += ProbeRle("PENTADECATHLON", PentadecathlonRle, 15);
            return failures == 0 ? 0 : 1;
        }

        private static int ProbeRle(string name, string rle, int expectedPeriod)
        {
            List<(int X, int Y)> decoded = Rle.Decode(rle);
            var cells = new LifeCell[decoded.Count];
            for (int i = 0; i < decoded.Count; i++)
            {
                cells[i] = new LifeCell(decoded[i].X, decoded[i].Y);
            }

            var sim = new LifeSimulation(Grid, Grid);
            sim.LoadCentered(cells);
            string initial = Snapshot(sim);

            int period = -1;
            for (int step = 1; step <= 40; step++)
            {
                sim.Step();
                if (Snapshot(sim) == initial)
                {
                    period = step;
                    break;
                }
            }

            var pops = new List<int>();
            var popProbe = new LifeSimulation(Grid, Grid);
            popProbe.LoadCentered(cells);
            pops.Add(popProbe.Population);
            for (int i = 0; i < 8; i++)
            {
                popProbe.Step();
                pops.Add(popProbe.Population);
            }

            bool ok = period == expectedPeriod;
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {name,-18} cells={decoded.Count,3} " +
                              $"period={(period < 0 ? "none" : period.ToString())} (expected {expectedPeriod})");
            Console.WriteLine($"       populations gen0..8: {string.Join(",", pops)}");
            Console.WriteLine($"       C# cells:");
            Console.WriteLine($"            {Rle.ToCSharp(decoded, 12)}");
            Console.WriteLine();
            return ok ? 0 : 1;
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
