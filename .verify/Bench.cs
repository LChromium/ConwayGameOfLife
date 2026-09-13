using System;
using System.Diagnostics;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Measures the throughput of the shipped LifeSimulation so the documentation can quote
    /// real numbers instead of guesses.
    /// </summary>
    internal static class Bench
    {
        /// <summary>
        /// Returns 0 when the benchmark ran; 1 when a workload was invalidated (a board that died
        /// out makes its own numbers vacuous).
        ///
        /// The harness-wide contract is: 0 = the command's check passed, 1 = the check failed,
        /// 2 = usage error. A dead board is a failed measurement, not a usage error, so it must not
        /// report 2 - that code is reserved for bad arguments.
        /// </summary>
        public static int Run()
        {
            Console.WriteLine("LifeSimulation throughput micro-benchmark");
            Console.WriteLine("  NOT a Unity player measurement: this is a standalone .NET run of the");
            Console.WriteLine("  rule engine only. Board composition and generations are reported so the");
            Console.WriteLine("  workload is reproducible and interpretable.");
            Console.WriteLine();
            Console.WriteLine($"{"board",-14}{"wrap",-8}{"gens",-9}{"total ms",-11}{"ms/gen",-11}{"cells/s",-16}{"gens/s"}");

            int invalid = 0;

            foreach ((int W, int H) in new[] { (96, 64), (256, 256), (512, 512), (1024, 1024) })
            {
                foreach (bool wrap in new[] { false, true })
                {
                    invalid += Measure(W, H, wrap);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Workload: initial board from Randomize(p=0.28, seed=99), so every board is");
            Console.WriteLine("reproducible. Life is chaotic, so density drifts during a run; the figures are");
            Console.WriteLine("for the whole run, not a steady-state density.");
            Console.WriteLine("Rule-application floor: one Step() performs W*H cell updates, each reading 8");
            Console.WriteLine("neighbours, so cells/s is the comparable figure.");

            return invalid == 0 ? 0 : 1;
        }

        private static int Measure(int width, int height, bool wrap)
        {
            var sim = new LifeSimulation(width, height) { WrapEdges = wrap };
            sim.Randomize(0.28f, new Random(99));

            // Warm up the JIT and reach steady state for the dense/sparse characteristics.
            for (int i = 0; i < 30; i++)
            {
                sim.Step();
            }

            int generations = width >= 1024 ? 60 : width >= 512 ? 120 : 400;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < generations; i++)
            {
                sim.Step();
            }

            sw.Stop();

            double totalMs = sw.Elapsed.TotalMilliseconds;
            double msPerGen = totalMs / generations;
            double cellsPerSecond = (double)width * height * generations / sw.Elapsed.TotalSeconds;
            double gensPerSecond = generations / sw.Elapsed.TotalSeconds;

            Console.WriteLine($"{$"{width}x{height}",-14}{(wrap ? "yes" : "no"),-8}{generations,-9}" +
                              $"{totalMs,-11:F1}{msPerGen,-11:F4}{cellsPerSecond,-16:F0}{gensPerSecond:F1}");

            // A board that died out makes its own throughput figure vacuous.
            if (sim.Population == 0)
            {
                Console.WriteLine("      (invalid: board died out - these numbers are meaningless)");
                return 1;
            }

            return 0;
        }
    }
}
