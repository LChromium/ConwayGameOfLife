using System;
using UnityEngine;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Compute-shader evolution backend.
    ///
    /// Two uint buffers of <c>Width * Height</c> elements holding only 0 or 1,
    /// indexed <c>y * Width + x</c> to match the CPU reference exactly. A step
    /// reads <c>front</c>, writes <c>back</c>, then swaps the two -- a cell is
    /// never overwritten in place within a generation.
    ///
    /// The front buffer is handed straight to the display kernel, so the normal
    /// display path performs no CPU readback at all.
    /// </summary>
    public sealed class GpuLifeBackend : ILifeBackend
    {
        /// <summary>Matches <c>[numthreads(8, 8, 1)]</c> in LifeGpu.compute.</summary>
        public const int ThreadGroupSize = 8;

        private readonly ComputeShader shader;
        private readonly int stepKernel;
        private readonly uint[] uploadScratch;
        private readonly uint[] readbackScratch;

        private ComputeBuffer front;
        private ComputeBuffer back;
        private int generation;
        private bool wrapEdges;
        private bool disposed;

        /// <summary>
        /// Creates a backend only if the device and the asset actually support
        /// it. On failure the caller is expected to fall back to the CPU backend
        /// and surface <paramref name="error"/> -- never to keep showing a
        /// "running" board that is not evolving.
        /// </summary>
        public static bool TryCreate(int width, int height, out GpuLifeBackend backend, out string error)
        {
            backend = null;
            error = null;

            if (width <= 0 || height <= 0)
            {
                error = $"invalid board size {width}x{height}";
                return false;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                error = "SystemInfo.supportsComputeShaders is false on this device";
                return false;
            }

            ComputeShader asset = Resources.Load<ComputeShader>("LifeGpu");
            if (asset == null)
            {
                error = "compute shader resource 'LifeGpu' not found";
                return false;
            }

            try
            {
                backend = new GpuLifeBackend(asset, width, height);
            }
            catch (Exception exception)
            {
                // Unity throws on buffer allocation failure (including out of
                // video memory) and on a missing kernel.
                error = $"{exception.GetType().Name}: {exception.Message}";
                backend = null;
                return false;
            }

            return true;
        }

        private GpuLifeBackend(ComputeShader asset, int width, int height)
        {
            shader = asset;
            stepKernel = shader.FindKernel("Step");

            Width = width;
            Height = height;
            CellCount = width * height;

            uploadScratch = new uint[CellCount];
            readbackScratch = new uint[CellCount];

            front = new ComputeBuffer(CellCount, sizeof(uint));
            back = new ComputeBuffer(CellCount, sizeof(uint));

            Array.Clear(uploadScratch, 0, uploadScratch.Length);
            front.SetData(uploadScratch);
            back.SetData(uploadScratch);
        }

        public string Name => "GPU";

        public int Width { get; }
        public int Height { get; }
        public int CellCount { get; }

        public int Generation => generation;

        public bool WrapEdges
        {
            get => wrapEdges;
            set => wrapEdges = value;
        }

        /// <summary>The buffer holding the current generation. Bind it to render; do not read it back per frame.</summary>
        public ComputeBuffer CurrentBuffer => front;

        public ComputeShader Shader => shader;

        public void LoadBoard(ReadOnlySpan<byte> cells)
        {
            if (cells.Length != CellCount)
            {
                throw new ArgumentException(
                    $"Board must be {CellCount} cells, got {cells.Length}.", nameof(cells));
            }

            for (int i = 0; i < CellCount; i++)
                uploadScratch[i] = cells[i] != 0 ? 1u : 0u;

            front.SetData(uploadScratch);
            generation = 0;
        }

        public void Clear()
        {
            Array.Clear(uploadScratch, 0, uploadScratch.Length);
            front.SetData(uploadScratch);
            generation = 0;
        }

        /// <summary>
        /// Paints one cell by uploading a single element into the current
        /// buffer. A one-element SetData is cheap enough to keep the existing
        /// hand-editing feature working on the GPU board.
        /// </summary>
        public void SetCell(int x, int y, bool alive)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return;

            uploadScratch[0] = alive ? 1u : 0u;
            front.SetData(uploadScratch, 0, y * Width + x, 1);
        }

        public void Step()
        {
            ApplyUniforms(stepKernel);

            shader.SetBuffer(stepKernel, "_StateIn", front);
            shader.SetBuffer(stepKernel, "_StateOut", back);

            // Dispatch is rounded up; the kernel rejects threads past the edge.
            int groupsX = (Width + ThreadGroupSize - 1) / ThreadGroupSize;
            int groupsY = (Height + ThreadGroupSize - 1) / ThreadGroupSize;
            shader.Dispatch(stepKernel, groupsX, groupsY, 1);

            (front, back) = (back, front);
            generation++;
        }

        /// <summary>
        /// Batch steps. Same work as calling <see cref="Step"/> repeatedly; it
        /// exists so a benchmark can advance N generations without the managed
        /// per-call overhead dominating the small-board numbers.
        /// </summary>
        public void StepMany(int generations)
        {
            for (int i = 0; i < generations; i++)
                Step();
        }

        internal void ApplyUniforms(int kernel)
        {
            shader.SetInt("_Width", Width);
            shader.SetInt("_Height", Height);
            shader.SetInt("_WrapEdges", wrapEdges ? 1 : 0);
        }

        /// <summary>
        /// Stage A does not implement GPU statistics. Returning false is the
        /// honest answer: the alternative is a full-board readback on every
        /// generation, which this stage explicitly forbids.
        /// </summary>
        public bool TryGetPopulation(out int population)
        {
            population = 0;
            return false;
        }

        /// <summary>
        /// Full readback, synchronising. Correctness evidence only; never call
        /// this from the display path and never include it in a timing.
        /// </summary>
        public bool TryReadAllCells(Span<uint> destination)
        {
            if (destination.Length < CellCount)
                return false;

            front.GetData(readbackScratch);
            for (int i = 0; i < CellCount; i++)
                destination[i] = readbackScratch[i];

            return true;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            front?.Release();
            back?.Release();
            front = null;
            back = null;
        }
    }
}
