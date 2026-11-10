using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelView;

public sealed class ImageSurface : FrameworkElement
{
    private Brush backdrop = new SolidColorBrush(Color.FromRgb(21, 25, 34));
    private static readonly Brush Checker = CreateChecker();
    private BitmapSource? bitmap;
    private Vector offset;
    private bool fit = true;
    private bool checker = true;

    public double Zoom { get; private set; } = 1;
    public bool HasImage => bitmap != null;
    public int PixelWidth => bitmap?.PixelWidth ?? 0;
    public int PixelHeight => bitmap?.PixelHeight ?? 0;
    public event Action? ZoomChanged;

    public ImageSurface()
    {
        ClipToBounds = true;
        Configure("深灰", true, true);
        SizeChanged += (_, _) =>
        {
            if (fit && bitmap != null)
                Fit();
            else
                InvalidateVisual();
        };
    }

    public void Configure(string background, bool showChecker, bool nearest)
    {
        backdrop = background switch
        {
            "纯黑" => Brushes.Black,
            "中灰" => Brushes.DimGray,
            "纯白" => Brushes.White,
            _ => new SolidColorBrush(Color.FromRgb(21, 25, 34))
        };
        checker = showChecker;
        RenderOptions.SetBitmapScalingMode(this, nearest ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        InvalidateVisual();
    }

    public void SetImage(BitmapSource source, bool keepZoom)
    {
        bool hadImage = bitmap != null;
        bitmap = source;
        if (!keepZoom || !hadImage)
            Fit();
        else
        {
            fit = false;
            InvalidateVisual();
        }
    }

    public void HoldView()
    {
        if (bitmap != null) fit = false;
    }

    public void SetFrame(BitmapSource source)
    {
        bool dimensionsChanged = PixelWidth != source.PixelWidth || PixelHeight != source.PixelHeight;
        bitmap = source;
        if (fit && dimensionsChanged)
            Fit();
        else
            InvalidateVisual();
    }

    public void Fit()
    {
        if (bitmap == null) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double fitX = ActualWidth * dpi.DpiScaleX / bitmap.PixelWidth;
        double fitY = ActualHeight * dpi.DpiScaleY / bitmap.PixelHeight;
        Zoom = Math.Min(1, Math.Max(0.00001, Math.Min(fitX, fitY)));
        fit = true;
        offset = default;
        NotifyZoom();
    }

    public void Actual()
    {
        if (bitmap == null) return;
        fit = false;
        offset = default;
        Zoom = 1;
        NotifyZoom();
    }

    public void ZoomAt(double multiplier, Point anchor, bool integerSteps)
    {
        if (bitmap == null || multiplier == 1) return;
        double next = Math.Clamp(Zoom * multiplier, Math.Min(Zoom, 0.001), 128);
        if (integerSteps)
        {
            if (multiplier > 1 && Zoom >= 0.5)
                next = Math.Floor(Zoom + 0.00001) + 1;
            else if (multiplier < 1 && Zoom > 1)
                next = Math.Ceiling(Zoom - 0.00001) - 1;
            else if (multiplier < 1 && Zoom >= 0.99999)
                next = 0.5;
            next = Math.Min(next, 128);
        }
        Vector fromCenter = anchor - new Point(ActualWidth / 2, ActualHeight / 2);
        offset = fromCenter - (fromCenter - offset) * (next / Zoom);
        Zoom = next;
        fit = false;
        NotifyZoom();
    }

    public void Pan(Vector delta)
    {
        if (bitmap == null) return;
        fit = false;
        offset += delta;
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (fit) Fit();
        else InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(backdrop, null, new Rect(RenderSize));
        if (bitmap == null) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double w = bitmap.PixelWidth * Zoom;
        double h = bitmap.PixelHeight * Zoom;
        double x = Math.Round((ActualWidth / 2 + offset.X) * dpi.DpiScaleX - w / 2) / dpi.DpiScaleX;
        double y = Math.Round((ActualHeight / 2 + offset.Y) * dpi.DpiScaleY - h / 2) / dpi.DpiScaleY;
        var destination = new Rect(x, y, Math.Max(1, Math.Round(w)) / dpi.DpiScaleX, Math.Max(1, Math.Round(h)) / dpi.DpiScaleY);
        if (checker) dc.DrawRectangle(Checker, null, destination);
        dc.DrawImage(bitmap, destination);
    }

    private void NotifyZoom()
    {
        InvalidateVisual();
        ZoomChanged?.Invoke();
    }

    private static DrawingBrush CreateChecker()
    {
        var first = new SolidColorBrush(Color.FromRgb(63, 69, 81));
        var second = new SolidColorBrush(Color.FromRgb(92, 99, 112));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(first, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(second, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        drawing.Children.Add(new GeometryDrawing(second, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }
}
