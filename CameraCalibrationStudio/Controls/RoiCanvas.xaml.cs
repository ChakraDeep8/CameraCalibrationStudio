using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CameraCalibrationStudio.Models.Roi;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CameraCalibrationStudio.Controls
{
    public enum ToolMode { Select, Rectangle, Square, Polygon, Line, Pan }

    /// <summary>
    /// Draws and edits calibration shapes directly over the source image. All shape geometry
    /// lives in ORIGINAL IMAGE pixel coordinates: the content grid (TransformRoot) is sized to
    /// the image's actual pixel dimensions and a single Scale+Translate RenderTransform maps it
    /// to the viewport, so WPF's own transform-aware hit testing (MouseEventArgs.GetPosition)
    /// hands back image-pixel coordinates directly — no manual zoom/pan math is needed anywhere
    /// shapes are read or written, which is what keeps calibration coordinates exact at any zoom.
    /// </summary>
    public partial class RoiCanvas : UserControl
    {
        private static readonly Color AccentColor = Color.FromRgb(0x3D, 0x8B, 0xFF);
        private static readonly Color IdleColor = Color.FromRgb(0xE6, 0xE6, 0xEA);

        public Models.Roi.RoiDocument? Document;
        public Services.RoiHistory? History;

        /// <summary>Resolves an object's class color for the canvas outline/label/handles, if it has one assigned.</summary>
        public Func<CalibrationObjectBase, Color?>? ClassColorResolver { get; set; }

        public event Action? Changed;
        public event Action<CalibrationObjectBase?>? SelectionChanged;
        public event Action<CalibrationObjectBase>? RequestNaming;
        /// <summary>Image-pixel coordinate under the cursor, or null when the cursor leaves the image.</summary>
        public event Action<Point?>? HoverPositionChanged;
        public event Action<double>? ZoomChanged;

        public ToolMode Tool { get; set; } = ToolMode.Select;
        public CalibrationObjectBase? Selected { get; private set; }
        public double CurrentZoomPercent => Scale.ScaleX * 100.0;

        private int _imageWidth, _imageHeight;

        /// <summary>
        /// True from image load until the user explicitly zooms/pans. While true, every viewport
        /// size change (window resize, maximize/restore completing, tab switch reflow, DPI change)
        /// re-fits the image. This is what keeps the image correctly scaled even when the actual
        /// maximize happens on the OS side *after* the first layout pass — a single one-shot fit
        /// could otherwise fit against a stale, too-small pre-maximize viewport size and never
        /// correct itself, which showed up as the image looking wrong-sized/"cropped".
        /// </summary>
        private bool _autoFitMode;
        private CalibrationObjectBase? _pendingNewShape;

        // drag state
        private bool _isDrawingDrag;
        private Point _dragStart;
        private UIElement? _previewVisual;

        private readonly List<Point> _polygonPoints = new();

        private bool _isPanning;
        private Point _panMouseStart;
        private double _panTranslateStartX, _panTranslateStartY;

        private bool _isMovingShape;
        private Point _moveLastPoint;

        private bool _isDraggingHandle;
        private int _dragHandleIndex;

        public RoiCanvas()
        {
            InitializeComponent();
        }

        // =====================================================================
        // Image loading / preview
        // =====================================================================

        public void LoadImage(BitmapSource bitmap, int originalWidth, int originalHeight)
        {
            _imageWidth = originalWidth;
            _imageHeight = originalHeight;
            _autoFitMode = true;
            TransformRoot.Width = originalWidth;
            TransformRoot.Height = originalHeight;
            BackgroundImage.Width = originalWidth;
            BackgroundImage.Height = originalHeight;
            BackgroundImage.Source = bitmap;
            ShapesCanvas.Width = originalWidth;
            ShapesCanvas.Height = originalHeight;

            // Shows the image at its true native resolution (100%) whenever it already fits the
            // canvas — no unnecessary downscaling. Only images actually larger than the canvas
            // get scaled down (never up: a small image is never blown up/blurred to fill space).
            // This guarantees the full frame is always visible with nothing cropped, while still
            // showing pixel-for-pixel native detail whenever there's room for it.
            if (Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
            {
                FitWithoutUpscaling();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(FitWithoutUpscaling), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        public void SetPreviewImage(BitmapSource bitmap) => BackgroundImage.Source = bitmap;

        // =====================================================================
        // Zoom / pan
        // =====================================================================

        /// <summary>Scales down to fit the canvas only if the image is larger than it; never scales up past 100%.</summary>
        private void FitWithoutUpscaling()
        {
            if (_imageWidth <= 0 || _imageHeight <= 0 || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
            double fitScale = Math.Min(Viewport.ActualWidth / _imageWidth, Viewport.ActualHeight / _imageHeight);
            double scale = Math.Clamp(Math.Min(fitScale, 1.0), 0.02, 8.0);
            SetZoom(scale, centerInViewport: true);
        }

        /// <summary>Explicit "Fit" action (button / 'F' key) — scales to fill the canvas, including upscaling small images.</summary>
        public void FitToWindow()
        {
            if (_imageWidth <= 0 || _imageHeight <= 0 || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
            double scale = Math.Min(Viewport.ActualWidth / _imageWidth, Viewport.ActualHeight / _imageHeight);
            scale = Math.Clamp(scale, 0.02, 8.0);
            SetZoom(scale, centerInViewport: true);
            _autoFitMode = true; // Fit re-engages auto-correction on future resizes
        }

        /// <summary>
        /// Call after a layout-only change that resizes the canvas but isn't a deliberate zoom
        /// action — e.g. collapsing/expanding the JSON side panel, or dragging the column
        /// splitters. Re-fits (no upscale) once the resulting layout has actually happened, and
        /// re-arms auto-fit mode, so the image can never end up clipped/"cropped" by a viewport
        /// that shrank out from under a zoom level the user never chose on purpose.
        /// </summary>
        public void RefitAfterLayoutChange()
        {
            if (_imageWidth <= 0) return;
            _autoFitMode = true;
            Dispatcher.BeginInvoke(new Action(FitWithoutUpscaling), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public void SetZoomPercent(double percent)
        {
            _autoFitMode = false; // explicit zoom — stop auto-correcting on resize
            SetZoom(percent / 100.0, centerInViewport: true);
        }

        private void SetZoom(double scale, bool centerInViewport)
        {
            scale = Math.Clamp(scale, 0.02, 8.0);
            Scale.ScaleX = scale;
            Scale.ScaleY = scale;

            if (centerInViewport)
            {
                Translate.X = Math.Max(0, (Viewport.ActualWidth - _imageWidth * scale) / 2.0);
                Translate.Y = Math.Max(0, (Viewport.ActualHeight - _imageHeight * scale) / 2.0);
            }

            RedrawAll();
            ZoomChanged?.Invoke(CurrentZoomPercent);
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_imageWidth <= 0) return;
            _autoFitMode = false; // explicit zoom — stop auto-correcting on resize
            Point imagePoint = e.GetPosition(TransformRoot);
            Point viewportPoint = e.GetPosition(Viewport);

            double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            double newScale = Math.Clamp(Scale.ScaleX * factor, 0.02, 8.0);

            Scale.ScaleX = newScale;
            Scale.ScaleY = newScale;
            Translate.X = viewportPoint.X - imagePoint.X * newScale;
            Translate.Y = viewportPoint.Y - imagePoint.Y * newScale;

            RedrawAll();
            ZoomChanged?.Invoke(CurrentZoomPercent);
            e.Handled = true;
        }

        private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // While in auto-fit mode (from image load until the user manually zooms/pans/clicks
            // Fit again), keep re-fitting on every viewport size change. This covers not just the
            // very first layout pass but also the window's maximize animation completing *after*
            // that first pass — a one-shot fit could otherwise lock in against a stale, too-small
            // pre-maximize size and never correct, which looked like the image being cropped or
            // undersized.
            if (_autoFitMode && _imageWidth > 0 && Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
                FitWithoutUpscaling();
        }

        // =====================================================================
        // Mouse interaction
        // =====================================================================

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Viewport.Focus();
            if (_imageWidth <= 0) return;
            var pos = e.GetPosition(TransformRoot);

            if (e.ClickCount == 2 && Tool == ToolMode.Polygon && _polygonPoints.Count >= 3)
            {
                FinishPolygon();
                return;
            }

            bool spacePan = Keyboard.IsKeyDown(Key.Space);
            if (Tool == ToolMode.Pan || spacePan)
            {
                StartPan(e);
                return;
            }

            switch (Tool)
            {
                case ToolMode.Select:
                    HandleSelectMouseDown(pos);
                    break;
                case ToolMode.Rectangle:
                case ToolMode.Square:
                case ToolMode.Line:
                    _isDrawingDrag = true;
                    _dragStart = Clamp(pos);
                    Viewport.CaptureMouse();
                    break;
                case ToolMode.Polygon:
                    HandlePolygonClick(Clamp(pos));
                    break;
            }
        }

        private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Tool == ToolMode.Polygon && _polygonPoints.Count >= 3)
                FinishPolygon();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_imageWidth <= 0)
            {
                HoverPositionChanged?.Invoke(null);
                return;
            }

            var pos = e.GetPosition(TransformRoot);
            bool inside = pos.X >= 0 && pos.Y >= 0 && pos.X <= _imageWidth && pos.Y <= _imageHeight;
            HoverPositionChanged?.Invoke(inside ? pos : null);

            if (_isPanning) { UpdatePan(e); return; }

            if (_isDrawingDrag)
            {
                UpdateDrawPreview(Clamp(pos));
                return;
            }

            if (Tool == ToolMode.Polygon && _polygonPoints.Count > 0)
            {
                UpdatePolygonRubberBand(Clamp(pos));
                return;
            }

            if (_isMovingShape && Selected != null)
            {
                var clamped = Clamp(pos);
                double dx = clamped.X - _moveLastPoint.X, dy = clamped.Y - _moveLastPoint.Y;
                Selected.Translate(dx, dy);
                _moveLastPoint = clamped;
                RedrawAll();
                Changed?.Invoke();
                return;
            }

            if (_isDraggingHandle && Selected != null)
            {
                UpdateHandleDrag(Clamp(pos));
                RedrawAll();
                Changed?.Invoke();
            }
        }

        private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning) { EndPan(); return; }

            if (_isDrawingDrag)
            {
                _isDrawingDrag = false;
                Viewport.ReleaseMouseCapture();
                FinishDrag(Clamp(e.GetPosition(TransformRoot)));
                return;
            }

            _isMovingShape = false;
            _isDraggingHandle = false;
        }

        private void Viewport_MouseLeave(object sender, MouseEventArgs e) => HoverPositionChanged?.Invoke(null);

        private void Viewport_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Tool == ToolMode.Polygon)
            {
                if (e.Key == Key.Enter && _polygonPoints.Count >= 3) { FinishPolygon(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelPolygon(); e.Handled = true; }
                else if (e.Key == Key.Back && _polygonPoints.Count > 0)
                {
                    _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
                    RedrawAll();
                    e.Handled = true;
                }
            }
        }

        private Point Clamp(Point p) => new(
            Math.Clamp(p.X, 0, _imageWidth),
            Math.Clamp(p.Y, 0, _imageHeight));

        // =====================================================================
        // Pan
        // =====================================================================

        private void StartPan(MouseButtonEventArgs e)
        {
            _autoFitMode = false; // explicit pan — stop auto-correcting on resize
            _isPanning = true;
            _panMouseStart = e.GetPosition(Viewport);
            _panTranslateStartX = Translate.X;
            _panTranslateStartY = Translate.Y;
            Viewport.CaptureMouse();
        }

        private void UpdatePan(MouseEventArgs e)
        {
            var pos = e.GetPosition(Viewport);
            Translate.X = _panTranslateStartX + (pos.X - _panMouseStart.X);
            Translate.Y = _panTranslateStartY + (pos.Y - _panMouseStart.Y);
        }

        private void EndPan()
        {
            _isPanning = false;
            Viewport.ReleaseMouseCapture();
        }

        // =====================================================================
        // Rectangle / Square / Line drawing
        // =====================================================================

        private void UpdateDrawPreview(Point current)
        {
            if (_previewVisual != null) ShapesCanvas.Children.Remove(_previewVisual);

            if (Tool == ToolMode.Line)
            {
                var line = new Line
                {
                    X1 = _dragStart.X, Y1 = _dragStart.Y, X2 = current.X, Y2 = current.Y,
                    Stroke = new SolidColorBrush(AccentColor), StrokeThickness = ScreenSize(2),
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                _previewVisual = line;
            }
            else
            {
                double dx = current.X - _dragStart.X, dy = current.Y - _dragStart.Y;
                if (Tool == ToolMode.Square)
                {
                    double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    dx = Math.Sign(dx == 0 ? 1 : dx) * side;
                    dy = Math.Sign(dy == 0 ? 1 : dy) * side;
                }
                var rect = new Rectangle
                {
                    Width = Math.Abs(dx), Height = Math.Abs(dy),
                    Stroke = new SolidColorBrush(AccentColor), StrokeThickness = ScreenSize(2),
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(30, AccentColor.R, AccentColor.G, AccentColor.B))
                };
                Canvas.SetLeft(rect, Math.Min(_dragStart.X, _dragStart.X + dx));
                Canvas.SetTop(rect, Math.Min(_dragStart.Y, _dragStart.Y + dy));
                _previewVisual = rect;
            }

            ShapesCanvas.Children.Add(_previewVisual);
        }

        private void FinishDrag(Point end)
        {
            if (_previewVisual != null) { ShapesCanvas.Children.Remove(_previewVisual); _previewVisual = null; }

            if (Tool == ToolMode.Line)
            {
                if ((end - _dragStart).Length < 3) return; // too short, ignore accidental click
                _pendingNewShape = new LineObject { Start = _dragStart, End = end };
            }
            else
            {
                double dx = end.X - _dragStart.X, dy = end.Y - _dragStart.Y;
                if (Tool == ToolMode.Square)
                {
                    double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    dx = Math.Sign(dx == 0 ? 1 : dx) * side;
                    dy = Math.Sign(dy == 0 ? 1 : dy) * side;
                }
                if (Math.Abs(dx) < 3 || Math.Abs(dy) < 3) return; // ignore accidental tiny drags

                _pendingNewShape = new RectangleObject
                {
                    X1 = _dragStart.X, Y1 = _dragStart.Y,
                    X2 = _dragStart.X + dx, Y2 = _dragStart.Y + dy,
                    IsSquare = Tool == ToolMode.Square
                };
                ((RectangleObject)_pendingNewShape).Normalize();
            }

            RequestNaming?.Invoke(_pendingNewShape);
        }

        // =====================================================================
        // Polygon drawing
        // =====================================================================

        private void HandlePolygonClick(Point pos)
        {
            _polygonPoints.Add(pos);
            RedrawAll();
        }

        private void UpdatePolygonRubberBand(Point current)
        {
            RedrawAll();
            if (_polygonPoints.Count == 0) return;

            var last = _polygonPoints[^1];
            var rubberBand = new Line
            {
                X1 = last.X, Y1 = last.Y, X2 = current.X, Y2 = current.Y,
                Stroke = new SolidColorBrush(AccentColor), StrokeThickness = ScreenSize(1.5),
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            ShapesCanvas.Children.Add(rubberBand);
        }

        private void FinishPolygon()
        {
            _pendingNewShape = new PolygonObject { Points = _polygonPoints.ToList() };
            _polygonPoints.Clear();
            RedrawAll();
            RequestNaming?.Invoke(_pendingNewShape);
        }

        private void CancelPolygon()
        {
            _polygonPoints.Clear();
            RedrawAll();
        }

        /// <summary>Host calls this after the user confirms a name for a just-drawn shape.</summary>
        public void CommitPendingShape()
        {
            if (_pendingNewShape == null || Document == null) return;
            History?.Snapshot(Document.Objects);
            Document.Objects.Add(_pendingNewShape);
            var created = _pendingNewShape;
            _pendingNewShape = null;
            RedrawAll();
            Select(created);
            Changed?.Invoke();
        }

        public void DiscardPendingShape()
        {
            _pendingNewShape = null;
            RedrawAll();
        }

        // =====================================================================
        // Select / move / resize / delete
        // =====================================================================

        private void HandleSelectMouseDown(Point pos)
        {
            if (Document == null) return;

            // 1. handle hit (only for the currently selected shape)
            if (Selected != null)
            {
                int handle = HitTestHandle(Selected, pos);
                if (handle >= 0)
                {
                    History?.Snapshot(Document.Objects);
                    _isDraggingHandle = true;
                    _dragHandleIndex = handle;
                    return;
                }
            }

            // 2. shape body hit (topmost first)
            var hit = Document.Objects.LastOrDefault(o => o.IsVisible && HitTestBody(o, pos));
            if (hit != null)
            {
                Select(hit);
                History?.Snapshot(Document.Objects);
                _isMovingShape = true;
                _moveLastPoint = pos;
                return;
            }

            Select(null);
        }

        private static bool HitTestBody(CalibrationObjectBase obj, Point p)
        {
            const double lineTolerance = 6;
            switch (obj)
            {
                case RectangleObject r:
                    return p.X >= Math.Min(r.X1, r.X2) && p.X <= Math.Max(r.X1, r.X2) &&
                           p.Y >= Math.Min(r.Y1, r.Y2) && p.Y <= Math.Max(r.Y1, r.Y2);
                case PolygonObject poly:
                    return IsPointInPolygon(poly.Points, p);
                case LineObject l:
                    return DistancePointToSegment(p, l.Start, l.End) <= lineTolerance;
                default:
                    return false;
            }
        }

        private static bool IsPointInPolygon(IReadOnlyList<Point> pts, Point p)
        {
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                if (((pts[i].Y > p.Y) != (pts[j].Y > p.Y)) &&
                    (p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X))
                    inside = !inside;
            }
            return inside;
        }

        private static double DistancePointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            double t = lenSq < 1e-6 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
            double px = a.X + t * dx, py = a.Y + t * dy;
            return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }

        private int HitTestHandle(CalibrationObjectBase obj, Point p)
        {
            double tolerance = 10 / Scale.ScaleX;
            var handles = GetHandlePoints(obj);
            for (int i = 0; i < handles.Count; i++)
                if ((handles[i] - p).Length <= tolerance) return i;
            return -1;
        }

        private static List<Point> GetHandlePoints(CalibrationObjectBase obj) => obj switch
        {
            RectangleObject r => new List<Point>
            {
                new(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2)),
                new(Math.Max(r.X1, r.X2), Math.Min(r.Y1, r.Y2)),
                new(Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2)),
                new(Math.Min(r.X1, r.X2), Math.Max(r.Y1, r.Y2)),
            },
            PolygonObject p => p.Points.ToList(),
            LineObject l => new List<Point> { l.Start, l.End },
            _ => new List<Point>()
        };

        private void UpdateHandleDrag(Point pos)
        {
            switch (Selected)
            {
                case RectangleObject r:
                    switch (_dragHandleIndex)
                    {
                        case 0: r.X1 = pos.X; r.Y1 = pos.Y; break;
                        case 1: r.X2 = pos.X; r.Y1 = pos.Y; break;
                        case 2: r.X2 = pos.X; r.Y2 = pos.Y; break;
                        case 3: r.X1 = pos.X; r.Y2 = pos.Y; break;
                    }
                    break;
                case PolygonObject poly when _dragHandleIndex < poly.Points.Count:
                    poly.Points[_dragHandleIndex] = pos;
                    break;
                case LineObject l:
                    if (_dragHandleIndex == 0) l.Start = pos; else l.End = pos;
                    break;
            }
        }

        public void Select(CalibrationObjectBase? obj)
        {
            Selected = obj;
            RedrawAll();
            SelectionChanged?.Invoke(obj);
        }

        public void DeleteSelected()
        {
            if (Selected == null || Document == null) return;
            History?.Snapshot(Document.Objects);
            Document.Objects.Remove(Selected);
            Selected = null;
            RedrawAll();
            SelectionChanged?.Invoke(null);
            Changed?.Invoke();
        }

        // =====================================================================
        // Undo / Redo (host calls these; canvas re-renders from the returned snapshot)
        // =====================================================================

        public void ReplaceObjects(IEnumerable<CalibrationObjectBase> objects)
        {
            if (Document == null) return;
            Document.Objects.Clear();
            foreach (var o in objects) Document.Objects.Add(o);
            Selected = null;
            RedrawAll();
            SelectionChanged?.Invoke(null);
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        public void RedrawAll()
        {
            ShapesCanvas.Children.Clear();
            if (Document == null) return;

            foreach (var obj in Document.Objects)
            {
                if (!obj.IsVisible) continue;
                DrawObject(obj, obj == Selected);
            }

            if (Tool == ToolMode.Polygon && _polygonPoints.Count > 0)
                DrawPolygonInProgress();

            if (Selected != null)
                DrawHandles(Selected);
        }

        private double ScreenSize(double desiredScreenPixels) => desiredScreenPixels / Math.Max(Scale.ScaleX, 0.001);

        private void DrawObject(CalibrationObjectBase obj, bool selected)
        {
            var classColor = ClassColorResolver?.Invoke(obj);
            var color = selected ? AccentColor : (classColor ?? IdleColor);
            var brush = new SolidColorBrush(color);
            double thickness = ScreenSize(selected ? 3 : 2);

            Shape? shape = obj switch
            {
                RectangleObject r => new Rectangle
                {
                    Width = Math.Abs(r.X2 - r.X1),
                    Height = Math.Abs(r.Y2 - r.Y1)
                },
                PolygonObject p => new Polygon { Points = new PointCollection(p.Points) },
                LineObject l => new Line { X1 = l.Start.X, Y1 = l.Start.Y, X2 = l.End.X, Y2 = l.End.Y },
                _ => null
            };
            if (shape == null) return;

            shape.Stroke = brush;
            shape.StrokeThickness = thickness;
            if (obj is RectangleObject or PolygonObject && selected)
                shape.Fill = new SolidColorBrush(Color.FromArgb(24, AccentColor.R, AccentColor.G, AccentColor.B));

            if (obj is RectangleObject rr)
            {
                Canvas.SetLeft(shape, Math.Min(rr.X1, rr.X2));
                Canvas.SetTop(shape, Math.Min(rr.Y1, rr.Y2));
            }

            ShapesCanvas.Children.Add(shape);

            // Label
            var bounds = obj.GetBounds();
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)),
                BorderBrush = brush,
                BorderThickness = new Thickness(ScreenSize(1)),
                Padding = new Thickness(ScreenSize(5), ScreenSize(2), ScreenSize(5), ScreenSize(2)),
                Child = new TextBlock
                {
                    Text = obj.Name,
                    Foreground = Brushes.White,
                    FontSize = ScreenSize(12)
                }
            };
            Canvas.SetLeft(label, bounds.Left);
            Canvas.SetTop(label, Math.Max(0, bounds.Top - ScreenSize(22)));
            ShapesCanvas.Children.Add(label);
        }

        private void DrawPolygonInProgress()
        {
            var poly = new Polyline
            {
                Points = new PointCollection(_polygonPoints),
                Stroke = new SolidColorBrush(AccentColor),
                StrokeThickness = ScreenSize(2)
            };
            ShapesCanvas.Children.Add(poly);

            foreach (var pt in _polygonPoints)
                ShapesCanvas.Children.Add(MakeHandleDot(pt, AccentColor));
        }

        private void DrawHandles(CalibrationObjectBase obj)
        {
            foreach (var pt in GetHandlePoints(obj))
                ShapesCanvas.Children.Add(MakeHandleDot(pt, AccentColor));
        }

        private Ellipse MakeHandleDot(Point pt, Color color)
        {
            double size = ScreenSize(9);
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = ScreenSize(2)
            };
            Canvas.SetLeft(dot, pt.X - size / 2);
            Canvas.SetTop(dot, pt.Y - size / 2);
            return dot;
        }
    }
}
