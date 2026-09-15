using System;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage-B seeding acceptance. The two degeneracy claims are the important ones:
    /// a test that only checks "cluster strength 0 runs without throwing" would prove
    /// nothing, so both are checked against an independently composed expectation.
    /// </summary>
    public sealed class LifeNoiseSeedingTests
    {
        private const int Width = 256;
        private const int Height = 256;

        private static LifeNoiseParameters Fbm(
            int seed = 7, float density = 0.35f, float scale = 32f,
            float warp = 0f, float cluster = 0.6f) =>
            new(LifeSeedingMode.Fbm, seed, density, scale, warp, cluster);

        private static byte[] Generate(in LifeNoiseParameters parameters, int width = Width, int height = Height)
        {
            var board = new byte[width * height];
            LifeNoiseSeeding.Generate(parameters, width, height, board);
            return board;
        }

        // -- reproducibility ----------------------------------------------------

        [Test]
        public void SameParameters_ProduceTheIdenticalBoard()
        {
            LifeNoiseParameters parameters = Fbm(warp: 6f);
            byte[] first = Generate(parameters);
            byte[] second = Generate(parameters);

            CollectionAssert.AreEqual(first, second,
                "the same parameters must reproduce the same initial state");
        }

        [Test]
        public void DifferentSeed_ProducesADifferentBoard()
        {
            byte[] a = Generate(Fbm(seed: 1, warp: 6f));
            byte[] b = Generate(Fbm(seed: 2, warp: 6f));

            CollectionAssert.AreNotEqual(a, b, "changing the seed must change the board");
        }

        [Test]
        public void BoardIsBinaryAndFillsEveryCell()
        {
            byte[] board = Generate(Fbm(warp: 4f));
            foreach (byte cell in board)
                Assert.IsTrue(cell == 0 || cell == 1, $"cell value {cell} is not 0 or 1");
        }

        // -- degeneracy 1: cluster strength 0 == uniform ------------------------

        [Test]
        public void ClusterStrengthZero_IsExactlyTheUniformMode()
        {
            // With cluster strength 0 the probability is constant, so the comparison
            // collapses to "hash random < density" -- which is what the uniform mode
            // does with the same hash. If the two ever disagreed, cluster strength 0
            // would not be the control group it claims to be.
            byte[] clustered = Generate(Fbm(cluster: 0f));
            byte[] uniform = Generate(new LifeNoiseParameters(LifeSeedingMode.Uniform, 7, 0.35f, 32f, 0f, 0f));

            CollectionAssert.AreEqual(uniform, clustered,
                "cluster strength 0 must degenerate to uniform probability seeding");
        }

        [Test]
        public void ClusterStrengthZero_RealisesTheRequestedDensity()
        {
            float requested = 0.35f;
            byte[] board = Generate(Fbm(density: requested, cluster: 0f));
            float actual = LifeNoiseSeeding.MeasureDensity(board);

            Assert.That(actual, Is.EqualTo(requested).Within(0.01f),
                $"uniform seeding at density {requested} realised {actual}");
        }

        // -- degeneracy 2: warp strength 0 == undistorted fBm -------------------

        [Test]
        public void WarpStrengthZero_IsExactlyTheUnwarpedField()
        {
            // Recomposed here from the documented relation rather than from the
            // generator's own loop: sample normalised noise n at (x/scale, y/scale),
            // form p = saturate(density + cluster * (n - 0.5)), and threshold.
            const int seed = 7;
            const float density = 0.35f;
            const float scale = 32f;
            const float cluster = 0.6f;

            byte[] generated = Generate(Fbm(seed: seed, density: density, scale: scale, warp: 0f, cluster: cluster));

            var expected = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float n = LifeNoiseSeeding.Fbm(x / scale, y / scale, seed);
                    float p = density + cluster * (n - 0.5f);
                    p = p < 0f ? 0f : p > 1f ? 1f : p;

                    float random = (LifeNoiseSeeding.Hash(x, y, seed ^ unchecked((int)0x9E3779B9)) >> 8)
                                   * (1f / 16777216f);

                    expected[y * Width + x] = random < p ? (byte)1 : (byte)0;
                }
            }

            CollectionAssert.AreEqual(expected, generated,
                "warp strength 0 must give the undistorted fBm field");
        }

        [Test]
        public void WarpStrengthAboveZero_ActuallyChangesTheBoard()
        {
            // Without this, the previous test could pass on a generator that ignored
            // the warp term entirely.
            byte[] unwarped = Generate(Fbm(warp: 0f));
            byte[] warped = Generate(Fbm(warp: 8f));

            CollectionAssert.AreNotEqual(unwarped, warped,
                "a non-zero warp strength must change the board");
        }

        // -- the noise has to do something -------------------------------------

        [Test]
        public void At1024_ClusteringIsMeasurableInTheGeneratedBoard()
        {
            // The quantified clustering claim lives here, on the generated data,
            // rather than on a screenshot. Screen-scraping was tried and abandoned:
            // classifying board pixels against UI chrome is fragile and twice
            // matched the panel colours instead of the board.
            const int size = 1024;
            const int blockSize = 32;

            var uniform = new byte[size * size];
            LifeNoiseSeeding.Generate(
                new LifeNoiseParameters(LifeSeedingMode.Uniform, 20260915, 0.32f, 60f, 0f, 0f),
                size, size, uniform);

            var clustered = new byte[size * size];
            LifeNoiseSeeding.Generate(
                new LifeNoiseParameters(LifeSeedingMode.Fbm, 20260915, 0.32f, 60f, 10f, 0.7f),
                size, size, clustered);

            float uniformSpread = BlockDensitySpread(uniform, size, size, blockSize);
            float clusteredSpread = BlockDensitySpread(clustered, size, size, blockSize);

            float uniformDensity = LifeNoiseSeeding.MeasureDensity(uniform);
            float clusteredDensity = LifeNoiseSeeding.MeasureDensity(clustered);

            TestContext.WriteLine(
                $"[stage-b] 1024x1024 seeding, block {blockSize}: " +
                $"uniform density {uniformDensity:0.0000} spread {uniformSpread:0.0000}; " +
                $"fBm density {clusteredDensity:0.0000} spread {clusteredSpread:0.0000}; " +
                $"ratio {clusteredSpread / uniformSpread:0.00}x");

            Assert.That(clusteredSpread, Is.GreaterThan(uniformSpread * 2f),
                $"at 1024 the fBm board should vary in density far more than uniform: " +
                $"uniform {uniformSpread:0.0000}, fBm {clusteredSpread:0.0000}");
            Assert.That(Math.Abs(clusteredDensity - 0.32f), Is.LessThan(0.02f),
                "the realised density should still land near the base probability");
        }

        [Test]
        public void ClusterStrengthAboveZero_ClustersMoreThanUniform()
        {
            // Measured as the spread of density across blocks, which is what clustering
            // actually is. Adjacent-pair agreement was tried first and is a poor probe:
            // it only moves by 2*Var(p) and a normalised four-octave fBm has a narrow
            // distribution, so a real effect looked like noise.
            const int blockSize = 16;
            const float density = 0.5f;

            byte[] uniform = Generate(new LifeNoiseParameters(LifeSeedingMode.Uniform, 7, density, 32f, 0f, 0f));
            byte[] clustered = Generate(Fbm(density: density, scale: 32f, cluster: 0.8f));

            float uniformSpread = BlockDensitySpread(uniform, blockSize);
            float clusteredSpread = BlockDensitySpread(clustered, blockSize);

            Assert.That(clusteredSpread, Is.GreaterThan(uniformSpread * 2f),
                $"clustered seeding should vary in density across the board: " +
                $"uniform spread {uniformSpread:0.####}, clustered {clusteredSpread:0.####}");
        }

        [Test]
        public void ClusterSize_GrowsWithScale()
        {
            // With a fixed block size, a large noise scale means a block sits inside a
            // single noise cell and inherits its full probability, while a small scale
            // averages several cells together and flattens the block. So the spread
            // must grow with scale.
            const int blockSize = 16;

            byte[] fine = Generate(Fbm(density: 0.5f, scale: 8f, cluster: 0.8f));
            byte[] coarse = Generate(Fbm(density: 0.5f, scale: 64f, cluster: 0.8f));

            float fineSpread = BlockDensitySpread(fine, blockSize);
            float coarseSpread = BlockDensitySpread(coarse, blockSize);

            Assert.That(coarseSpread, Is.GreaterThan(fineSpread),
                $"a larger cluster scale should produce larger blobs: " +
                $"scale 8 spread {fineSpread:0.####}, scale 64 spread {coarseSpread:0.####}");
        }

        [Test]
        public void RealisedDensityMovesAwayFromTheBaseProbability_WhenClustering()
        {
            // The base density is a probability, not a population promise. Clamping
            // p at 0 and 1 is what moves the realised density, and the UI has to
            // report the realised one.
            byte[] clustered = Generate(Fbm(density: 0.15f, scale: 32f, cluster: 1f));
            float actual = LifeNoiseSeeding.MeasureDensity(clustered);

            Assert.That(Math.Abs(actual - 0.15f), Is.GreaterThan(0.005f),
                $"clamping should move the realised density off the base probability, got {actual}");
        }

        // -- value noise --------------------------------------------------------

        [Test]
        public void ValueNoise_StaysInRange()
        {
            for (int i = 0; i < 2000; i++)
            {
                float x = i * 0.37f;
                float y = i * 0.11f;
                float value = LifeNoiseSeeding.ValueNoise(x, y, 3);
                Assert.That(value, Is.InRange(0f, 1f), $"value noise left [0,1] at ({x},{y})");
            }
        }

        [Test]
        public void Fbm_StaysInRange()
        {
            for (int i = 0; i < 2000; i++)
            {
                float value = LifeNoiseSeeding.Fbm(i * 0.021f, i * 0.037f, 11);
                Assert.That(value, Is.InRange(0f, 1f), $"fBm left [0,1] at sample {i}");
            }
        }

        [Test]
        public void Hash_IsStableForFixedInputs()
        {
            // Pins the bit pattern. If this ever changes, every seeded board in the
            // project changes with it, and previously reported boards stop reproducing.
            // The origin is included on purpose: an all-zero input used to be a fixed
            // point of the mixer, which this pins against regressing.
            Assert.AreEqual(0x1BD22E95u, LifeNoiseSeeding.Hash(0, 0, 0), "hash(0,0,0) changed");
            Assert.AreEqual(0x021081F0u, LifeNoiseSeeding.Hash(1, 0, 0), "hash(1,0,0) changed");
            Assert.AreEqual(0x195C74E7u, LifeNoiseSeeding.Hash(0, 1, 0), "hash(0,1,0) changed");
            Assert.AreNotEqual(0u, LifeNoiseSeeding.Hash(0, 0, 0), "the origin must not hash to zero");
        }

        // -- argument handling --------------------------------------------------

        [Test]
        public void Generate_RejectsBadArguments()
        {
            var destination = new byte[16];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LifeNoiseSeeding.Generate(Fbm(), 0, 4, destination));
            Assert.Throws<ArgumentException>(() =>
                LifeNoiseSeeding.Generate(Fbm(), 8, 8, destination));
        }

        [Test]
        public void Parameters_ClampOutOfRangeInputs()
        {
            var parameters = new LifeNoiseParameters(LifeSeedingMode.Fbm, 1, 5f, -3f, -1f, 9f);
            Assert.AreEqual(1f, parameters.Density, "density must clamp to 1");
            Assert.AreEqual(LifeNoiseParameters.MinScale, parameters.Scale, "scale must have a floor");
            Assert.AreEqual(0f, parameters.WarpStrength, "negative warp must clamp to 0");
            Assert.AreEqual(1f, parameters.ClusterStrength, "cluster strength must clamp to 1");
        }

        // -- helpers ------------------------------------------------------------

        private static float BlockDensitySpread(byte[] board, int blockSize) =>
            BlockDensitySpread(board, Width, Height, blockSize);

        /// <summary>
        /// Standard deviation of the density measured over square blocks of
        /// <paramref name="blockSize"/> cells. Uniform seeding gives the binomial
        /// spread sqrt(d(1-d)/n); spatial clustering pushes it well above that.
        /// </summary>
        private static float BlockDensitySpread(byte[] board, int width, int height, int blockSize)
        {
            var densities = new System.Collections.Generic.List<float>();

            for (int blockY = 0; blockY + blockSize <= height; blockY += blockSize)
            {
                for (int blockX = 0; blockX + blockSize <= width; blockX += blockSize)
                {
                    int alive = 0;
                    for (int y = 0; y < blockSize; y++)
                    {
                        for (int x = 0; x < blockSize; x++)
                            alive += board[(blockY + y) * width + blockX + x] != 0 ? 1 : 0;
                    }

                    densities.Add((float)alive / (blockSize * blockSize));
                }
            }

            float mean = 0f;
            foreach (float d in densities)
                mean += d;
            mean /= densities.Count;

            float sumSquares = 0f;
            foreach (float d in densities)
                sumSquares += (d - mean) * (d - mean);

            return (float)Math.Sqrt(sumSquares / densities.Count);
        }
    }
}
