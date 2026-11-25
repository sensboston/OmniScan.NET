using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.App;

/// <summary>A cut frame shown on the preview; <see cref="Rect"/> is in page-width units.</summary>
public sealed class EditableFrame : INotifyPropertyChanged
{
    private RotatedRect _rect;

    public EditableFrame(RotatedRect rect, ItemKind kind, bool manual = false, IReadOnlyList<(double X, double Y)>? outline = null)
    {
        _rect = rect;
        Kind = kind;
        Manual = manual;
        Outline = outline;
    }

    /// <summary>
    /// A sticker's outline (page-width units): shown instead of the frame until the frame is selected;
    /// gone once the frame is changed (it is then cut as drawn).
    /// </summary>
    public IReadOnlyList<(double X, double Y)>? Outline { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ItemKind Kind { get; }

    /// <summary>Drawn or changed by the user: cut exactly as shown.</summary>
    public bool Manual { get; private set; }

    public RotatedRect Rect
    {
        get => _rect;
        set
        {
            _rect = value;
            Manual = true;
            Outline = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rect)));
        }
    }

    public CutFrame ToCutFrame() => new(_rect, Kind, Manual);
}

/// <summary>
/// Shows the preview of the bed with the frames that will be cut, and lets the user change them:
/// drag inside a frame to move it, drag a corner or a side to resize, drag the round knob to turn,
/// drag on an empty spot to draw a new frame; Delete or the context menu removes the selected one.
/// </summary>
public sealed class FrameEditor : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(FrameEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FramesProperty = DependencyProperty.Register(
        nameof(Frames), typeof(ObservableCollection<EditableFrame>), typeof(FrameEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnFramesChanged));

    public static readonly DependencyProperty EditedCommandProperty = DependencyProperty.Register(
        nameof(EditedCommand), typeof(ICommand), typeof(FrameEditor));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(FrameEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Inset = 8;
    private const double HandleSize = 10;
    private const double KnobDistance = 26;
    private const double MinSide = 12;
    private const double SnapDegrees = 2.5;

    private static readonly Brush FrameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 200, 120)));
    private static readonly Brush HandleFill = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen FramePen = Freeze(new Pen(FrameBrush, 2));
    private static readonly Pen SelectedPen = Freeze(new Pen(FrameBrush, 3));
    private static readonly Pen HandlePen = Freeze(new Pen(FrameBrush, 2));
    private static readonly Pen DrawPen = Freeze(new Pen(FrameBrush, 2) { DashStyle = DashStyles.Dash });

    private EditableFrame? _selected;
    private Drag _drag;
    private Point _dragStart;
    private RotatedRect _dragOriginal;
    private Rect? _drawing;

    public FrameEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ContextMenu menu = new();
        MenuItem delete = new() { Header = Strings.Delete };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(delete);
        ContextMenu = menu;
        ContextMenuOpening += (_, e) =>
        {
            if (_selected is null)
                e.Handled = true;
        };
    }

    private enum Drag
    {
        None,
        Move,
        Corner0,
        Corner1,
        Corner2,
        Corner3,
        EdgeTop,
        EdgeRight,
        EdgeBottom,
        EdgeLeft,
        Rotate,
        Create,
    }

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public ObservableCollection<EditableFrame>? Frames
    {
        get => (ObservableCollection<EditableFrame>?)GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    /// <summary>Frames can be changed only on a preview: a saved scan just shows its result.</summary>
    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>Executed after every change made by the user.</summary>
    public ICommand? EditedCommand
    {
        get => (ICommand?)GetValue(EditedCommandProperty);
        set => SetValue(EditedCommandProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Source is not { } source || ImageRect() is not { } image)
            return;

        dc.DrawImage(source, image);
        if (Frames is null)
            return;

        foreach (EditableFrame frame in Frames)
        {
            Point[] c = ScreenCorners(frame.Rect, image);
            bool selected = frame == _selected;
            dc.DrawGeometry(null, selected ? SelectedPen : FramePen,
                !selected && frame.Outline is { Count: > 2 } outline
                    ? Polygon(outline.Select(p => new Point(p.X * image.Width + image.X, p.Y * image.Width + image.Y)).ToArray())
                    : Polygon(c));
            if (!selected)
                continue;

            foreach (Point p in c.Concat(EdgeMiddles(c)))
                dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
            (Point top, Point knob) = Knob(c);
            dc.DrawLine(HandlePen, top, knob);
            dc.DrawEllipse(HandleFill, HandlePen, knob, HandleSize / 2 + 1, HandleSize / 2 + 1);
        }

        if (_drawing is { } d)
            dc.DrawRectangle(null, DrawPen, d);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditable || Frames is null || ImageRect() is not { } image)
            return;
        Focus();
        Point p = e.GetPosition(this);
        _dragStart = p;
        _drag = Drag.None;

        if (_selected is not null)
            _drag = HitHandles(_selected, image, p);
        if (_drag == Drag.None)
        {
            EditableFrame? hit = Frames.LastOrDefault(f => Inside(ScreenCorners(f.Rect, image), p));
            _selected = hit;
            _drag = hit is null ? (image.Contains(p) ? Drag.Create : Drag.None) : Drag.Move;
        }

        if (_selected is not null)
            _dragOriginal = _selected.Rect;
        if (_drag != Drag.None)
            CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsEditable || ImageRect() is not { } image)
        {
            Cursor = null;
            return;
        }
        Point p = e.GetPosition(this);

        if (_drag == Drag.None)
        {
            Cursor = CursorFor(_selected is null ? Drag.None : HitHandles(_selected, image, p))
                ?? (Frames?.Any(f => Inside(ScreenCorners(f.Rect, image), p)) == true ? Cursors.SizeAll : Cursors.Cross);
            return;
        }

        if (_drag == Drag.Create)
        {
            _drawing = new Rect(_dragStart, p);
            _drawing = Rect.Intersect(_drawing.Value, image);
            InvalidateVisual();
            return;
        }

        if (_selected is null)
            return;
        double k = image.Width; // screen pixels per page-width unit
        RotatedRect r = _dragOriginal.Scale(k);
        r = r with { Cx = r.Cx + image.X, Cy = r.Cy + image.Y };
        RotatedRect changed = _drag switch
        {
            Drag.Move => r with { Cx = r.Cx + p.X - _dragStart.X, Cy = r.Cy + p.Y - _dragStart.Y },
            Drag.Rotate => Turn(r, p),
            >= Drag.Corner0 and <= Drag.Corner3 => ResizeCorner(r, (int)_drag - (int)Drag.Corner0, p),
            _ => ResizeEdge(r, _drag, p),
        };
        _selected.Rect = (changed with { Cx = changed.Cx - image.X, Cy = changed.Cy - image.Y }).Scale(1 / k);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None)
            return;
        ReleaseMouseCapture();

        if (_drag == Drag.Create && _drawing is { } d && ImageRect() is { } image && d.Width >= MinSide && d.Height >= MinSide)
        {
            double k = image.Width;
            EditableFrame frame = new(new RotatedRect((d.X + d.Width / 2 - image.X) / k, (d.Y + d.Height / 2 - image.Y) / k, d.Width / k, d.Height / k, 0),
                ItemKind.Photo, manual: true);
            Frames?.Add(frame);
            _selected = frame;
        }

        bool changed = _drag != Drag.Create || _drawing is { Width: >= MinSide, Height: >= MinSide };
        _drawing = null;
        _drag = Drag.None;
        InvalidateVisual();
        if (changed && (_selected is not null))
            Edited();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!IsEditable || Frames is null || ImageRect() is not { } image)
            return;
        Point p = e.GetPosition(this);
        _selected = Frames.LastOrDefault(f => Inside(ScreenCorners(f.Rect, image), p));
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private static void OnFramesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        FrameEditor editor = (FrameEditor)d;
        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= editor.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged now)
            now.CollectionChanged += editor.OnCollectionChanged;
        editor._selected = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selected is not null && Frames?.Contains(_selected) != true)
            _selected = null;
        InvalidateVisual();
    }

    private void DeleteSelected()
    {
        if (_selected is null || Frames is null)
            return;
        Frames.Remove(_selected);
        _selected = null;
        InvalidateVisual();
        Edited();
    }

    private void Edited()
    {
        if (EditedCommand?.CanExecute(null) == true)
            EditedCommand.Execute(null);
    }

    /// <summary>Where the bitmap is drawn: fitted into the control, centered, aspect kept.</summary>
    private Rect? ImageRect()
    {
        if (Source is not { PixelWidth: > 0, PixelHeight: > 0 } s)
            return null;
        double w = ActualWidth - 2 * Inset, h = ActualHeight - 2 * Inset;
        if (w <= 0 || h <= 0)
            return null;
        double k = Math.Min(w / s.PixelWidth, h / s.PixelHeight);
        double iw = s.PixelWidth * k, ih = s.PixelHeight * k;
        return new Rect(Inset + (w - iw) / 2, Inset + (h - ih) / 2, iw, ih);
    }

    private static Point[] ScreenCorners(RotatedRect r, Rect image) =>
        r.Scale(image.Width).Corners().Select(c => new Point(c.X + image.X, c.Y + image.Y)).ToArray();

    private static IEnumerable<Point> EdgeMiddles(Point[] c) =>
        [Mid(c[0], c[1]), Mid(c[1], c[2]), Mid(c[2], c[3]), Mid(c[3], c[0])];

    private static (Point Top, Point Knob) Knob(Point[] c)
    {
        Point top = Mid(c[0], c[1]);
        Vector outward = Mid(c[0], c[1]) - Mid(c[3], c[2]);
        if (outward.Length < 1e-6)
            outward = new Vector(0, -1);
        outward.Normalize();
        return (top, top + outward * KnobDistance);
    }

    private static Drag HitHandles(EditableFrame frame, Rect image, Point p)
    {
        Point[] c = ScreenCorners(frame.Rect, image);
        double reach = HandleSize;
        if ((Knob(c).Knob - p).Length <= reach)
            return Drag.Rotate;
        for (int i = 0; i < 4; i++)
        {
            if ((c[i] - p).Length <= reach)
                return Drag.Corner0 + i;
        }

        Point[] m = EdgeMiddles(c).ToArray();
        Drag[] edges = [Drag.EdgeTop, Drag.EdgeRight, Drag.EdgeBottom, Drag.EdgeLeft];
        for (int i = 0; i < 4; i++)
        {
            if ((m[i] - p).Length <= reach)
                return edges[i];
        }

        return Drag.None;
    }

    private static Cursor? CursorFor(Drag drag) => drag switch
    {
        Drag.Rotate => Cursors.Hand,
        Drag.Corner0 or Drag.Corner2 => Cursors.SizeNWSE,
        Drag.Corner1 or Drag.Corner3 => Cursors.SizeNESW,
        Drag.EdgeTop or Drag.EdgeBottom => Cursors.SizeNS,
        Drag.EdgeLeft or Drag.EdgeRight => Cursors.SizeWE,
        _ => null,
    };

    /// <summary>Resizes by a corner; the opposite corner stays where it is.</summary>
    private static RotatedRect ResizeCorner(RotatedRect r, int corner, Point p)
    {
        (Vector u, Vector v) = Axes(r);
        (double X, double Y) fixedCorner = r.Corners()[(corner + 2) % 4];
        Point f = new(fixedCorner.X, fixedCorner.Y);
        Vector d = p - f;
        double du = d * u, dv = d * v;
        double sign = corner is 0 or 3 ? -1 : 1;     // left corners grow towards -u
        double signV = corner is 0 or 1 ? -1 : 1;    // top corners grow towards -v
        double w = Math.Max(MinSide, du * sign), h = Math.Max(MinSide, dv * signV);
        Point c = f + u * (sign * w / 2) + v * (signV * h / 2);
        return r with { Cx = c.X, Cy = c.Y, Width = w, Height = h };
    }

    /// <summary>Resizes by a side; the opposite side stays where it is.</summary>
    private static RotatedRect ResizeEdge(RotatedRect r, Drag edge, Point p)
    {
        (Vector u, Vector v) = Axes(r);
        Point center = new(r.Cx, r.Cy);
        switch (edge)
        {
            case Drag.EdgeRight or Drag.EdgeLeft:
            {
                double s = edge == Drag.EdgeRight ? 1 : -1;
                Point anchor = center - u * (s * r.Width / 2);
                double w = Math.Max(MinSide, (p - anchor) * u * s);
                Point c = anchor + u * (s * w / 2);
                return r with { Cx = c.X, Cy = c.Y, Width = w };
            }

            default:
            {
                double s = edge == Drag.EdgeBottom ? 1 : -1;
                Point anchor = center - v * (s * r.Height / 2);
                double h = Math.Max(MinSide, (p - anchor) * v * s);
                Point c = anchor + v * (s * h / 2);
                return r with { Cx = c.X, Cy = c.Y, Height = h };
            }
        }
    }

    /// <summary>Turns so that the knob points at the mouse; snaps to straight within a few degrees.</summary>
    private static RotatedRect Turn(RotatedRect r, Point p)
    {
        double angle = Math.Atan2(p.Y - r.Cy, p.X - r.Cx) * 180 / Math.PI + 90;
        while (angle > 180)
            angle -= 360;
        while (angle <= -180)
            angle += 360;
        foreach (double straight in new[] { -180.0, -90.0, 0.0, 90.0, 180.0 })
        {
            if (Math.Abs(angle - straight) <= SnapDegrees)
                angle = straight;
        }

        return r with { Angle = angle };
    }

    private static (Vector U, Vector V) Axes(RotatedRect r)
    {
        double rad = r.Angle * Math.PI / 180;
        return (new Vector(Math.Cos(rad), Math.Sin(rad)), new Vector(-Math.Sin(rad), Math.Cos(rad)));
    }

    private static bool Inside(Point[] c, Point p)
    {
        bool inside = false;
        for (int i = 0, j = c.Length - 1; i < c.Length; j = i++)
        {
            if ((c[i].Y > p.Y) != (c[j].Y > p.Y)
                && p.X < (c[j].X - c[i].X) * (p.Y - c[i].Y) / (c[j].Y - c[i].Y) + c[i].X)
                inside = !inside;
        }

        return inside;
    }

    private static StreamGeometry Polygon(Point[] c)
    {
        StreamGeometry g = new();
        using (StreamGeometryContext ctx = g.Open())
        {
            ctx.BeginFigure(c[0], false, true);
            ctx.PolyLineTo(c[1..], true, true);
        }

        g.Freeze();
        return g;
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static T Freeze<T>(T f)
        where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
