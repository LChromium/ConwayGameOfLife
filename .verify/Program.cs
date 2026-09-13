using System;
using System.Collections.Generic;
using System.Text;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Standalone ground-truth harness. Compiles the real LifeSimulation/LifePatterns sources
    /// against net8.0 so pattern behaviour can be measured without the Unity Editor.
    /// </summary>
    internal static class Program
    {
        private const int Grid = 64;
        private const int MaxPeriodSearch = 40;

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "probe")
            {
                Probe.Run();
                return 0;
            }

            if (args.Length > 0 && args[0] == "search")
            {
                Search.Run();
                return 0;
            }

            if (args.Length > 0 && args[0] == "diagnose")
            {
                Diagnose.Run();
                return 0;
            }

            if (args.Length > 0 && args[0] == "bench")
            {
                Bench.Run();
                return 0;
            }

            if (args.Length > 0 && args[0] == "rules")
            {
                RuleCheck.Run();
                return 0;
            }

            bool allOk = true;

            // Every shipped pattern is checked against its declared kind and period.
            foreach (LifePattern pattern in LifePatterns.All)
            {
                allOk &= Report(pattern);
            }

            Console.WriteLine();
            Console.WriteLine(allOk ? "RESULT: all patterns behave as declared." : "RESULT: MISMATCHES FOUND.");
            return allOk ? 0 : 1;
        }

        private static bool Report(LifePattern pattern)
        {
            return Analyze(pattern.EnglishName, pattern.Kind, pattern.Period, pattern.Cells);
        }

        private static bool ReportCandidate(string name, LifePatternKind kind, int period, LifeCell[] cells)
        {
            return Analyze(name + " (candidate)", kind, period, cells);
        }

        private static bool Analyze(string name, LifePatternKind kind, int declaredPeriod, LifeCell[] cells)
        {
            var sim = new LifeSimulation(Grid, Grid);
            sim.LoadCentered(cells);

            int initialPopulation = sim.Population;
            string initial = Snapshot(sim);
            (int originX, int originY) = Bounds(initial, Grid, Grid);

            var seen = new Dictionary<string, int> { [initial] = 0 };
            int? detectedPeriod = null;
            int settleGeneration = 0;

            for (int step = 1; step <= MaxPeriodSearch; step++)
            {
                sim.Step();
                string now = Snapshot(sim);
                if (now == initial)
                {
                    detectedPeriod = step;
                    break;
                }

                if (!seen.ContainsKey(now))
                {
                    seen[now] = step;
                }
            }

            // For translated patterns (spaceships) the board never repeats in place,
            // so detect a pure translation by comparing trimmed masks.
            string initialMask = TrimmedMask(initial, Grid, Grid);
            int? translationPeriod = null;
            int translationDx = 0;
            int translationDy = 0;

            if (detectedPeriod == null)
            {
                var sim2 = new LifeSimulation(Grid, Grid);
                sim2.LoadCentered(cells);
                for (int step = 1; step <= MaxPeriodSearch; step++)
                {
                    sim2.Step();
                    string snapshot = Snapshot(sim2);
                    string mask = TrimmedMask(snapshot, Grid, Grid);
                    if (mask == initialMask)
                    {
                        translationPeriod = step;
                        (int dx, int dy) = Bounds(snapshot, Grid, Grid);
                        translationDx = dx - originX;
                        translationDy = dy - originY;
                        break;
                    }
                }
            }

            string verdict;
            bool ok;

            switch (kind)
            {
                case LifePatternKind.StillLife:
                    ok = detectedPeriod == 1;
                    verdict = ok
                        ? "stable from generation 1"
                        : $"UNEXPECTED: still life changed (period {(detectedPeriod?.ToString() ?? "none")})";
                    break;

                case LifePatternKind.Oscillator:
                case LifePatternKind.PeriodicOscillator:
                    // Both kinds must return to their exact initial state after exactly their
                    // declared period. They differ only in period length (2 vs >= 3).
                    ok = detectedPeriod == declaredPeriod;
                    verdict = ok
                        ? $"period {detectedPeriod} as declared"
                        : $"UNEXPECTED: declared period {declaredPeriod}, measured {detectedPeriod?.ToString() ?? "none"} (settled at gen {settleGeneration})";
                    break;

                case LifePatternKind.Spaceship:
                    ok = translationPeriod == declaredPeriod
                         && Math.Abs(translationDx) + Math.Abs(translationDy) > 0;
                    verdict = ok
                        ? $"period {translationPeriod}, translated by ({translationDx},{translationDy})"
                        : $"UNEXPECTED: declared period {declaredPeriod}, measured translation period {(translationPeriod?.ToString() ?? "none")} offset ({translationDx},{translationDy})";
                    break;

                default:
                    ok = false;
                    verdict = $"UNEXPECTED: unhandled pattern kind {kind}";
                    break;
            }

            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {name,-24} pop {initialPopulation,4}  {verdict}");
            return ok;
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

        /// <summary>Top-left corner of the live-cell bounding box; int.MaxValue when empty.</summary>
        private static (int MinX, int MinY) Bounds(string snapshot, int width, int height)
        {
            int minX = int.MaxValue, minY = int.MaxValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (snapshot[y * width + x] != '1')
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                }
            }

            return (minX, minY);
        }

        /// <summary>Relative cell mask ignoring absolute board position.</summary>
        private static string TrimmedMask(string snapshot, int width, int height)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (snapshot[y * width + x] != '1')
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    sb.Append(snapshot[y * width + x]);
                }

                sb.Append('/');
            }

            return sb.ToString();
        }
    }
}

