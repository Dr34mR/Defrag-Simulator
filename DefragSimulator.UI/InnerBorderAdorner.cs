using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DefragSimulator.UI
{
    /// <summary>
    /// Draws a thin inner border inside the adorned element's bounds.
    /// Used to add a 1px border inside the green Final border for visual polish.
    /// </summary>
    public class InnerBorderAdorner : Adorner
    {
        private Brush _borderBrush;
        private double _thickness;

        public InnerBorderAdorner(UIElement adornedElement, Brush borderBrush, double thickness = 1.0)
            : base(adornedElement)
        {
            _borderBrush = borderBrush;
            _thickness = thickness;
            IsHitTestVisible = false;
        }

        public void UpdateBrush(Brush brush)
        {
            _borderBrush = brush;
            InvalidateVisual();
        }

        public void UpdateThickness(double thickness)
        {
            _thickness = thickness;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_borderBrush == null) return;

            var size = AdornedElement.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;

            // Inset the rect slightly from the outer edge so it appears inside the existing border.
            // Using 1 device-independent pixel inset and drawing with specified thickness.
            double inset = _thickness; // keep a full thickness inset from the outer edge
            var rect = new Rect(inset, inset,
                size.Width - 2 * inset,
                size.Height - 2 * inset);

            if (rect.Width > 0 && rect.Height > 0)
            {
                var pen = new Pen(_borderBrush, _thickness);
                pen.Freeze();
                drawingContext.DrawRectangle(null, pen, rect);
            }
        }
    }
}
