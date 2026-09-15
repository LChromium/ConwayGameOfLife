using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// Stage A correctness evidence: the same definite board is handed to the CPU
    /// reference and to the compute-shader backend, both are advanced, and the
    /// FULL state is compared at predetermined generations.
    ///
    /// Deliberate properties:
    ///   * the board is built once, in managed code, and given to both backends as
    ///     an identical array. Neither backend rolls its own dice, so a mismatch
    ///     can never be explained away as "the seeds differed";
    ///   * the first differing cell is reported with its coordinates, the
    ///     generation, the boundary mode and BOTH values;
    ///   * the full-board readback used here is correctness evidence only. Its
    ///     cost is deliberately absent from every timing number the project
    ///     reports.
    ///
    /// The run also writes gpu-cpu-equivalence.json next to the project so the
    /// result survives outside the test log.
    /// </summary>
    public sealed class GpuCpuEquivalenceTests
    {
        private const string ReportFileName = "gpu-cpu-equivalence.json";

        private sealed class CaseResult
        {
            public string Board;
            public int Width;
            public int Height;
            public bool Wrap;
            public int GenerationsCompared;
            public bool Match;

            /// <summary>
            /// True when the CPU reference board actually differs between the first
            /// and last compared generation. Without this, a frozen backend would
            /// "match" forever and the whole run could pass vacuously.
            /// </summary>
            public bool Changed;

            public int DifferGeneration = -1;
            public int DifferX = -1;
            public int DifferY = -1;
            public uint CpuValue;
            public uint GpuValue;
        }

        [Test]
        public void GpuMatchesCpu_AcrossPatternsSizesAndBoundaries()
        {
            if (!GpuLifeBackend.TryCreate(8, 8, out GpuLifeBackend probe, out string error))
            {
                Assert.Ignore($"GPU backend unavailable on this machine: {error}");
                return;
            }

            probe.Dispose();

            var results = new List<CaseResult>();
            var failures = new List<string>();

            foreach (CaseDefinition definition in EnumerateCases())
            {
                byte[] board = BuildBoard(definition, out string label);
                CaseResult result = RunCase(label, board, definition);
                results.Add(result);

                if (!result.Match)
                {
                    failures.Add(
                        $"{label} {definition.Width}x{definition.Height} " +
                        $"{(definition.Wrap ? "wrap" : "fixed")}: first difference at generation " +
                        $"{result.DifferGeneration}, cell ({result.DifferX},{result.DifferY}) " +
                        $"= y*W+x {result.DifferY * definition.Width + result.DifferX}, " +
                        $"CPU={result.CpuValue} GPU={result.GpuValue}");
                }
            }

            WriteReport(results, failures);

            int changedBoards = 0;
            foreach (CaseResult result in results)
            {
                if (result.Changed)
                    changedBoards++;
            }

            // Guard against a vacuous pass: a backend that never evolved would
            // agree with a frozen reference forever.
            Assert.GreaterOrEqual(changedBoards, 30,
                $"only {changedBoards} of {results.Count} boards changed between the first and last " +
                "compared generation - the comparison is close to vacuous and proves little");

            Assert.IsEmpty(failures,
                $"{failures.Count} of {results.Count} CPU/GPU comparisons disagreed. " +
                "Performance claims are suspended until these are resolved.\n" +
                string.Join("\n", failures));
        }

        // -- cases -------------------------------------------------------------

        private readonly struct CaseDefinition
        {
            public readonly int Width;
            public readonly int Height;
            public readonly bool Wrap;
            public readonly int Generations;

            /// <summary>-1 = random board, otherwise an index into LifePatterns.All.</summary>
            public readonly int PatternIndex;
            public readonly int Seed;
            public readonly double Density;

            public CaseDefinition(int width, int height, bool wrap, int generations,
                int patternIndex = -1, int seed = 0, double density = 0.3)
            {
                Width = width;
                Height = height;
                Wrap = wrap;
                Generations = generations;
                PatternIndex = patternIndex;
                Seed = seed;
                Density = density;
            }
        }

        private static IEnumerable<CaseDefinition> EnumerateCases()
        {
            // Small boards: the whole matrix. These are the sizes where the eight
            // neighbour offsets collide under wrapping, which is exactly where a
            // de-duplicating kernel would silently diverge from the reference.
            int[][] smallSizes =
            {
                new[] { 1, 1 },
                new[] { 1, 7 },
                new[] { 2, 9 },
                new[] { 7, 9 },
                new[] { 17, 19 },
                new[] { 96, 64 },
            };

            foreach (int[] size in smallSizes)
            {
                foreach (bool wrap in new[] { false, true })
                {
                    for (int pattern = 0; pattern < LifePatterns.All.Length; pattern++)
                        yield return new CaseDefinition(size[0], size[1], wrap, 8, patternIndex: pattern);

                    yield return new CaseDefinition(size[0], size[1], wrap, 8, seed: 12345, density: 0.30);
                    yield return new CaseDefinition(size[0], size[1], wrap, 8, seed: 777, density: 0.50);
                }
            }

            // Larger boards: a smaller subset. Same kernel, but the point here is
            // that the dispatch and the buffers still behave at scale.
            foreach (bool wrap in new[] { false, true })
            {
                yield return new CaseDefinition(256, 256, wrap, 4, patternIndex: 4);
                yield return new CaseDefinition(256, 256, wrap, 4, seed: 4242, density: 0.30);
                yield return new CaseDefinition(1024, 1024, wrap, 3, patternIndex: 7);
                yield return new CaseDefinition(1024, 1024, wrap, 3, seed: 99, density: 0.20);
            }
        }

        private static byte[] BuildBoard(CaseDefinition definition, out string label)
        {
            if (definition.PatternIndex >= 0)
            {
                LifePattern pattern = LifePatterns.All[definition.PatternIndex];
                label = "pattern:" + pattern.EnglishName;
                return BuildPatternBoard(pattern, definition.Width, definition.Height);
            }

            label = $"random:seed{definition.Seed}:p{definition.Density:0.00}";
            return BuildRandomBoard(definition.Width, definition.Height, definition.Seed, definition.Density);
        }

        /// <summary>
        /// Centres a pattern with the same bounding-box rule as
        /// LifeSimulation.LoadCentered, so the harness and the app agree.
        /// </summary>
        private static byte[] BuildPatternBoard(LifePattern pattern, int width, int height)
        {
            var board = new byte[width * height];
            LifeCell[] cells = pattern.Cells;
            if (cells.Length == 0)
                return board;

            int minX = cells[0].X, maxX = cells[0].X, minY = cells[0].Y, maxY = cells[0].Y;
            for (int i = 1; i < cells.Length; i++)
            {
                minX = Math.Min(minX, cells[i].X);
                maxX = Math.Max(maxX, cells[i].X);
                minY = Math.Min(minY, cells[i].Y);
                maxY = Math.Max(maxY, cells[i].Y);
            }

            int offsetX = (width - (maxX - minX + 1)) / 2 - minX;
            int offsetY = (height - (maxY - minY + 1)) / 2 - minY;

            for (int i = 0; i < cells.Length; i++)
            {
                int x = cells[i].X + offsetX;
                int y = cells[i].Y + offsetY;
                if (x >= 0 && x < width && y >= 0 && y < height)
                    board[y * width + x] = 1;
            }

            return board;
        }

        private static byte[] BuildRandomBoard(int width, int height, int seed, double density)
        {
            var board = new byte[width * height];
            var random = new System.Random(seed);
            for (int i = 0; i < board.Length; i++)
                board[i] = random.NextDouble() < density ? (byte)1 : (byte)0;

            return board;
        }

        // -- execution ---------------------------------------------------------

        private static CaseResult RunCase(string label, byte[] board, CaseDefinition definition)
        {
            var result = new CaseResult
            {
                Board = label,
                Width = definition.Width,
                Height = definition.Height,
                Wrap = definition.Wrap,
            };

            Assert.IsTrue(GpuLifeBackend.TryCreate(definition.Width, definition.Height,
                out GpuLifeBackend gpu, out string error), $"GPU backend creation failed: {error}");

            using (gpu)
            {
                var cpu = new CpuLifeBackend(definition.Width, definition.Height);
                cpu.WrapEdges = definition.Wrap;
                gpu.WrapEdges = definition.Wrap;

                // The identical array goes to both. This is the whole point.
                cpu.LoadBoard(board);
                gpu.LoadBoard(board);

                int count = definition.Width * definition.Height;
                var cpuCells = new uint[count];
                var gpuCells = new uint[count];
                uint[] firstGeneration = null;

                for (int generation = 0; generation <= definition.Generations; generation++)
                {
                    if (generation > 0)
                    {
                        cpu.Step();
                        gpu.Step();
                    }

                    Assert.IsTrue(cpu.TryReadAllCells(cpuCells), "CPU readback failed");
                    Assert.IsTrue(gpu.TryReadAllCells(gpuCells), "GPU readback failed");

                    if (generation == 0)
                        firstGeneration = (uint[])cpuCells.Clone();

                    int index = FirstDifference(cpuCells, gpuCells);
                    if (index >= 0)
                    {
                        result.Match = false;
                        result.GenerationsCompared = generation;
                        result.DifferGeneration = generation;
                        result.DifferX = index % definition.Width;
                        result.DifferY = index / definition.Width;
                        result.CpuValue = cpuCells[index];
                        result.GpuValue = gpuCells[index];
                        return result;
                    }

                    result.GenerationsCompared = generation;
                }

                result.Match = true;
                result.Changed = FirstDifference(firstGeneration, cpuCells) >= 0;
            }

            return result;
        }

        private static int FirstDifference(uint[] left, uint[] right)
        {
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return i;
            }

            return -1;
        }

        // -- report ------------------------------------------------------------

        private static void WriteReport(List<CaseResult> results, List<string> failures)
        {
            int matched = 0;
            int changed = 0;
            foreach (CaseResult result in results)
            {
                if (result.Match)
                    matched++;
                if (result.Changed)
                    changed++;
            }

            var json = new StringBuilder();
            json.Append("{\n");
            json.Append($"  \"unityVersion\": \"{Application.unityVersion}\",\n");
            json.Append($"  \"graphicsDevice\": \"{SystemInfo.graphicsDeviceName}\",\n");
            json.Append($"  \"graphicsApi\": \"{SystemInfo.graphicsDeviceType}\",\n");
            json.Append($"  \"computeSupported\": {Bool(SystemInfo.supportsComputeShaders)},\n");
            json.Append($"  \"threadGroupSize\": {GpuLifeBackend.ThreadGroupSize},\n");
            json.Append($"  \"cases\": {results.Count},\n");
            json.Append($"  \"matched\": {matched},\n");
            json.Append($"  \"mismatched\": {results.Count - matched},\n");
            json.Append($"  \"boardsThatChanged\": {changed},\n");
            json.Append("  \"results\": [\n");

            for (int i = 0; i < results.Count; i++)
            {
                CaseResult r = results[i];
                json.Append("    {");
                json.Append($"\"board\": \"{r.Board}\", ");
                json.Append($"\"size\": \"{r.Width}x{r.Height}\", ");
                json.Append($"\"boundary\": \"{(r.Wrap ? "wrap" : "fixed")}\", ");
                json.Append($"\"generationsCompared\": {r.GenerationsCompared}, ");
                json.Append($"\"match\": {Bool(r.Match)}, ");
                json.Append($"\"changed\": {Bool(r.Changed)}");
                if (!r.Match)
                {
                    json.Append($", \"firstDifference\": {{\"generation\": {r.DifferGeneration}, ");
                    json.Append($"\"x\": {r.DifferX}, \"y\": {r.DifferY}, ");
                    json.Append($"\"cpu\": {r.CpuValue}, \"gpu\": {r.GpuValue}}}");
                }

                json.Append("}");
                json.Append(i == results.Count - 1 ? "\n" : ",\n");
            }

            json.Append("  ],\n");
            json.Append("  \"note\": \"Full-board readback is correctness evidence only and is excluded from every timing figure.\"\n");
            json.Append("}\n");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(projectRoot, ReportFileName);
            File.WriteAllText(path, json.ToString());

            Debug.Log($"[stage-a] CPU/GPU equivalence: {matched}/{results.Count} cases matched. Report: {path}");
            foreach (string failure in failures)
                Debug.LogError("[stage-a] MISMATCH " + failure);
        }

        private static string Bool(bool value) => value ? "true" : "false";
    }
}
