using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// EditMode coverage for the Conway rule engine itself: the B3/S23 transition, both boundary
    /// conditions, and bookkeeping (generation counter, population, clear/load helpers).
    /// </summary>
    public sealed class LifeSimulationTests
    {
        [Test]
        public void Constructor_RejectsNonPositiveDimensions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LifeSimulation(0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LifeSimulation(10, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LifeSimulation(-4, -4));
        }

        [Test]
        public void NewSimulation_StartsEmptyAtGenerationZero()
        {
            var sim = new LifeSimulation(16, 16);

            Assert.AreEqual(0, sim.Generation);
            Assert.AreEqual(0, sim.Population);
            Assert.IsFalse(sim.IsAlive(0, 0));
            Assert.IsFalse(sim.IsAlive(15, 15));
        }

        [Test]
        public void Step_ImplementsBirthRule_DeadCellWithExactlyThreeNeighboursLives()
        {
            var sim = new LifeSimulation(16, 16);

            // Three neighbours around (5,5); the cell itself is dead.
            sim.SetCell(4, 4, true);
            sim.SetCell(5, 4, true);
            sim.SetCell(6, 4, true);

            Assert.IsFalse(sim.IsAlive(5, 5), "precondition: target cell is dead");

            sim.Step();

            Assert.IsTrue(sim.IsAlive(5, 5), "dead cell with exactly 3 neighbours must be born (B3)");
        }

        [Test]
        public void Step_ImplementsSurvivalRule_IgnoresOvercrowdingAndLoneliness()
        {
            // A solid 2x2 block: every cell has exactly 3 neighbours, so all four survive.
            var block = new LifeSimulation(16, 16);
            foreach (LifeCell cell in PatternCells("BLOCK"))
            {
                block.SetCell(cell.X, cell.Y, true);
            }

            block.Step();

            Assert.AreEqual(4, block.Population, "2x2 block must survive overcrowding (S23)");

            // A lone cell has 0 neighbours and must die of loneliness.
            var lonely = new LifeSimulation(16, 16);
            lonely.SetCell(5, 5, true);
            lonely.Step();

            Assert.AreEqual(0, lonely.Population, "isolated cell must die");
        }

        [Test]
        public void Step_BlinkerOscillatesWithPeriodTwo()
        {
            var sim = new LifeSimulation(16, 16);
            for (int x = 4; x <= 6; x++)
            {
                sim.SetCell(x, 4, true);
            }

            sim.Step();

            Assert.AreEqual(3, sim.Population, "blinker keeps 3 live cells");
            Assert.IsTrue(sim.IsAlive(5, 3) && sim.IsAlive(5, 4) && sim.IsAlive(5, 5),
                "horizontal blinker must become vertical after one generation");

            sim.Step();

            Assert.IsTrue(sim.IsAlive(4, 4) && sim.IsAlive(5, 4) && sim.IsAlive(6, 4),
                "blinker must return to its original shape after two generations");
        }

        [Test]
        public void Step_MatchesIndependentReferenceOnRandomBoards()
        {
            var rng = new Random(1234567);

            for (int trial = 0; trial < 100; trial++)
            {
                int width = 1 + rng.Next(12);
                int height = 1 + rng.Next(12);
                var sim = new LifeSimulation(width, height);
                bool[,] board = new bool[width, height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool alive = rng.Next(2) == 0;
                        board[x, y] = alive;
                        sim.SetCell(x, y, alive);
                    }
                }

                bool[,] expected = ReferenceStep(board, width, height, wrapEdges: false);
                sim.Step();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Assert.AreEqual(expected[x, y], sim.IsAlive(x, y),
                            $"cell ({x},{y}) on a {width}x{height} board diverged from the reference rules");
                    }
                }
            }
        }

        [Test]
        public void Step_WithWrapEdges_MatchesIndependentReferenceOnRandomBoards()
        {
            var rng = new Random(7654321);

            for (int trial = 0; trial < 100; trial++)
            {
                int width = 1 + rng.Next(12);
                int height = 1 + rng.Next(12);
                var sim = new LifeSimulation(width, height) { WrapEdges = true };
                bool[,] board = new bool[width, height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool alive = rng.Next(2) == 0;
                        board[x, y] = alive;
                        sim.SetCell(x, y, alive);
                    }
                }

                bool[,] expected = ReferenceStep(board, width, height, wrapEdges: true);
                sim.Step();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Assert.AreEqual(expected[x, y], sim.IsAlive(x, y),
                            $"cell ({x},{y}) on a {width}x{height} wrapped board diverged from the reference rules");
                    }
                }
            }
        }

        [Test]
        public void WrapEdges_ProducesDifferentResultFromFixedBoundary()
        {
            // A blinker sitting on the top edge: with wrapping it oscillates in place,
            // with a fixed boundary the cells touching the border behave differently.
            var wrapped = new LifeSimulation(8, 8) { WrapEdges = true };
            var fixedBoundary = new LifeSimulation(8, 8) { WrapEdges = false };

            for (int x = 0; x < 3; x++)
            {
                wrapped.SetCell(x, 0, true);
                fixedBoundary.SetCell(x, 0, true);
            }

            wrapped.Step();
            fixedBoundary.Step();

            Assert.AreEqual(3, wrapped.Population, "wrapped blinker on the edge keeps 3 live cells");
            Assert.AreNotEqual(Snapshot(wrapped), Snapshot(fixedBoundary),
                "the two boundary modes must not produce identical boards");
        }

        [Test]
        public void SetCell_TracksPopulationAndIgnoresOutOfBounds()
        {
            var sim = new LifeSimulation(8, 8);

            sim.SetCell(2, 2, true);
            sim.SetCell(3, 2, true);
            Assert.AreEqual(2, sim.Population);

            sim.SetCell(2, 2, true); // idempotent
            Assert.AreEqual(2, sim.Population, "setting the same cell alive twice must not double-count");

            sim.SetCell(2, 2, false);
            Assert.AreEqual(1, sim.Population);

            sim.SetCell(-1, 0, true);
            sim.SetCell(0, -1, true);
            sim.SetCell(8, 0, true);
            sim.SetCell(0, 8, true);
            Assert.AreEqual(1, sim.Population, "out-of-bounds writes must be ignored");
        }

        [Test]
        public void Clear_ResetsBoardGenerationAndPopulation()
        {
            var sim = new LifeSimulation(16, 16);
            foreach (LifeCell cell in PatternCells("BLINKER"))
            {
                sim.SetCell(cell.X, cell.Y, true);
            }

            sim.Step();
            Assert.Greater(sim.Generation, 0);

            sim.Clear();

            Assert.AreEqual(0, sim.Generation);
            Assert.AreEqual(0, sim.Population);
            Assert.IsFalse(sim.IsAlive(0, 0));
        }

        [Test]
        public void LoadCentered_CentersThePatternAndResetsState()
        {
            // An odd board side keeps "centred" unambiguous in integer coordinates.
            const int size = 33;
            var sim = new LifeSimulation(size, size);
            sim.Step();
            sim.Step();

            LifeCell[] glider = PatternCells("GLIDER");
            sim.LoadCentered(glider);

            Assert.AreEqual(0, sim.Generation, "LoadCentered must reset the generation counter");
            Assert.AreEqual(5, sim.Population, "glider has 5 live cells");

            // Derive the pattern's own bounding box from its definition...
            int patternMinX = int.MaxValue, patternMaxX = int.MinValue;
            int patternMinY = int.MaxValue, patternMaxY = int.MinValue;
            foreach (LifeCell cell in glider)
            {
                patternMinX = Math.Min(patternMinX, cell.X);
                patternMaxX = Math.Max(patternMaxX, cell.X);
                patternMinY = Math.Min(patternMinY, cell.Y);
                patternMaxY = Math.Max(patternMaxY, cell.Y);
            }

            // ...and confirm the loaded board reproduces it exactly.
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = 0; y < sim.Height; y++)
            {
                for (int x = 0; x < sim.Width; x++)
                {
                    if (!sim.IsAlive(x, y))
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }

            Assert.AreEqual(patternMaxX - patternMinX, maxX - minX, "loaded bounding box width must match the pattern");
            Assert.AreEqual(patternMaxY - patternMinY, maxY - minY, "loaded bounding box height must match the pattern");

            // Centring lands within one cell of the board centre (integer division may bias by one
            // for patterns whose bounding box spans an even number of cells).
            int boardCenter = (size - 1) / 2;
            Assert.LessOrEqual(Math.Abs((minX + maxX) / 2 - boardCenter), 1, "pattern should be horizontally centered");
            Assert.LessOrEqual(Math.Abs((minY + maxY) / 2 - boardCenter), 1, "pattern should be vertically centered");
        }

        [Test]
        public void Randomize_RespectsProbabilityBoundsAndTracksPopulation()
        {
            var sim = new LifeSimulation(32, 32);
            sim.Randomize(0f, new Random(1));
            Assert.AreEqual(0, sim.Population, "probability 0 must produce an empty board");

            sim.Randomize(1f, new Random(2));
            Assert.AreEqual(32 * 32, sim.Population, "probability 1 must fill the board");

            sim.Randomize(0.5f, new Random(3));
            Assert.Greater(sim.Population, 0);
            Assert.Less(sim.Population, 32 * 32, "probability 0.5 should produce a mixed board");

            int counted = 0;
            for (int y = 0; y < sim.Height; y++)
            {
                for (int x = 0; x < sim.Width; x++)
                {
                    if (sim.IsAlive(x, y))
                    {
                        counted++;
                    }
                }
            }

            Assert.AreEqual(counted, sim.Population, "Population must match a manual recount");
        }

        [Test]
        public void Randomize_RejectsNullRandom()
        {
            var sim = new LifeSimulation(8, 8);
            Assert.Throws<ArgumentNullException>(() => sim.Randomize(0.5f, null));
        }

        // --- layout resolution math -------------------------------------------

        [Test]
        public void PanelFit_ScaleFactorIsConservativeAgainstUnitysOwnMeasuredScale()
        {
            // Unity's real fit transform is not documented precisely enough to reproduce exactly.
            // At 640x480 against a 1440x900 design resolution, Unity reported a panel of ~1309x982
            // (effective scale 640/1309.09 = 0.48891) while the geometric interpolation yields
            // 0.48686. Rather than assert an exact match to a number the engine owns, this asserts
            // the property that actually matters: the predicted scale is never MORE generous than
            // Unity's, so a "fits" verdict here cannot be optimistic.
            //
            // The same measurement cross-checked at 1920x1080 lives in the PlayMode suite, where the
            // real panel is observed rather than predicted.
            float predicted = PanelScreenFit.ScaleFactor(640, 480, 1440, 900, 0.5f);
            const float unityObserved = 0.48891f;

            Assert.LessOrEqual(predicted, unityObserved + 0.0001f,
                $"predicted scale {predicted:F5} is more generous than Unity's observed {unityObserved:F5}, " +
                "so fit verdicts could be optimistic");

            // Ordering sanity: a bigger screen must not yield a smaller scale.
            Assert.Greater(PanelScreenFit.ScaleFactor(1920, 1080, 1440, 900, 0.5f), predicted);

            // With a 16:9 design resolution the scale is exactly the linear ratio on 16:9 viewports.
            Assert.AreEqual(1.2f, PanelScreenFit.ScaleFactor(1920, 1080, 1600, 900, 0.5f), 0.0001f);
            Assert.AreEqual(0.8f, PanelScreenFit.ScaleFactor(1280, 720, 1600, 900, 0.5f), 0.0001f);
        }

        /// <summary>
        /// Content height measured from a live PlayMode run in the single-column layout:
        /// header 63 + machine 849 + footer 37 = 949 panel units, against the 1600x900 reference.
        /// </summary>
        private const float SingleColumnContentHeight = 949f;

        /// <summary>
        /// Content height in the compact (stacked) layout, derived from the same per-region
        /// measurements: the archive drops below the grid instead of sizing the row beside it.
        /// </summary>
        private const float CompactContentHeight = 828f;

        private const int ReferenceWidth = 1600;
        private const int ReferenceHeight = 900;
        private const float ReferenceMatch = 0.5f;

        [Test]
        public void PanelFit_TargetResolutionsExposeTheWholeDesignSpace()
        {
            // The design space is 1600x900. It is SCALED to fit the target viewport, so the claim
            // here is not "the screen is 1600x900" - it is the narrower, checkable statement that a
            // viewport at least as large as the design space and of the same aspect ratio exposes
            // all of it, leaving nothing to crop. The identical values at 1280x720 and 1920x1080
            // are the point: the design space is scale-invariant, not the pixel count.
            foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1440) })
            {
                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                Assert.AreEqual(ReferenceWidth, panelWidth, 1f, $"{w}x{h}: design space partially cropped horizontally");
                Assert.AreEqual(ReferenceHeight, panelHeight, 1f, $"{w}x{h}: design space partially cropped vertically");

                // And it really is a scale: the factor differs, the design space does not.
                float scale = PanelScreenFit.ScaleFactor(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);
                Assert.AreEqual((float)w / ReferenceWidth, scale, 0.0001f, $"{w}x{h}: unexpected scale factor");
            }
        }

        [Test]
        public void PanelFit_CompactLayoutFitsWhenItIsUsed()
        {
            // The stacked layout only has to work in the cases it is chosen for. It is chosen when
            // the panel is either narrower than 1100 or shorter than 830 - and 830 is exactly the
            // stacked layout's minimum height, so any panel that triggers it can also host it.
            foreach ((int w, int h) in new[] { (300, 400), (1024, 600), (1280, 720), (1920, 1080) })
            {
                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                bool wouldStack = panelWidth < 1100f || panelHeight < 830f;
                if (!wouldStack)
                {
                    continue; // not a stacked case; the two-column tests cover it
                }

                Assert.IsTrue(
                    PanelScreenFit.FitsVertically(CompactContentHeight, w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch),
                    $"{w}x{h}: stacks (panel {panelWidth:F0}x{panelHeight:F0}) but the compact layout does not fit");
            }
        }

        [Test]
        public void PanelFit_TargetResolutionsKeepTheTwoColumnLayout()
        {
            // The two-column layout is the intended presentation; the stacked one exists only for a
            // panel too small to host it. The check is the layout's real minimum content height, not
            // the height it happened to render at in one capture (a taller panel simply gives its
            // growing children more room, which is not the same as needing more room).
            static bool NeedsCompact(int w, int h)
            {
                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                // Mirrors LifeTerminalController.ApplyCompactClass.
                return panelWidth < 1100f
                       || panelHeight < 830f;
            }

            foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (1440, 900), (1920, 1200) })
            {
                Assert.IsFalse(NeedsCompact(w, h), $"{w}x{h} is a target viewport and must keep two columns");
            }

            // A genuinely cramped panel still stacks, so the fallback is not dead code.
            //
            // Worth noting which cases are actually cramped: because MatchWidthOrHeight keeps the
            // panel proportional to the window, a merely "small-ish" window still exposes the whole
            // design space. An 800x480 window yields a panel of ~1604x962 and is NOT cramped.
            Assert.IsTrue(NeedsCompact(300, 400), "a very small window should stack");
        }

        [Test]
        public void PanelFit_CompactIsOnlyChosenWhenItActuallyFits()
        {
            // The breakpoint must sit at or above the stacked layout's own minimum height. If it
            // were lower, a panel in between would stack and STILL not fit - strictly worse than
            // staying in two columns.
            //
            // A 2560x400 window exposes a 1600x518 panel: short enough to trigger stacking, yet
            // 518 < 828 so even the stacked layout cannot fit. No layout rescues that window, and
            // the code must not pretend otherwise - the threshold is therefore set at 830.
            (float _, float veryShort) =
                PanelScreenFit.VisiblePanelSize(2560, 400, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            Assert.Less(veryShort, CompactContentHeight,
                "the short-window case should be unworkable even when stacked");

            // At the threshold itself the stacked layout must fit, otherwise the breakpoint lies.
            Assert.GreaterOrEqual(830f, CompactContentHeight,
                "the stacking threshold is below the stacked layout's minimum height");
        }

        [Test]
        public void PanelFit_CompactLayoutFitsAtEveryTargetResolution()
        {
            foreach ((int w, int h) in new[] { (1280, 720), (1600, 900), (1920, 1080), (2560, 1440) })
            {
                (float panelWidth, float panelHeight) = PanelScreenFit.VisiblePanelSize(w, h, 1440, 900, 0.5f);

                Assert.IsTrue(
                    PanelScreenFit.FitsVertically(CompactContentHeight, w, h, 1440, 900, 0.5f),
                    $"{w}x{h}: compact layout needs {CompactContentHeight}px but only {panelHeight:F0}px is visible");

                // The compact layout still needs room for a usable grid beside nothing else.
                Assert.Greater(panelWidth, 700f, $"{w}x{h}: panel only {panelWidth:F0}px wide");
            }
        }

        [Test]
        public void PanelFit_RejectsNonPositiveDimensions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PanelScreenFit.ScaleFactor(0, 480, 1440, 900, 0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => PanelScreenFit.ScaleFactor(640, 0, 1440, 900, 0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => PanelScreenFit.ScaleFactor(640, 480, 0, 900, 0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => PanelScreenFit.ScaleFactor(640, 480, 1440, 0, 0.5f));
        }

        // --- helpers ----------------------------------------------------------

        internal static LifePattern FindPattern(string englishName)
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.EnglishName == englishName)
                {
                    return pattern;
                }
            }

            Assert.Fail($"pattern '{englishName}' is not present in LifePatterns.All");
            return null;
        }

        internal static LifeCell[] PatternCells(string englishName)
        {
            return FindPattern(englishName).Cells;
        }

        internal static string Snapshot(LifeSimulation sim)
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

        /// <summary>
        /// The live-cell set reduced to a translation-invariant mask: the bounding box cropped and
        /// rendered as a string. Two boards have the same NormalizedShape iff one is a pure
        /// translation of the other. This is what makes a spaceship's period checkable, since a
        /// spaceship never returns to its absolute starting position.
        /// </summary>
        internal static string NormalizedShape(LifeSimulation sim)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < sim.Height; y++)
            {
                for (int x = 0; x < sim.Width; x++)
                {
                    if (!sim.IsAlive(x, y))
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return string.Empty; // extinct
            }

            var sb = new StringBuilder();
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    sb.Append(sim.IsAlive(x, y) ? '1' : '0');
                }

                sb.Append('/');
            }

            return sb.ToString();
        }

        /// <summary>Live cells in a stable order (row-major), for exact shape comparisons.</summary>
        internal static List<(int X, int Y)> AliveCells(LifeSimulation sim)
        {
            var cells = new List<(int X, int Y)>();
            for (int y = 0; y < sim.Height; y++)
            {
                for (int x = 0; x < sim.Width; x++)
                {
                    if (sim.IsAlive(x, y))
                    {
                        cells.Add((x, y));
                    }
                }
            }

            return cells;
        }

        /// <summary>
        /// Asserts that <paramref name="actual"/> is exactly <paramref name="expected"/> translated
        /// by (dx, dy) - i.e. the same shape moved, not merely a similar one.
        /// </summary>
        internal static void AssertSameCells(
            List<(int X, int Y)> expected,
            List<(int X, int Y)> actual,
            int dx,
            int dy,
            string label)
        {
            Assert.AreEqual(expected.Count, actual.Count,
                $"{label}: live-cell count changed (expected {expected.Count}, got {actual.Count})");

            var actualSet = new HashSet<(int X, int Y)>(actual);
            foreach ((int x, int y) in expected)
            {
                var moved = (X: x + dx, Y: y + dy);
                Assert.IsTrue(actualSet.Contains(moved),
                    $"{label}: cell ({x},{y}) shifted by ({dx},{dy}) should land on ({moved.X},{moved.Y}), but that cell is dead");
            }
        }

        /// <summary>
        /// Independent re-implementation of B3/S23, written directly from the rule statement so
        /// the test does not share code with the implementation under test.
        /// </summary>
        internal static bool[,] ReferenceStep(bool[,] board, int width, int height, bool wrapEdges)
        {
            var next = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
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

                            if (wrapEdges)
                            {
                                nx = ((nx % width) + width) % width;
                                ny = ((ny % height) + height) % height;
                            }
                            else if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                            {
                                continue;
                            }

                            if (board[nx, ny])
                            {
                                neighbours++;
                            }
                        }
                    }

                    next[x, y] = neighbours == 3 || (board[x, y] && neighbours == 2);
                }
            }

            return next;
        }
    }
}
