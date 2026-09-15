using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife
{
    /// <summary>
    /// The board view.
    ///
    /// Primary path: the state buffer is drawn by the <c>Render</c> kernel into a
    /// viewport-sized texture, and the element shows that texture. The GPU
    /// backend's own buffer is bound directly, so displaying a 1024x1024 board
    /// costs no CPU readback at all. The CPU backend uploads its cells into the
    /// renderer's buffer and uses the very same kernel, so the two backends look
    /// identical; <see cref="LastUploadMilliseconds"/> records that upload cost
    /// separately, as required.
    ///
    /// Fallback path: if compute shaders are unavailable the element falls back
    /// to per-cell Painter2D drawing so the app stays usable. It is deliberately
    /// a fallback, and <see cref="UsesTexturePath"/> reports which one is live --
    /// the terminal shows it rather than pretending.
    ///
    /// Pan and zoom are integer pixels-per-cell, never below 1, so a visible cell
    /// never becomes smaller than a screen pixel and the board is never sampled
    /// down.
    /// </summary>
    public sealed class LifeGridElement : VisualElement
    {
        // Palette shared with LifeBoardRenderer.
        private static readonly Color ScreenColor = new(0.11f, 0.14f, 0.13f);
        private static readonly Color GridColor = new(0.19f, 0.24f, 0.21f);
        private static readonly Color CellColor = new(0.62f, 0.76f, 0.65f);
        private static readonly Color CellHighlight = new(0.80f, 0.82f, 0.60f);

        private const int MinCellPixels = 1;
        private const int MaxCellPixels = 64;

        private ILifeBackend backend;
        private LifeBoardRenderer renderer;
        private uint[] uploadCells;
        private uint[] probeCells;
        private uint[] previewScratch;
        private bool previewActive;
        private bool uploadPending = true;
        private float panelScale = 1f;

        private int originX;
        private int originY;
        private int cellPixels = 2;
        private int requestedZoom;
        private int lastViewportWidth;
        private int lastViewportHeight;

        private bool painting;
        private bool paintValue;
        private bool panning;
        private bool centerPending;
        private bool editingEnabled = true;
        private int diagnosticLogs;
        private Vector2 panAnchor;

        public System.Action Edited { get; set; }

        /// <summary>
        /// Cost of the last CPU->GPU state upload, in milliseconds. Zero while the
        /// GPU backend is live (it renders straight from its own buffer).
        /// </summary>
        public float LastUploadMilliseconds { get; private set; }

        /// <summary>True when the shader/texure path is live; false when drawing per cell.</summary>
        public bool UsesTexturePath => renderer != null;

        public int OriginX => originX;
        public int OriginY => originY;
        public int CellPixels => cellPixels;
        public float PanelScale => panelScale;

        /// <summary>The backend this board is bound to. Used by the performance probe.</summary>
        public ILifeBackend Backend => backend;

        public LifeGridElement()
        {
            focusable = true;
            pickingMode = PickingMode.Position;
            generateVisualContent += DrawGrid;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerUp);
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        public void Bind(ILifeBackend value)
        {
            backend = value;
            uploadCells = null;
            uploadPending = true;
            ResetView();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Supplies the shared texture renderer, or null to force the per-cell
        /// fallback. The terminal owns the renderer because it also decides which
        /// backend is live.
        /// </summary>
        public void AttachRenderer(LifeBoardRenderer value, float panelToScreenScale)
        {
            renderer = value;
            panelScale = panelToScreenScale > 0f ? panelToScreenScale : 1f;
            uploadPending = true;
            Refresh();
        }

        public void SetPanelScale(float value)
        {
            if (value <= 0f || Mathf.Approximately(value, panelScale))
                return;

            panelScale = value;
            Refresh();
        }

        /// <summary>Re-draws the board. Call after any change to the underlying state.</summary>
        public void Refresh()
        {
            if (backend == null || renderer == null)
            {
                MarkDirtyRepaint();
                return;
            }

            // contentRect is NaN until the element has been through layout, and
            // NaN fails every "<= 0" test, so it has to be checked explicitly.
            float width = contentRect.width;
            float height = contentRect.height;
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f)
                return;

            int viewportWidth = Mathf.Max(1, Mathf.RoundToInt(width * panelScale));
            int viewportHeight = Mathf.Max(1, Mathf.RoundToInt(height * panelScale));

            // A viewport size change re-frames the board. Without this, switching
            // between the wide and stacked layouts kept the old origin, which on a
            // 256-row board could put every visible row outside the board and render
            // the whole panel as flat screen colour.
            if (viewportWidth != lastViewportWidth || viewportHeight != lastViewportHeight)
            {
                lastViewportWidth = viewportWidth;
                lastViewportHeight = viewportHeight;
                centerPending = true;
            }

            // A centring request made before the first layout is honoured here,
            // once the element actually has a size.
            ApplyPendingCenter();

            renderer.EnsureBoard(backend.Width, backend.Height);
            renderer.EnsureViewport(viewportWidth, viewportHeight);

            ComputeBuffer state = ResolveStateBuffer();
            if (state == null)
                return;

            renderer.Render(state, originX, originY, cellPixels, decorations: true, preview: previewActive);

            Background background = Background.FromRenderTexture(renderer.Target);
            style.backgroundImage = new StyleBackground(background);

            if (diagnosticLogs < 6)
            {
                diagnosticLogs++;
                UnityEngine.Debug.Log($"[Life] board render #{diagnosticLogs}: rect={width:F0}x{height:F0} " +
                          $"panelScale={panelScale:F3} viewport={viewportWidth}x{viewportHeight} " +
                          $"rt={(renderer.Target != null ? renderer.Target.width + "x" + renderer.Target.height : "null")} " +
                          $"origin=({originX},{originY}) cellPixels={cellPixels} backend={backend.Name}");
            }
        }

        /// <summary>
        /// Returns the buffer to display. The GPU backend's buffer is used as-is.
        /// The CPU backend has to be uploaded, and that upload is timed here so it
        /// can be reported separately from evolution.
        /// </summary>
        private ComputeBuffer ResolveStateBuffer()
        {
            if (previewActive)
            {
                // The upload buffer already holds the candidate, written by
                // ShowPreview. The backend is deliberately not consulted, so a
                // preview cannot disturb the real board.
                LastUploadMilliseconds = 0f;
                return renderer.UploadBuffer;
            }

            if (backend is GpuLifeBackend gpu)
            {
                // Nothing to upload: the display kernel reads the backend's own
                // buffer, so the GPU path never round-trips through the CPU.
                LastUploadMilliseconds = 0f;
                uploadPending = false;
                return gpu.CurrentBuffer;
            }

            if (!uploadPending)
                return renderer.UploadBuffer;

            int count = backend.Width * backend.Height;
            if (uploadCells == null || uploadCells.Length != count)
                uploadCells = new uint[count];

            Stopwatch watch = Stopwatch.StartNew();
            if (!backend.TryReadAllCells(uploadCells))
            {
                LastUploadMilliseconds = 0f;
                return renderer.UploadBuffer;
            }

            renderer.Upload(uploadCells);
            watch.Stop();

            LastUploadMilliseconds = (float)watch.Elapsed.TotalMilliseconds;
            uploadPending = false;
            return renderer.UploadBuffer;
        }

        /// <summary>Marks the CPU-side state as changed so the next refresh re-uploads.</summary>
        public void MarkBoardDirty()
        {
            uploadPending = true;
            Refresh();
        }

        // -- seeding preview ---------------------------------------------------

        /// <summary>
        /// Shows a candidate board without touching the backend at all: the cells go
        /// into the renderer's own upload buffer and the pass runs in the preview
        /// colour. This is what makes "adjust parameters, then decide" safe -- the
        /// real board, its generation counter and its population are all untouched
        /// until the candidate is applied.
        /// </summary>
        public void ShowPreview(ReadOnlySpan<byte> cells, int width, int height)
        {
            if (renderer == null || backend == null)
                return;
            if (width != backend.Width || height != backend.Height)
                return;
            if (cells.Length < width * height)
                return;

            int count = width * height;
            if (previewScratch == null || previewScratch.Length != count)
                previewScratch = new uint[count];

            for (int i = 0; i < count; i++)
                previewScratch[i] = cells[i] != 0 ? 1u : 0u;

            renderer.EnsureBoard(width, height);
            renderer.Upload(previewScratch);

            previewActive = true;
            Refresh();
        }

        /// <summary>Drops the candidate and goes back to showing the real board.</summary>
        public void ClearPreview()
        {
            if (!previewActive)
                return;

            previewActive = false;

            // The CPU path has to re-upload the real board; the upload buffer
            // currently holds the discarded candidate.
            uploadPending = true;
            Refresh();
        }

        public bool PreviewActive => previewActive;

        /// <summary>
        /// Turns hand-editing on or off. The terminal switches it off while a seeding
        /// candidate is on screen: painting would change the real board underneath a
        /// picture that no longer describes it. Panning and zooming stay available --
        /// they only move the view.
        /// </summary>
        public void SetEditingEnabled(bool value) => editingEnabled = value;

        public bool EditingEnabled => editingEnabled;

        // -- view transform ----------------------------------------------------

        public void ResetView()
        {
            originX = 0;
            originY = 0;
            cellPixels = 2;
            requestedZoom = 0;
            Refresh();
        }

        /// <summary>
        /// Overrides the initial zoom, in screen pixels per cell. 0 means "fit the
        /// whole board if it fits". Used by evidence captures, which need the grid
        /// gap to be visible and therefore at least three pixels per cell.
        /// </summary>
        public void SetInitialZoom(int pixelsPerCell) => requestedZoom = pixelsPerCell;

        /// <summary>
        /// Puts the middle of the board in the middle of the viewport. On a large
        /// board the default view would otherwise sit at the top-left corner and
        /// show nothing, because zoom is never allowed below one pixel per cell.
        /// </summary>
        public void CenterView()
        {
            centerPending = true;
            Refresh();
        }

        private void ApplyPendingCenter()
        {
            if (!centerPending || backend == null)
                return;

            // Start at the largest integer zoom that still fits the whole board,
            // clamped to the one-pixel-per-cell floor. Below three pixels per cell
            // the shader drops the grid gap, so a board that fits at 2x is drawn
            // as solid cells rather than a grid.
            if (requestedZoom > 0)
            {
                cellPixels = Mathf.Clamp(requestedZoom, MinCellPixels, MaxCellPixels);
            }
            else
            {
                int fitX = Mathf.FloorToInt(contentRect.width * panelScale / backend.Width);
                int fitY = Mathf.FloorToInt(contentRect.height * panelScale / backend.Height);
                cellPixels = Mathf.Clamp(Mathf.Min(fitX, fitY), MinCellPixels, 16);
            }

            int visibleX = Mathf.Max(1, Mathf.RoundToInt(contentRect.width * panelScale / cellPixels));
            int visibleY = Mathf.Max(1, Mathf.RoundToInt(contentRect.height * panelScale / cellPixels));

            originX = (backend.Width - visibleX) / 2;
            originY = (backend.Height - visibleY) / 2;
            ClampOrigin();
            centerPending = false;
        }

        public void PanBy(int cellsX, int cellsY)
        {
            originX += cellsX;
            originY += cellsY;
            ClampOrigin();
            Refresh();
        }

        public void ZoomBy(int steps)
        {
            int target = Mathf.Clamp(cellPixels + steps, MinCellPixels, MaxCellPixels);
            if (target == cellPixels)
                return;

            cellPixels = target;
            ClampOrigin();
            Refresh();
        }

        private void ClampOrigin()
        {
            if (backend == null)
                return;

            float width = contentRect.width;
            float height = contentRect.height;
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f)
            {
                // No layout yet, so there is no viewport to keep the board inside of.
                originX = Mathf.Clamp(originX, -backend.Width + 1, backend.Width - 1);
                originY = Mathf.Clamp(originY, -backend.Height + 1, backend.Height - 1);
                return;
            }

            // At least one cell must stay on screen. The previous bound allowed the
            // board to be panned entirely out of view, which reads as a broken display
            // rather than as an empty region.
            int visibleX = Mathf.Max(1, Mathf.RoundToInt(width * panelScale / Mathf.Max(1, cellPixels)));
            int visibleY = Mathf.Max(1, Mathf.RoundToInt(height * panelScale / Mathf.Max(1, cellPixels)));

            originX = Mathf.Clamp(originX, -(visibleX - 1), backend.Width - 1);
            originY = Mathf.Clamp(originY, -(visibleY - 1), backend.Height - 1);
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) => Refresh();

        private void OnWheel(WheelEvent evt)
        {
            if (backend == null)
                return;

            Vector2 pointerPixels = evt.localMousePosition * panelScale;

            // Keep whatever cell sits under the pointer pinned while zooming.
            float anchorCellX = originX + pointerPixels.x / cellPixels;
            float anchorCellY = originY + pointerPixels.y / cellPixels;

            int steps = evt.delta.y > 0f ? 1 : -1;
            int target = Mathf.Clamp(cellPixels + steps, MinCellPixels, MaxCellPixels);
            if (target == cellPixels)
                return;

            cellPixels = target;

            originX = Mathf.FloorToInt(anchorCellX - pointerPixels.x / cellPixels);
            originY = Mathf.FloorToInt(anchorCellY - pointerPixels.y / cellPixels);
            ClampOrigin();

            Refresh();
            evt.StopPropagation();
        }

        // -- painting and panning ---------------------------------------------

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (backend == null)
                return;

            if (evt.button == 1 || evt.button == 2)
            {
                panning = true;
                panAnchor = evt.localPosition;
                this.CapturePointer(evt.pointerId);
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0 || !editingEnabled || !TryGetCell(evt.localPosition, out int x, out int y))
                return;

            painting = true;
            paintValue = !IsAlive(x, y);
            this.CapturePointer(evt.pointerId);
            Paint(x, y);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (panning)
            {
                Vector2 delta = (Vector2)evt.localPosition - panAnchor;
                panAnchor = evt.localPosition;

                float pixelDeltaX = delta.x * panelScale;
                float pixelDeltaY = delta.y * panelScale;
                originX -= Mathf.RoundToInt(pixelDeltaX / cellPixels);
                originY -= Mathf.RoundToInt(pixelDeltaY / cellPixels);
                ClampOrigin();
                Refresh();
                return;
            }

            if (painting && TryGetCell(evt.localPosition, out int x, out int y))
                Paint(x, y);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            EndPointerInteraction(evt.pointerId);
        }

        private void OnPointerUp(PointerCancelEvent evt)
        {
            EndPointerInteraction(evt.pointerId);
        }

        private void EndPointerInteraction(int pointerId)
        {
            painting = false;
            panning = false;
            if (this.HasPointerCapture(pointerId))
                this.ReleasePointer(pointerId);
        }

        private void Paint(int x, int y)
        {
            if (backend == null || !editingEnabled || IsAlive(x, y) == paintValue)
                return;

            backend.SetCell(x, y, paintValue);
            MarkBoardDirty();
            Edited?.Invoke();
        }

        /// <summary>
        /// Paints a cell as if the user had clicked it, using the same code path as
        /// a real edit.
        ///
        /// Exists because UI Toolkit pointer events cannot be synthesized from a
        /// test assembly: PointerEventBase's position/button setters are internal,
        /// and GetPooled's overloads are internal too. Without this seam the
        /// edit->controller wiring would be untestable.
        /// </summary>
        internal void PaintForTest(int x, int y)
        {
            if (backend == null || !editingEnabled || !IsInside(x, y))
                return;

            paintValue = !IsAlive(x, y);
            Paint(x, y);
        }

        private bool IsAlive(int x, int y)
        {
            if (backend == null || !IsInside(x, y))
                return false;

            // A single-cell query costs one full read. That is acceptable for a
            // click -- it is not on the per-frame path -- but the scratch array is
            // reused so a click does not allocate the board on every call.
            int count = backend.Width * backend.Height;
            if (probeCells == null || probeCells.Length != count)
                probeCells = new uint[count];

            return backend.TryReadAllCells(probeCells) && probeCells[y * backend.Width + x] != 0;
        }

        private bool IsInside(int x, int y)
        {
            return backend != null && x >= 0 && x < backend.Width && y >= 0 && y < backend.Height;
        }

        private bool TryGetCell(Vector2 position, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (backend == null || cellPixels <= 0)
                return false;

            Vector2 pixels = position * panelScale;
            x = originX + Mathf.FloorToInt(pixels.x / cellPixels);
            y = originY + Mathf.FloorToInt(pixels.y / cellPixels);
            return IsInside(x, y);
        }

        // -- fallback: per-cell drawing ---------------------------------------

        private void DrawGrid(MeshGenerationContext context)
        {
            // The texture path paints itself through style.backgroundImage.
            if (renderer != null || backend == null)
                return;

            if (contentRect.width <= 0f || contentRect.height <= 0f)
                return;

            Painter2D painter = context.painter2D;
            painter.fillColor = ScreenColor;
            FillRect(painter, contentRect);

            float cellSize = cellPixels / panelScale;
            float originPixelX = -originX * cellSize;
            float originPixelY = -originY * cellSize;
            float inset = Mathf.Max(0.55f, cellSize * 0.08f);

            int firstX = Mathf.Max(0, originX);
            int firstY = Mathf.Max(0, originY);
            int lastX = Mathf.Min(backend.Width - 1, originX + Mathf.CeilToInt(contentRect.width / cellSize));
            int lastY = Mathf.Min(backend.Height - 1, originY + Mathf.CeilToInt(contentRect.height / cellSize));

            uint[] cells = new uint[backend.Width * backend.Height];
            bool haveCells = backend.TryReadAllCells(cells);

            for (int y = firstY; y <= lastY; y++)
            {
                for (int x = firstX; x <= lastX; x++)
                {
                    Rect rect = new(
                        originPixelX + x * cellSize + inset,
                        originPixelY + y * cellSize + inset,
                        cellSize - inset * 2f,
                        cellSize - inset * 2f);

                    bool alive = haveCells && cells[y * backend.Width + x] != 0;
                    painter.fillColor = alive ? CellColor : GridColor;
                    FillRect(painter, rect);
                }
            }

            painter.strokeColor = CellHighlight;
            painter.lineWidth = 1f;
            StrokeRect(painter, new Rect(originPixelX, originPixelY,
                cellSize * backend.Width, cellSize * backend.Height));
        }

        private static void FillRect(Painter2D painter, Rect rect)
        {
            DrawRectPath(painter, rect);
            painter.Fill();
        }

        private static void StrokeRect(Painter2D painter, Rect rect)
        {
            DrawRectPath(painter, rect);
            painter.Stroke();
        }

        private static void DrawRectPath(Painter2D painter, Rect rect)
        {
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
        }
    }
}
