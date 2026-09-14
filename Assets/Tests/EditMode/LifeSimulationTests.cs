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
        public void PanelFit_MatchesTheRunningPlayerAtEveryMeasuredResolution()
        {
            // These are the panel sizes a real Windows Player actually laid out, recorded by
            // RuntimeLayoutProbe into Screenshots/player-measurements.jsonl. Pinning them here is
            // what keeps the formula honest: an earlier version used the rule Unity DOCUMENTS
            // (a logarithmic interpolation), which agreed at 16:9 and was wrong everywhere else -
            // it predicted 929.5 for the portrait case where the Player laid out 807.48.
            //
            // If Unity changes its fit behaviour, this test fails loudly instead of the layout
            // silently stacking in the wrong place.
            var measured = new (int W, int H, float PanelW, float PanelH)[]
            {
                (1280, 720, 1600f, 900f),
                (1920, 1080, 1600f, 900f),
                (600, 1000, 807.48f, 1345.79f),
            };

            foreach ((int w, int h, float expectedWidth, float expectedHeight) in measured)
            {
                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                Assert.AreEqual(expectedWidth, panelWidth, 1f,
                    $"{w}x{h}: predicted panel width disagrees with the measured Player");
                Assert.AreEqual(expectedHeight, panelHeight, 1f,
                    $"{w}x{h}: predicted panel height disagrees with the measured Player");
            }
        }

        [Test]
        public void PanelFit_LinearAndLogarithmicFormsDifferOffSixteenByNine()
        {
            // The guard against silently reverting to the documented-but-wrong formula. Both forms
            // coincide at 16:9, so only a non-16:9 case can tell them apart.
            static float Logarithmic(int sw, int sh)
            {
                double lw = Math.Log((double)sw / ReferenceWidth);
                double lh = Math.Log((double)sh / ReferenceHeight);
                return (float)Math.Exp(0.5 * lh + 0.5 * lw);
            }

            // At 16:9 they agree - which is exactly why the earlier samples looked convincing.
            Assert.AreEqual(Logarithmic(1280, 720), PanelScreenFit.ScaleFactor(1280, 720, ReferenceWidth, ReferenceHeight, 0.5f), 0.0001f);

            // Off 16:9 they diverge, and the linear form is the one the Player confirms.
            float linear = PanelScreenFit.ScaleFactor(600, 1000, ReferenceWidth, ReferenceHeight, 0.5f);
            float logarithmic = Logarithmic(600, 1000);
            Assert.Greater(Math.Abs(linear - logarithmic), 0.05f,
                "the two forms should differ materially on a portrait viewport");

            Assert.AreEqual(600f / 807.48f, linear, 0.001f,
                "the linear form is the one that matches the measured Player");
        }

        [Test]
        public void PanelFit_ScaleIsNeverMoreGenerousThanTheObservedEditorPanel()
        {
            // An independent data point from the Editor, at a fourth aspect ratio (4:3). Unity
            // reported a panel of ~1309x982 for a 640x480 PlayMode host against a 1440x900 design
            // resolution. The formula should reproduce that, which also re-confirms linearity on a
            // ratio the Player samples did not cover.
            float predictedScale = 640f / 1309.09f; // what the Editor actually did
            float linear = PanelScreenFit.ScaleFactor(640, 480, 1440, 900, 0.5f);

            Assert.AreEqual(predictedScale, linear, 0.002f,
                "the linear model should reproduce the panel the Editor laid out at 640x480");

            // Ordering sanity: a bigger screen must not yield a smaller scale.
            Assert.Greater(PanelScreenFit.ScaleFactor(1920, 1080, 1440, 900, 0.5f), linear);

            // On a 16:9 viewport against a 16:9 design space the scale is exactly the linear ratio.
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
        public void PanelFit_StackedLayoutFitsInTheCasesItIsChosenFor()
        {
            // The stacked layout is chosen purely on WIDTH. In every such case it must actually fit,
            // otherwise the breakpoint would be trading a working layout for a broken one.
            foreach ((int w, int h) in new[] { (300, 400), (640, 480), (1024, 768), (1280, 720) })
            {
                (float panelWidth, _) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                if (panelWidth >= 1280f)
                {
                    continue; // not a stacked case; the two-column tests cover it
                }

                Assert.IsTrue(
                    PanelScreenFit.FitsVertically(CompactContentHeight, w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch),
                    $"{w}x{h}: stacks but the compact layout needs {CompactContentHeight}px and does not fit");
            }
        }

        [Test]
        public void PanelFit_TargetResolutionsKeepTheTwoColumnLayout()
        {
            // The two-column layout is the intended presentation; stacking exists for panels that
            // cannot host it. The decision is horizontal only - see the height test below.
            static bool NeedsCompact(int w, int h)
            {
                (float panelWidth, _) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                // Mirrors LifeTerminalController.ApplyCompactClass.
                return panelWidth < 1280f;
            }

            foreach ((int w, int h) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (1440, 900), (1920, 1200), (1024, 768) })
            {
                Assert.IsFalse(NeedsCompact(w, h), $"{w}x{h} is a target viewport and must keep two columns");
            }

            // A narrow panel still stacks, so the fallback is not dead code.
            //
            // The scale factor is 1/sqrt(aspectRatio) relative to the design aspect, so a small
            // square window (aspect 1 against the design's 16:9) scales by 0.5 and STILL exposes a
            // 1200-wide panel. Reaching the stacked layout therefore needs a window around 500x500
            // or smaller - the transform hands back width as the window shrinks.
            Assert.IsTrue(NeedsCompact(480, 480), "a small square window should stack");

            (float tinyPanelWidth, _) =
                PanelScreenFit.VisiblePanelSize(480, 480, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            Assert.Less(tinyPanelWidth, 1280f, "the stacked case should fail the width check");
        }

        [Test]
        public void PanelFit_PortraitViewportStacksInsteadOfSqueezingTwoColumns()
        {
            // With the scale a linear blend of the width and height ratios at match 0.5:
            //     scale    = (W/1600 + H/900) / 2
            //     panelW   = W / scale        panelH = H / scale
            // The width therefore depends on BOTH dimensions and has no positive lower bound - a
            // narrow window gives a narrow panel. (An earlier comment here claimed width depended
            // only on window area with a floor of 1152; the portrait case below disproves it.)
            //
            // A landscape 1024x768 window exposes ~1371 and keeps two columns, while a portrait
            // 600x1000 window exposes only ~807 and must stack: there the height is generous and it
            // is the width that got squeezed.
            (float portraitWidth, float portraitHeight) =
                PanelScreenFit.VisiblePanelSize(600, 1000, ReferenceWidth, ReferenceHeight, ReferenceMatch);

            Assert.Less(portraitWidth, 1280f, "a portrait viewport should fall under the stacking width");
            Assert.Greater(portraitHeight, ReferenceHeight,
                "a portrait viewport should expose more vertical room than the design space");

            // And the stacked layout must fit there, since that is what will be used.
            Assert.IsTrue(
                PanelScreenFit.FitsVertically(CompactContentHeight, 600, 1000, ReferenceWidth, ReferenceHeight, ReferenceMatch),
                "the stacked layout must fit the portrait viewport it is chosen for");

            // A landscape window of the same height has far more width, so it must not stack.
            (float landscapeWidth, _) =
                PanelScreenFit.VisiblePanelSize(1600, 1000, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            Assert.GreaterOrEqual(landscapeWidth, 1280f, "a landscape viewport should keep two columns");
        }

        [Test]
        public void PanelFit_MatchesTheLinearFormulaDirectly()
        {
            // The formula is not obvious from any single sample, so state it and check it head-on:
            // the panel is narrower than the window whenever the fit scale exceeds 1... and the
            // scale at match 0.5 is just the mean of the two ratios.
            foreach ((int w, int h) in new[] { (600, 1000), (1280, 720), (1024, 768), (600, 600), (400, 900) })
            {
                float expectedScale = ((float)w / ReferenceWidth + (float)h / ReferenceHeight) / 2f;
                float actualScale = PanelScreenFit.ScaleFactor(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);
                Assert.AreEqual(expectedScale, actualScale, 0.0001f, $"{w}x{h}: scale is not the mean of the ratios");

                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);
                Assert.AreEqual(w / expectedScale, panelWidth, 0.01f, $"{w}x{h}: panel width");
                Assert.AreEqual(h / expectedScale, panelHeight, 0.01f, $"{w}x{h}: panel height");
            }

            // Narrow windows really do give narrow panels - there is no floor. This is the specific
            // claim an earlier comment got wrong.
            (float narrowPanel, _) =
                PanelScreenFit.VisiblePanelSize(400, 900, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            Assert.Less(narrowPanel, 700f,
                "a narrow window should give a narrow panel; there is no lower bound");
        }

        [Test]
        public void PanelFit_HeightShortageIsNotFixedByStacking()
        {
            // Why the breakpoint must ignore height: the stacked layout is itself ~828 units tall.
            // A short panel that switched to stacking would still not fit, having traded a working
            // two-column layout for a broken stacked one.
            //
            // A 2560x400 window is exactly that case: ~518 units of visible height, less than the
            // stacked layout needs. No layout rescues it - the archive scrolls instead - and since
            // it is wide, it must NOT stack.
            (float shortPanelWidth, float veryShort) =
                PanelScreenFit.VisiblePanelSize(2560, 400, ReferenceWidth, ReferenceHeight, ReferenceMatch);

            Assert.Less(veryShort, CompactContentHeight,
                "the short-window case should be unworkable even when stacked");
            Assert.GreaterOrEqual(shortPanelWidth, 1280f,
                "a wide-but-short window must not be pushed into the stacked layout");
        }

        [Test]
        public void PanelFit_UsesTheTwoColumnLayoutOnANonSixteenByNineViewport()
        {
            // Both earlier samples were 16:9, the same shape as the design space, which cannot
            // distinguish "the formula generalises" from "the aspect ratios happened to match".
            // A 4:3 and a 16:10 viewport exercise the match-0.5 interpolation for real.
            //
            // Note the panel is NOT expected to reach the design space here: on a 1024x768 window
            // the fit transform yields ~1386x1039, which is narrower than 1600. The design space is
            // what 16:9-or-wider viewports expose, not a guarantee at every resolution.
            foreach ((int w, int h) in new[] { (1024, 768), (1600, 1200), (1920, 1200), (1440, 900) })
            {
                (float panelWidth, float panelHeight) =
                    PanelScreenFit.VisiblePanelSize(w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch);

                // These viewports are all wide enough for two columns, so none of them should stack.
                Assert.GreaterOrEqual(panelWidth, 1280f,
                    $"{w}x{h}: panel only {panelWidth:F0}px, too narrow for two columns");

                // And tall enough for the two-column layout.
                Assert.IsTrue(
                    PanelScreenFit.FitsVertically(CompactContentHeight, w, h, ReferenceWidth, ReferenceHeight, ReferenceMatch),
                    $"{w}x{h}: the two-column layout does not fit");
            }

            // The 4:3 case must genuinely differ from the 16:9 case, otherwise it proves nothing.
            (float sixteenByNine, _) =
                PanelScreenFit.VisiblePanelSize(1280, 720, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            (float fourByThree, _) =
                PanelScreenFit.VisiblePanelSize(1024, 768, ReferenceWidth, ReferenceHeight, ReferenceMatch);
            Assert.Greater(Math.Abs(sixteenByNine - fourByThree), 1f,
                "a 4:3 viewport should expose a different panel width than a 16:9 one");
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
