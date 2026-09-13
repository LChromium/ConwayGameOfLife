using System;
using System.Collections.Generic;
using System.Text;
using ConwayGameOfLife;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Validates LifeSimulation.Step() against independently computed Conway rules on
    /// random boards, plus a hand-traced blinker. Catches engine-level defects.
    /// </summary>
    internal static class RuleCheck
    {
        public static void Run()
        {
            int failures = 0;
            failures += CheckBlinker();
            failures += CheckRulesAgainstReference();
            failures += CheckWrapEdgesAgainstReference();

            Console.WriteLine(failures == 0 ? "ENGINE: rules verified." : $"ENGINE: {failures} FAILURE(S).");
        }

        private static int CheckBlinker()
        {
            var sim = new LifeSimulation(16, 16);

            // Horizontal blinker at (4,4),(5,4),(6,4).
            for (int x = 4; x <= 6; x++)
            {
                sim.SetCell(x, 4, true);
            }

            sim.Step();
            bool vertical = sim.IsAlive(5, 3) && sim.IsAlive(5, 4) && sim.IsAlive(5, 5);
            int popAfterOne = sim.Population;
            sim.Step();
            bool horizontal = sim.IsAlive(4, 4) && sim.IsAlive(5, 4) && sim.IsAlive(6, 4);

            bool ok = vertical && horizontal && popAfterOne == 3;
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] blinker  gen1 vertical={vertical} pop={popAfterOne}  gen2 horizontal={horizontal}");
            return ok ? 0 : 1;
        }

        private static int CheckRulesAgainstReference()
        {
            var rng = new Random(20250910);
            int mismatches = 0;

            for (int trial = 0; trial < 200 && mismatches < 5; trial++)
            {
                int w = 1 + rng.Next(9);
                int h = 1 + rng.Next(9);
                var sim = new LifeSimulation(w, h);
                var board = new bool[w, h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool alive = rng.Next(2) == 0;
                        board[x, y] = alive;
                        sim.SetCell(x, y, alive);
                    }
                }

                bool[,] expected = Reference(board, w, h, wrap: false);
                sim.Step();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (sim.IsAlive(x, y) != expected[x, y])
                        {
                            mismatches++;
                            Console.WriteLine($"      mismatch at ({x},{y}) size {w}x{h}: sim={sim.IsAlive(x, y)} ref={expected[x, y]}");
                        }
                    }
                }
            }

            bool ok = mismatches == 0;
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] B3/S23 fixed-boundary vs reference on 200 random boards (mismatches={mismatches})");
            return ok ? 0 : 1;
        }

        private static int CheckWrapEdgesAgainstReference()
        {
            var rng = new Random(77001);
            int mismatches = 0;

            for (int trial = 0; trial < 200 && mismatches < 5; trial++)
            {
                int w = 1 + rng.Next(9);
                int h = 1 + rng.Next(9);
                var sim = new LifeSimulation(w, h) { WrapEdges = true };
                var board = new bool[w, h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool alive = rng.Next(2) == 0;
                        board[x, y] = alive;
                        sim.SetCell(x, y, alive);
                    }
                }

                bool[,] expected = Reference(board, w, h, wrap: true);
                sim.Step();

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (sim.IsAlive(x, y) != expected[x, y])
                        {
                            mismatches++;
                            Console.WriteLine($"      mismatch at ({x},{y}) size {w}x{h}: sim={sim.IsAlive(x, y)} ref={expected[x, y]}");
                        }
                    }
                }
            }

            bool ok = mismatches == 0;
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] B3/S23 toroidal-boundary vs reference on 200 random boards (mismatches={mismatches})");
            return ok ? 0 : 1;
        }

        /// <summary>Independent implementation of Conway's rules, written from the definition.</summary>
        private static bool[,] Reference(bool[,] board, int w, int h, bool wrap)
        {
            var next = new bool[w, h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int neighbours = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            int nx = x + dx;
                            int ny = y + dy;

                            if (wrap)
                            {
                                nx = ((nx % w) + w) % w;
                                ny = ((ny % h) + h) % h;
                            }
                            else if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                            {
                                continue;
                            }

                            if (board[nx, ny])
                            {
                                neighbours++;
                            }
                        }
                    }

                    bool alive = board[x, y];
                    next[x, y] = neighbours == 3 || (alive && neighbours == 2);
                }
            }

            return next;
        }
    }
}
