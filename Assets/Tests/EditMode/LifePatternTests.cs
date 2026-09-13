using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Verifies that every entry in <see cref="LifePatterns.All"/> actually behaves the way its
    /// <see cref="LifePatternKind"/> and <see cref="LifePattern.Period"/> claim.
    ///
    /// This is the machine-checkable form of the assignment's core requirement:
    /// at least two still lifes, two oscillators, and two longer-period oscillators.
    /// </summary>
    public sealed class LifePatternTests
    {
        private const int BoardWidth = 64;
        private const int BoardHeight = 64;

        [Test]
        public void Archive_CoversEveryRequiredCategory()
        {
            int still = 0, oscillator = 0, periodic = 0, spaceship = 0;
            foreach (LifePattern pattern in LifePatterns.All)
            {
                switch (pattern.Kind)
                {
                    case LifePatternKind.StillLife: still++; break;
                    case LifePatternKind.Oscillator: oscillator++; break;
                    case LifePatternKind.PeriodicOscillator: periodic++; break;
                    case LifePatternKind.Spaceship: spaceship++; break;
                }
            }

            Assert.GreaterOrEqual(still, 2, "need at least two still lifes (稳定状态)");
            Assert.GreaterOrEqual(oscillator, 2, "need at least two oscillators (振荡状态)");
            Assert.GreaterOrEqual(periodic, 2, "need at least two longer-period oscillators (循环震荡)");
            Assert.GreaterOrEqual(spaceship, 2, "need at least two spaceships");
        }

        [Test]
        public void Archive_HasUniqueEnglishNames()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (LifePattern pattern in LifePatterns.All)
            {
                Assert.IsTrue(seen.Add(pattern.EnglishName), $"duplicate pattern name '{pattern.EnglishName}'");
            }
        }

        [Test]
        public void EveryPattern_HasCellsAndConsistentPeriod()
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                Assert.Greater(pattern.Cells.Length, 0, $"{pattern.EnglishName} has no cells");

                Assert.GreaterOrEqual(pattern.Period, 1, $"{pattern.EnglishName} has a non-positive period");

                if (pattern.Kind == LifePatternKind.StillLife)
                {
                    Assert.AreEqual(1, pattern.Period, $"{pattern.EnglishName} is a still life and must have period 1");
                }

                if (pattern.Kind == LifePatternKind.Oscillator)
                {
                    Assert.AreEqual(2, pattern.Period, $"{pattern.EnglishName} is a plain oscillator and must have period 2");
                }

                if (pattern.Kind == LifePatternKind.PeriodicOscillator)
                {
                    Assert.GreaterOrEqual(pattern.Period, 3,
                        $"{pattern.EnglishName} is a longer-period oscillator and must have period >= 3");
                }
            }
        }

        [Test]
        public void StillLifes_AreCompletelyUnchangedByEveryGeneration()
        {
            int checkedCount = 0;
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.StillLife)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                string initial = LifeSimulationTests.Snapshot(sim);
                int initialPopulation = sim.Population;

                for (int generation = 1; generation <= 4; generation++)
                {
                    sim.Step();
                    Assert.AreEqual(initial, LifeSimulationTests.Snapshot(sim),
                        $"{pattern.EnglishName} must be stable, but it changed at generation {generation}");
                    Assert.AreEqual(initialPopulation, sim.Population,
                        $"{pattern.EnglishName} population changed at generation {generation}");
                }

                checkedCount++;
            }

            Assert.GreaterOrEqual(checkedCount, 2, "expected at least two still lifes");
        }

        [Test]
        public void Oscillators_ReturnToTheirExactShapeAfterTheirPeriod()
        {
            int checkedCount = 0;
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.Oscillator)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                string initial = LifeSimulationTests.Snapshot(sim);

                for (int generation = 1; generation < pattern.Period; generation++)
                {
                    sim.Step();
                    Assert.AreNotEqual(initial, LifeSimulationTests.Snapshot(sim),
                        $"{pattern.EnglishName} matched its initial state too early, at generation {generation}");
                }

                sim.Step();
                Assert.AreEqual(initial, LifeSimulationTests.Snapshot(sim),
                    $"{pattern.EnglishName} must return to its initial state after exactly {pattern.Period} generations");

                checkedCount++;
            }

            Assert.GreaterOrEqual(checkedCount, 2, "expected at least two period-2 oscillators");
        }

        [Test]
        public void PeriodicOscillators_ReturnToTheirExactShapeAfterTheirPeriod()
        {
            int checkedCount = 0;
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.PeriodicOscillator)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                string initial = LifeSimulationTests.Snapshot(sim);

                for (int generation = 1; generation < pattern.Period; generation++)
                {
                    sim.Step();
                    Assert.AreNotEqual(initial, LifeSimulationTests.Snapshot(sim),
                        $"{pattern.EnglishName} matched its initial state too early, at generation {generation}");
                }

                sim.Step();
                Assert.AreEqual(initial, LifeSimulationTests.Snapshot(sim),
                    $"{pattern.EnglishName} must return to its initial state after exactly {pattern.Period} generations");

                checkedCount++;
            }

            Assert.GreaterOrEqual(checkedCount, 2, "expected at least two longer-period oscillators");
        }

        [Test]
        public void EveryNonStillLife_DoesNotRepeatBeforeItsDeclaredPeriod()
        {
            // "Period N" means the state is identical at generation N and at no earlier generation.
            // Without this, a pattern that never changes could satisfy a loose "returns after N" check.
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind == LifePatternKind.StillLife || pattern.Kind == LifePatternKind.Spaceship)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                string initial = LifeSimulationTests.Snapshot(sim);

                for (int generation = 1; generation < pattern.Period; generation++)
                {
                    sim.Step();
                    Assert.AreNotEqual(initial, LifeSimulationTests.Snapshot(sim),
                        $"{pattern.EnglishName} matched its initial state too early, at generation {generation}");
                }

                sim.Step();
                Assert.AreEqual(initial, LifeSimulationTests.Snapshot(sim),
                    $"{pattern.EnglishName} must return to its initial state after exactly {pattern.Period} generations");
            }
        }

        [Test]
        public void Spaceships_TranslateTheirWholeShapeByAConsistentOffset()
        {
            int checkedCount = 0;

            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.Spaceship)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                int initialPopulation = sim.Population;
                List<(int X, int Y)> initial = LifeSimulationTests.AliveCells(sim);

                for (int i = 0; i < pattern.Period; i++)
                {
                    sim.Step();
                }

                // The complete set of live cells must have moved by one single offset. Comparing the
                // full set (not just the bounding box) rejects a pattern that merely changes shape.
                Assert.AreEqual(initialPopulation, sim.Population,
                    $"{pattern.EnglishName} should keep its cell count while travelling");

                List<(int X, int Y)> after = LifeSimulationTests.AliveCells(sim);
                Assert.AreEqual(initial.Count, after.Count,
                    $"{pattern.EnglishName} changed its live-cell count");

                int dx = after[0].X - initial[0].X;
                int dy = after[0].Y - initial[0].Y;
                Assert.AreNotEqual(0, Math.Abs(dx) + Math.Abs(dy),
                    $"{pattern.EnglishName} did not move, so it is not a spaceship");

                LifeSimulationTests.AssertSameCells(initial, after, dx, dy, pattern.EnglishName);

                // Every period afterwards must repeat the same displacement.
                for (int i = 0; i < pattern.Period; i++)
                {
                    sim.Step();
                }

                LifeSimulationTests.AssertSameCells(initial, LifeSimulationTests.AliveCells(sim), dx * 2, dy * 2,
                    pattern.EnglishName);

                checkedCount++;
            }

            Assert.GreaterOrEqual(checkedCount, 2, "expected at least two spaceships");
        }

        [Test]
        public void Spaceships_DoNotRepeatInPlaceAtAnySmallerPeriod()
        {
            // A spaceship returns to its own shape only after translating; it must never return to
            // the identical absolute position at a period shorter than the declared one.
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.Spaceship)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                string initial = LifeSimulationTests.Snapshot(sim);

                for (int generation = 1; generation < pattern.Period; generation++)
                {
                    sim.Step();
                    Assert.AreNotEqual(initial, LifeSimulationTests.Snapshot(sim),
                        $"{pattern.EnglishName} returned to its exact starting position at generation {generation}");
                }
            }
        }

        [Test]
        public void Glider_TravelsOneCellDiagonallyPerPeriod()
        {
            LifePattern glider = LifeSimulationTests.FindPattern("GLIDER");
            LifeSimulation sim = Load(glider);
            List<(int X, int Y)> initial = LifeSimulationTests.AliveCells(sim);

            for (int i = 0; i < glider.Period; i++)
            {
                sim.Step();
            }

            List<(int X, int Y)> after = LifeSimulationTests.AliveCells(sim);
            int dx = after[0].X - initial[0].X;
            int dy = after[0].Y - initial[0].Y;

            Assert.AreEqual(1, dx, "glider drifts one cell horizontally per period");
            Assert.AreEqual(1, dy, "glider drifts one cell vertically per period");
            LifeSimulationTests.AssertSameCells(initial, after, dx, dy, glider.EnglishName);
        }

        [Test]
        public void Pulsar_HasExactlyFortyEightCellsAndPeriodThree()
        {
            LifePattern pulsar = LifeSimulationTests.FindPattern("PULSAR");

            Assert.AreEqual(3, pulsar.Period);
            Assert.AreEqual(48, pulsar.Cells.Length, "the canonical pulsar has 48 cells");

            LifeSimulation sim = Load(pulsar);
            Assert.AreEqual(48, sim.Population);

            // The pulsar is the classic example of a longer-period oscillator: it grows before
            // returning to its own shape.
            sim.Step();
            Assert.AreEqual(56, sim.Population, "pulsar generation 1 has 56 cells");
            sim.Step();
            Assert.AreEqual(72, sim.Population, "pulsar generation 2 has 72 cells");
        }

        // --- helpers ----------------------------------------------------------

        private static LifeSimulation Load(LifePattern pattern)
        {
            var sim = new LifeSimulation(BoardWidth, BoardHeight);
            sim.LoadCentered(pattern.Cells);
            return sim;
        }
    }
}
