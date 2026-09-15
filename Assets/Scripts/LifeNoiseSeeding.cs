using System;

namespace ConwayGameOfLife
{
    /// <summary>Which generator produced a board.</summary>
    public enum LifeSeedingMode
    {
        /// <summary>Uniform per-cell probability. The control group for the fBm path.</summary>
        Uniform,

        /// <summary>fBm value noise with domain warping, thresholded per cell.</summary>
        Fbm,
    }

    /// <summary>
    /// The stage-B seeding parameters. Deliberately the whole set the brief allows --
    /// no noise editor, no per-octave controls.
    /// </summary>
    public readonly struct LifeNoiseParameters : IEquatable<LifeNoiseParameters>
    {
        public LifeSeedingMode Mode { get; }

        /// <summary>Derives every hash in the generator. Same seed, same board.</summary>
        public int Seed { get; }

        /// <summary>
        /// Base probability, 0..1. NOT a promise about the resulting population:
        /// clustering moves the realised density, and the UI reports the actual one.
        /// </summary>
        public float Density { get; }

        /// <summary>Cluster size in cells: one noise period spans this many cells.</summary>
        public float Scale { get; }

        /// <summary>Domain warp offset in cells. 0 disables warping entirely.</summary>
        public float WarpStrength { get; }

        /// <summary>
        /// How far the noise pushes the probability away from <see cref="Density"/>.
        /// 0 makes the probability constant, which degenerates to uniform seeding.
        /// </summary>
        public float ClusterStrength { get; }

        public LifeNoiseParameters(
            LifeSeedingMode mode, int seed, float density, float scale,
            float warpStrength, float clusterStrength)
        {
            Mode = mode;
            Seed = seed;
            Density = Clamp01(density);
            Scale = scale < MinScale ? MinScale : scale;
            WarpStrength = warpStrength < 0f ? 0f : warpStrength;
            ClusterStrength = Clamp01(clusterStrength);
        }

        public const float MinScale = 1f;

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value))
                return 0f;
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        public override string ToString() =>
            $"{Mode} seed={Seed} density={Density:0.###} scale={Scale:0.##} " +
            $"warp={WarpStrength:0.##} cluster={ClusterStrength:0.###}";

        /// <summary>
        /// Value equality, so "is the candidate on screen still the one these controls
        /// describe" is a cheap field comparison rather than reflection.
        /// </summary>
        public bool Equals(LifeNoiseParameters other) =>
            Mode == other.Mode
            && Seed == other.Seed
            && Density.Equals(other.Density)
            && Scale.Equals(other.Scale)
            && WarpStrength.Equals(other.WarpStrength)
            && ClusterStrength.Equals(other.ClusterStrength);

        public override bool Equals(object obj) => obj is LifeNoiseParameters other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            (int)Mode, Seed, Density, Scale, WarpStrength, ClusterStrength);
    }

    /// <summary>
    /// Stage-B noise seeding. Pure C#: no UnityEngine, so it can be exercised by the
    /// standalone verification harness as well as by EditMode tests.
    ///
    /// <para><b>One generator, not two.</b> The noise field is produced here, on the
    /// CPU, and handed to whichever backend is live as a definite array which the
    /// backend uploads in one batch. Nothing generates noise a second time on the
    /// GPU. That is deliberate: bit-identical float noise across a C# JIT and an
    /// HLSL compiler is not something to assume, because the shader compiler is free
    /// to contract <c>a * b + c</c> into an FMA and change the last bits. Generating
    /// once removes the whole class of problem, and the CPU/GPU agreement check then
    /// verifies the upload path rather than two drifting implementations.</para>
    ///
    /// <para><b>The generating relation</b>, exactly as specified:
    /// warp the sampling coordinate, sample normalised noise <c>n</c>, form
    /// <c>p = saturate(density + clusterStrength * (n - 0.5))</c>, and compare a
    /// per-cell hash random against <c>p</c>.</para>
    ///
    /// <para><b>Degeneracies are structural, not special-cased.</b>
    /// <c>clusterStrength = 0</c> makes <c>p</c> constant, so the comparison collapses
    /// to "hash random &lt; density" -- which is exactly what <see cref="LifeSeedingMode.Uniform"/>
    /// does with the same hash. <c>warpStrength = 0</c> multiplies the warp offset by
    /// zero, so the sampling coordinate is exactly <c>(x / scale, y / scale)</c>.
    /// Neither is a branch that could drift out of sync with the other path.</para>
    /// </summary>
    public static class LifeNoiseSeeding
    {
        /// <summary>Fixed at four layers; the brief does not open an octave editor.</summary>
        public const int Octaves = 4;

        public const float Lacunarity = 2f;
        public const float Gain = 0.5f;

        /// <summary>Alive when the per-cell hash random is strictly below p.</summary>
        public static void Generate(in LifeNoiseParameters parameters, int width, int height, Span<byte> destination)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Board dimensions must be positive.");
            if (destination.Length < width * height)
                throw new ArgumentException($"Board needs {width * height} cells, got {destination.Length}.", nameof(destination));

            if (parameters.Mode == LifeSeedingMode.Uniform)
            {
                GenerateUniform(parameters, width, height, destination);
                return;
            }

            GenerateFbm(parameters, width, height, destination);
        }

        private static void GenerateUniform(in LifeNoiseParameters parameters, int width, int height, Span<byte> destination)
        {
            int seed = parameters.Seed;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float random = ThresholdRandom(x, y, seed);
                    destination[y * width + x] = random < parameters.Density ? (byte)1 : (byte)0;
                }
            }
        }

        private static void GenerateFbm(in LifeNoiseParameters parameters, int width, int height, Span<byte> destination)
        {
            int seed = parameters.Seed;
            float scale = parameters.Scale;
            float warp = parameters.WarpStrength;
            float cluster = parameters.ClusterStrength;
            float density = parameters.Density;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sampleX = x;
                    float sampleY = y;

                    if (warp > 0f)
                    {
                        // Warp the sampling coordinate first, then sample the noise.
                        float warpX = ValueNoise(x / scale, y / scale, seed ^ WarpSeedX) * 2f - 1f;
                        float warpY = ValueNoise(x / scale, y / scale, seed ^ WarpSeedY) * 2f - 1f;
                        sampleX += warp * warpX;
                        sampleY += warp * warpY;
                    }

                    float n = Fbm(sampleX / scale, sampleY / scale, seed);
                    float p = density + cluster * (n - 0.5f);
                    p = p < 0f ? 0f : p > 1f ? 1f : p;

                    float random = ThresholdRandom(x, y, seed);
                    destination[y * width + x] = random < p ? (byte)1 : (byte)0;
                }
            }
        }

        // -- noise --------------------------------------------------------------

        /// <summary>
        /// Normalised fBm in [0, 1]: four octaves of value noise, amplitude halved and
        /// frequency doubled each layer, divided by the total amplitude so the range
        /// does not depend on <see cref="Octaves"/>.
        /// </summary>
        public static float Fbm(float x, float y, int seed)
        {
            float sum = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float total = 0f;

            for (int octave = 0; octave < Octaves; octave++)
            {
                int octaveSeed = seed ^ (octave * OctaveSeedStride);
                sum += amplitude * ValueNoise(x * frequency, y * frequency, octaveSeed);
                total += amplitude;
                amplitude *= Gain;
                frequency *= Lacunarity;
            }

            return sum / total;
        }

        /// <summary>
        /// Value noise in [0, 1] on the integer lattice: hash the four corners, then
        /// interpolate with a smoothstep so the field has no visible lattice creases.
        /// </summary>
        public static float ValueNoise(float x, float y, int seed)
        {
            int x0 = FloorToInt(x);
            int y0 = FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;

            float v00 = ToUnit(Hash(x0, y0, seed));
            float v10 = ToUnit(Hash(x0 + 1, y0, seed));
            float v01 = ToUnit(Hash(x0, y0 + 1, seed));
            float v11 = ToUnit(Hash(x0 + 1, y0 + 1, seed));

            float sx = Smoothstep(fx);
            float sy = Smoothstep(fy);

            float bottom = v00 + (v10 - v00) * sx;
            float top = v01 + (v11 - v01) * sx;
            return bottom + (top - bottom) * sy;
        }

        /// <summary>3t^2 - 2t^3. Polynomial only, so it stays reproducible everywhere.</summary>
        private static float Smoothstep(float t) => t * t * (3f - 2f * t);

        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        // -- hashing ------------------------------------------------------------

        // Integer mixing in the style of the xxHash / Murmur finalisers: public-domain
        // bit mixing with no library dependency, chosen over a named noise library so
        // the exact bit pattern is pinned here and cannot move under a dependency bump.
        private const uint HashPrime1 = 2246822519u;
        private const uint HashPrime2 = 3266489917u;
        private const uint HashPrime3 = 668265263u;

        /// <summary>
        /// Folded in before the avalanche so that an all-zero input does not map to
        /// zero: without it hash(0,0,0) is a fixed point of the mixer, and the lattice
        /// point at the origin would be stuck at the bottom of the range for seed 0.
        /// </summary>
        private const uint HashOffset = 0x9E3779B9u;

        /// <summary>Integer hash over a lattice point. Range is the full uint range.</summary>
        public static uint Hash(int x, int y, int seed)
        {
            uint h = (uint)x * HashPrime2;
            h += (uint)y * HashPrime3;
            h += (uint)seed * HashPrime1;
            h += HashOffset;
            h ^= h >> 15;
            h *= HashPrime1;
            h ^= h >> 13;
            h *= HashPrime3;
            h ^= h >> 16;
            return h;
        }

        /// <summary>
        /// Maps a hash to [0, 1) using the top 24 bits, which is exact in a float:
        /// <c>(h &gt;&gt; 8) * 2^-24</c> needs no rounding.
        /// </summary>
        private static float ToUnit(uint hash) => (hash >> 8) * (1f / 16777216f);

        /// <summary>
        /// The per-cell random compared against p. Uses its own seed derivation so the
        /// threshold stream is not correlated with the noise field.
        /// </summary>
        private static float ThresholdRandom(int x, int y, int seed) =>
            ToUnit(Hash(x, y, seed ^ ThresholdSeedSalt));

        private const int ThresholdSeedSalt = unchecked((int)0x9E3779B9);
        private const int WarpSeedX = unchecked((int)0x5BF03635);
        private const int WarpSeedY = unchecked((int)0x27D4EB2F);
        private const int OctaveSeedStride = unchecked((int)0x85EBCA6B);

        // -- reporting ----------------------------------------------------------

        public static int CountAlive(ReadOnlySpan<byte> cells)
        {
            int alive = 0;
            for (int i = 0; i < cells.Length; i++)
                alive += cells[i] != 0 ? 1 : 0;

            return alive;
        }

        /// <summary>
        /// The density actually realised, which is what the UI reports: the requested
        /// density is a base probability, not a population promise.
        /// </summary>
        public static float MeasureDensity(ReadOnlySpan<byte> cells) =>
            cells.Length == 0 ? 0f : (float)CountAlive(cells) / cells.Length;
    }
}
