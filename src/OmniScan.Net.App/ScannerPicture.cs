using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OmniScan.Net.App;

/// <summary>
/// A flatbed scanner seen from above, its front (the panel) toward the viewer, with a sheet on the glass
/// whose top lies along the left or the right edge: "TOP" written along that edge and an arrow to it.
/// Drawn in the text color, so it follows the theme.
/// </summary>
public sealed class ScannerPicture : FrameworkElement
{
    public static readonly DependencyProperty TopOnRightProperty = DependencyProperty.Register(
        nameof(TopOnRight), typeof(bool), typeof(ScannerPicture),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TopTextProperty = DependencyProperty.Register(
        nameof(TopText), typeof(string), typeof(ScannerPicture),
        new FrameworkPropertyMetadata("TOP", FrameworkPropertyMetadataOptions.AffectsRender));

    public bool TopOnRight
    {
        get => (bool)GetValue(TopOnRightProperty);
        set => SetValue(TopOnRightProperty, value);
    }

    public string TopText
    {
        get => (string)GetValue(TopTextProperty);
        set => SetValue(TopTextProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(170, 120);

    protected override void OnRender(DrawingContext dc)
    {
        Brush ink = TextElement.GetForeground(this) ?? Brushes.Black;
        Brush accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.SteelBlue;
        Pen outline = new(ink, 1.5);
        Pen thin = new(ink, 1) { DashStyle = DashStyles.Solid };
        double w = ActualWidth, h = ActualHeight;

        // Body, glass, and the panel along the front.
        Rect body = new(4, 4, w - 8, h - 8);
        dc.DrawRoundedRectangle(null, outline, body, 8, 8);
        Rect glass = new(body.Left + 12, body.Top + 10, body.Width - 24, body.Height - 34);
        dc.DrawRectangle(null, thin, glass);
        Rect panel = new(body.Left + body.Width * 0.25, body.Bottom - 16, body.Width * 0.5, 8);
        dc.DrawRoundedRectangle(null, thin, panel, 3, 3);

        // The sheet, turned so that its top is along the left (or right) edge: its text lines run parallel
        // to that edge, the first one next to it. Turned to the left, a line starts at the bottom;
        // turned to the right, at the top.
        Rect sheet = new(glass.Left + 6, glass.Top + 5, glass.Width - 12, glass.Height - 10);
        dc.DrawRectangle(Brushes.Transparent, new Pen(ink, 1.2), sheet);
        Brush faint = ink.Clone();
        faint.Opacity = 0.45;
        Pen line = new(faint, 2);
        double band = 24;   // room for the word TOP along the top edge
        const int lines = 9;
        double span = sheet.Width - band - 10;
        for (int i = 0; i < lines; i++)
        {
            double offset = band + i * span / (lines - 1);
            double x = TopOnRight ? sheet.Right - offset : sheet.Left + offset;
            double length = (sheet.Height - 10) * (i % 4 == 3 ? 0.55 : 0.92);
            double y0 = TopOnRight ? sheet.Top + 5 : sheet.Bottom - 5;
            double y1 = TopOnRight ? y0 + length : y0 - length;
            dc.DrawLine(line, new Point(x, y0), new Point(x, y1));
        }

        // "TOP" along that edge, reading from the sheet's own top-left corner, and an arrow to the edge.
        FormattedText top = new(TopText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            11, accent, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double edge = TopOnRight ? sheet.Right - band / 2 : sheet.Left + band / 2;
        dc.PushTransform(new TranslateTransform(edge, sheet.Top + sheet.Height / 2));
        dc.PushTransform(new RotateTransform(TopOnRight ? 90 : -90));
        dc.DrawText(top, new Point(-top.Width / 2, -top.Height / 2));
        dc.Pop();
        dc.Pop();

        Pen arrow = new(accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double ax = TopOnRight ? sheet.Right + 2 : sheet.Left - 2;
        double dir = TopOnRight ? 1 : -1;
        double ay = sheet.Top + sheet.Height / 2;
        double tip = ax + dir * 6;
        dc.DrawLine(arrow, new Point(tip - dir * 7, ay - 6), new Point(tip, ay));
        dc.DrawLine(arrow, new Point(tip - dir * 7, ay + 6), new Point(tip, ay));
    }
}
