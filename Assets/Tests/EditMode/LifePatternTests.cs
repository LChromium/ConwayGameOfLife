using System;
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
        public void PeriodicOscillators_StayInPlace()
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (pattern.Kind != LifePatternKind.PeriodicOscillator)
                {
                    continue;
                }

                LifeSimulation sim = Load(pattern);
                BoundingBox(sim, out int minX, out int minY, out int maxX, out int maxY);
                int centerX = minX + maxX;
                int centerY = minY + maxY;

                // Every generation must keep the same bounding-box centre and stay populated.
                // A spaceship would drift the centre by a whole cell; an oscillator must not.
                for (int generation = 1; generation <= pattern.Period * 2; generation++)
                {
                    sim.Step();
                    BoundingBox(sim, out int x, out int y, out int x2, out int y2);

                    Assert.AreEqual(centerX, x + x2,
                        $"{pattern.EnglishName} drifted horizontally at generation {generation}");
                    Assert.AreEqual(centerY, y + y2,
                        $"{pattern.EnglishName} drifted vertically at generation {generation}");
                    Assert.Greater(sim.Population, 0,
                        $"{pattern.EnglishName} died out at generation {generation}");
                }
            }
        }

        [Test]
        public void Spaceships_TranslateByAWholeOffsetEveryPeriod()
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
                BoundingBox(sim, out int minX, out int minY, out _, out _);

                for (int i = 0; i < pattern.Period; i++)
                {
                    sim.Step();
                }

                Assert.AreEqual(initialPopulation, sim.Population,
                    $"{pattern.EnglishName} should keep its cell count while travelling");

                BoundingBox(sim, out int minX2, out int minY2, out _, out _);
                int dx = minX2 - minX;
                int dy = minY2 - minY;

                Assert.AreNotEqual(0, Math.Abs(dx) + Math.Abs(dy),
                    $"{pattern.EnglishName} did not move, so it is not a spaceship");

                checkedCount++;
            }

            Assert.GreaterOrEqual(checkedCount, 2, "expected at least two spaceships");
        }

        [Test]
        public void Glider_TravelsOneCellDiagonallyPerPeriod()
        {
            LifePattern glider = LifeSimulationTests.FindPattern("GLIDER");
            LifeSimulation sim = Load(glider);
            BoundingBox(sim, out int minX, out int minY, out _, out _);

            for (int i = 0; i < glider.Period; i++)
            {
                sim.Step();
            }

            BoundingBox(sim, out int minX2, out int minY2, out _, out _);
            Assert.AreEqual(1, minX2 - minX, "glider drifts one cell horizontally per period");
            Assert.AreEqual(1, minY2 - minY, "glider drifts one cell vertically per period");
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

        private static void BoundingBox(LifeSimulation sim, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue;
            minY = int.MaxValue;
            maxX = int.MinValue;
            maxY = int.MinValue;

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
        }
    }
}
