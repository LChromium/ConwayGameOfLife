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
        public static void Run()
        {
            Console.WriteLine("LifeSimulation throughput (Release, single core)");
            Console.WriteLine($"{"board",-14}{"wrap",-8}{"gens",-9}{"total ms",-11}{"ms/gen",-11}{"cells/s",-16}{"gens/s"}");

            foreach ((int W, int H) in new[] { (96, 64), (256, 256), (512, 512), (1024, 1024) })
            {
                foreach (bool wrap in new[] { false, true })
                {
                    Measure(W, H, wrap);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Rule-application floor: one Step() performs W*H cell updates,");
            Console.WriteLine("each reading 8 neighbours, so cells/s is the comparable figure.");
        }

        private static void Measure(int width, int height, bool wrap)
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

            // Keep the population realistic so later runs are not measuring an empty board.
            if (sim.Population == 0)
            {
                Console.WriteLine("      (warning: board died out)");
            }
        }
    }
}
