using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CameraCalibrationStudio.Models.Roi
{
    public enum ShapeKind { Rectangle, Square, Polygon, Line }

    /// <summary>
    /// Base type for every calibration object. All geometry is stored in ORIGINAL IMAGE
    /// pixel coordinates — never display/canvas coordinates. This is the single source of
    /// truth the canvas, object list, JSON preview and file export all read from.
    /// </summary>
    public abstract class CalibrationObjectBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Guid Id { get; } = Guid.NewGuid();

        private string _name = "";
        public string Name { get => _name; set { _name = value; Raise(); } }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set { _isVisible = value; Raise(); } }

        public abstract ShapeKind Kind { get; }

        public abstract CalibrationObjectBase Clone();

        /// <summary>Moves the whole shape by a pixel delta (used for drag-to-move).</summary>
        public abstract void Translate(double dx, double dy);

        /// <summary>Axis-aligned bounds in image pixel coordinates, for hit-testing/labels.</summary>
        public abstract Rect GetBounds();
    }

    public class RectangleObject : CalibrationObjectBase
    {
        public double X1, Y1, X2, Y2;
        public bool IsSquare;

        public override ShapeKind Kind => IsSquare ? ShapeKind.Square : ShapeKind.Rectangle;

        public void Normalize()
        {
            if (X1 > X2) (X1, X2) = (X2, X1);
            if (Y1 > Y2) (Y1, Y2) = (Y2, Y1);
        }

        public override void Translate(double dx, double dy)
        {
            X1 += dx; X2 += dx; Y1 += dy; Y2 += dy;
        }

        public override Rect GetBounds() => new(Math.Min(X1, X2), Math.Min(Y1, Y2), Math.Abs(X2 - X1), Math.Abs(Y2 - Y1));

        public override CalibrationObjectBase Clone() =>
            new RectangleObject { Name = Name, IsVisible = IsVisible, X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2, IsSquare = IsSquare };
    }

    public class PolygonObject : CalibrationObjectBase
    {
        public List<Point> Points { get; set; } = new();
        public override ShapeKind Kind => ShapeKind.Polygon;

        public override void Translate(double dx, double dy)
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        }

        public override Rect GetBounds()
        {
            if (Points.Count == 0) return Rect.Empty;
            double minX = Points.Min(p => p.X), maxX = Points.Max(p => p.X);
            double minY = Points.Min(p => p.Y), maxY = Points.Max(p => p.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        public override CalibrationObjectBase Clone() =>
            new PolygonObject { Name = Name, IsVisible = IsVisible, Points = Points.Select(p => p).ToList() };
    }

    public class LineObject : CalibrationObjectBase
    {
        public Point Start, End;
        public override ShapeKind Kind => ShapeKind.Line;

        public override void Translate(double dx, double dy)
        {
            Start = new Point(Start.X + dx, Start.Y + dy);
            End = new Point(End.X + dx, End.Y + dy);
        }

        public override Rect GetBounds()
        {
            double minX = Math.Min(Start.X, End.X), maxX = Math.Max(Start.X, End.X);
            double minY = Math.Min(Start.Y, End.Y), maxY = Math.Max(Start.Y, End.Y);
            return new Rect(minX, minY, Math.Max(maxX - minX, 1), Math.Max(maxY - minY, 1));
        }

        public override CalibrationObjectBase Clone() =>
            new LineObject { Name = Name, IsVisible = IsVisible, Start = Start, End = End };
    }
}
