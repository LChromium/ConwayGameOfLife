using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife
{
    public sealed class LifeGridElement : VisualElement
    {
        private static readonly Color ScreenColor = new(0.11f, 0.14f, 0.13f);
        private static readonly Color GridColor = new(0.19f, 0.24f, 0.21f);
        private static readonly Color CellColor = new(0.62f, 0.76f, 0.65f);
        private static readonly Color CellHighlight = new(0.80f, 0.82f, 0.60f);

        private LifeSimulation simulation;
        private bool painting;
        private bool paintValue;

        public System.Action Edited { get; set; }

        public LifeGridElement()
        {
            focusable = true;
            pickingMode = PickingMode.Position;
            generateVisualContent += DrawGrid;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerUp);
        }

        public void Bind(LifeSimulation value)
        {
            simulation = value;
            MarkDirtyRepaint();
        }

        private void DrawGrid(MeshGenerationContext context)
        {
            if (simulation == null || contentRect.width <= 0f || contentRect.height <= 0f)
                return;

            Painter2D painter = context.painter2D;
            painter.fillColor = ScreenColor;
            FillRect(painter, contentRect);

            float cellSize = Mathf.Min(contentRect.width / simulation.Width, contentRect.height / simulation.Height);
            float boardWidth = cellSize * simulation.Width;
            float boardHeight = cellSize * simulation.Height;
            Vector2 origin = new((contentRect.width - boardWidth) * 0.5f, (contentRect.height - boardHeight) * 0.5f);
            float inset = Mathf.Max(0.55f, cellSize * 0.08f);

            for (int y = 0; y < simulation.Height; y++)
            {
                for (int x = 0; x < simulation.Width; x++)
                {
                    Rect rect = new(origin.x + x * cellSize + inset, origin.y + y * cellSize + inset,
                        cellSize - inset * 2f, cellSize - inset * 2f);
                    painter.fillColor = simulation.IsAlive(x, y) ? CellColor : GridColor;
                    FillRect(painter, rect);
                }
            }

            painter.strokeColor = CellHighlight;
            painter.lineWidth = 1f;
            StrokeRect(painter, new Rect(origin.x, origin.y, boardWidth, boardHeight));
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

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (simulation == null || evt.button != 0 || !TryGetCell(evt.localPosition, out int x, out int y))
                return;

            painting = true;
            paintValue = !simulation.IsAlive(x, y);
            this.CapturePointer(evt.pointerId);
            Paint(x, y);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (painting && TryGetCell(evt.localPosition, out int x, out int y))
                Paint(x, y);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            painting = false;
            if (this.HasPointerCapture(evt.pointerId))
                this.ReleasePointer(evt.pointerId);
        }

        private void OnPointerUp(PointerCancelEvent evt)
        {
            painting = false;
            if (this.HasPointerCapture(evt.pointerId))
                this.ReleasePointer(evt.pointerId);
        }

        private void Paint(int x, int y)
        {
            if (simulation.IsAlive(x, y) == paintValue)
                return;

            simulation.SetCell(x, y, paintValue);
            MarkDirtyRepaint();
            Edited?.Invoke();
        }

        /// <summary>
        /// Paints a cell as if the user had clicked it, using the same code path as a real edit.
        ///
        /// Exists because UI Toolkit pointer events cannot be synthesized from a test assembly:
        /// PointerEventBase's position/button setters are internal, and GetPooled's overloads are
        /// internal too. Without this seam the edit->controller wiring would be untestable.
        /// </summary>
        internal void PaintForTest(int x, int y)
        {
            if (simulation == null || !IsInside(x, y))
                return;

            paintValue = !simulation.IsAlive(x, y);
            Paint(x, y);
        }

        private bool IsInside(int x, int y)
        {
            return simulation != null
                   && x >= 0 && x < simulation.Width
                   && y >= 0 && y < simulation.Height;
        }

        private bool TryGetCell(Vector2 position, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (simulation == null)
                return false;

            float cellSize = Mathf.Min(contentRect.width / simulation.Width, contentRect.height / simulation.Height);
            if (cellSize <= 0f)
                return false;

            float boardWidth = cellSize * simulation.Width;
            float boardHeight = cellSize * simulation.Height;
            Vector2 origin = new((contentRect.width - boardWidth) * 0.5f, (contentRect.height - boardHeight) * 0.5f);
            x = Mathf.FloorToInt((position.x - origin.x) / cellSize);
            y = Mathf.FloorToInt((position.y - origin.y) / cellSize);
            return x >= 0 && x < simulation.Width && y >= 0 && y < simulation.Height;
        }
    }
}
