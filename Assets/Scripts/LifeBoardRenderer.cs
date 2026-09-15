using System;
using UnityEngine;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Draws a board state buffer into a viewport-sized texture, using the
    /// <c>Render</c> kernel of LifeGpu.compute.
    ///
    /// This is the single display path for both backends:
    ///   * the GPU backend hands over its live front buffer -- no readback at all;
    ///   * the CPU backend uploads its cells into <see cref="UploadBuffer"/> and
    ///     hands that over, so both paths produce an identical picture.
    ///
    /// Pan and zoom happen inside the kernel. Zoom is an integer number of
    /// screen pixels per cell, never below 1, so a visible cell is never
    /// smaller than a pixel and the board is never sampled down.
    /// </summary>
    public sealed class LifeBoardRenderer : IDisposable
    {
        // Same palette as the Painter2D fallback in LifeGridElement, so the two
        // paths are visually comparable side by side.
        private static readonly Color ScreenColor = new(0.11f, 0.14f, 0.13f);
        private static readonly Color GridColor = new(0.19f, 0.24f, 0.21f);
        private static readonly Color CellColor = new(0.62f, 0.76f, 0.65f);
        private static readonly Color BorderColor = new(0.80f, 0.82f, 0.60f);

        /// <summary>
        /// Live cells while a seeding candidate is on screen. A restrained amber, so a
        /// candidate can never be mistaken for the confirmed board.
        /// </summary>
        private static readonly Color PreviewCellColor = new(0.84f, 0.70f, 0.42f);

        private const int ThreadGroupSize = GpuLifeBackend.ThreadGroupSize;

        private readonly ComputeShader shader;
        private readonly int renderKernel;

        private ComputeBuffer upload;
        private uint[] uploadScratch;
        private RenderTexture target;

        private int boardWidth;
        private int boardHeight;
        private int cellCount;
        private bool disposed;

        public static bool TryCreate(out LifeBoardRenderer renderer, out string error)
        {
            renderer = null;
            error = null;

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
                renderer = new LifeBoardRenderer(asset);
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
                renderer = null;
                return false;
            }

            return true;
        }

        private LifeBoardRenderer(ComputeShader asset)
        {
            shader = asset;
            renderKernel = shader.FindKernel("Render");
        }

        public RenderTexture Target => target;

        /// <summary>Buffer the CPU backend uploads into. Bind it to render; do not read it back.</summary>
        public ComputeBuffer UploadBuffer => upload;

        /// <summary>Allocates the board-sized upload buffer. Only on init or a size change.</summary>
        public void EnsureBoard(int width, int height)
        {
            if (upload != null && boardWidth == width && boardHeight == height)
                return;

            ReleaseBoard();

            boardWidth = width;
            boardHeight = height;
            cellCount = width * height;
            uploadScratch = new uint[cellCount];
            upload = new ComputeBuffer(cellCount, sizeof(uint));
        }

        /// <summary>Allocates the viewport texture. Only on init or a size change.</summary>
        public void EnsureViewport(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);

            if (target != null && target.width == width && target.height == height)
                return;

            ReleaseViewport();

            // Linear read/write: a random-write texture cannot be sRGB-flagged on
            // every backend, so the palette is converted to linear on the way in
            // and Unity converts it back on display.
            target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "LifeBoardViewport",
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            target.Create();
        }

        public void Upload(ReadOnlySpan<uint> cells)
        {
            if (upload == null || cells.Length < cellCount)
                return;

            for (int i = 0; i < cellCount; i++)
                uploadScratch[i] = cells[i] != 0 ? 1u : 0u;

            upload.SetData(uploadScratch);
        }

        /// <summary>
        /// Renders the current state. <paramref name="cellPixels"/> is clamped to
        /// at least 1 so a cell never shrinks below one screen pixel.
        /// <paramref name="preview"/> swaps the live-cell colour for the candidate
        /// amber; it changes nothing else about the pass.
        /// </summary>
        public void Render(ComputeBuffer state, int originX, int originY, int cellPixels, bool decorations, bool preview)
        {
            if (target == null || state == null)
                return;

            shader.SetInt("_Width", boardWidth);
            shader.SetInt("_Height", boardHeight);
            shader.SetInt("_ViewportW", target.width);
            shader.SetInt("_ViewportH", target.height);
            shader.SetInt("_OriginX", originX);
            shader.SetInt("_OriginY", originY);
            shader.SetInt("_CellPixels", Mathf.Max(1, cellPixels));
            shader.SetInt("_Decorations", decorations ? 1 : 0);

            shader.SetVector("_ScreenColor", ToLinear(ScreenColor));
            shader.SetVector("_GridColor", ToLinear(GridColor));
            shader.SetVector("_CellColor", ToLinear(preview ? PreviewCellColor : CellColor));
            shader.SetVector("_BorderColor", ToLinear(BorderColor));

            shader.SetBuffer(renderKernel, "_StateIn", state);
            shader.SetTexture(renderKernel, "_Display", target);

            int groupsX = (target.width + ThreadGroupSize - 1) / ThreadGroupSize;
            int groupsY = (target.height + ThreadGroupSize - 1) / ThreadGroupSize;
            shader.Dispatch(renderKernel, groupsX, groupsY, 1);
        }

        private static Vector4 ToLinear(Color color) => new(
            Mathf.GammaToLinearSpace(color.r),
            Mathf.GammaToLinearSpace(color.g),
            Mathf.GammaToLinearSpace(color.b),
            1f);

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            ReleaseBoard();
            ReleaseViewport();
        }

        private void ReleaseBoard()
        {
            upload?.Release();
            upload = null;
            uploadScratch = null;
            cellCount = 0;
        }

        private void ReleaseViewport()
        {
            if (target == null)
                return;

            target.Release();
            UnityEngine.Object.Destroy(target);
            target = null;
        }
    }
}
