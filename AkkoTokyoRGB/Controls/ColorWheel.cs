using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AkkoTokyoRGB.Controls;

/// <summary>
/// High-resolution circular HSV color wheel supporting full 24-bit RGB (16.7M colors).
/// Angle = Hue (0..360), Radius = Saturation (0..1), paired with external Value (0..1).
/// </summary>
public sealed class ColorWheel : Grid
{
    private const int BitmapSize = 240;
    private readonly Image _wheelImage;
    private readonly Ellipse _markerOuter;
    private readonly Ellipse _markerInner;
    private readonly Canvas _overlay;

    private double _hue;        // 0..1
    private double _saturation; // 0..1
    private double _value = 1.0;// 0..1
    private bool _dragging;

    public event EventHandler? ColorChanged;
    public event EventHandler? DragCompleted;

    public ColorWheel()
    {
        Width = 176;
        Height = 176;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        Cursor = Cursors.Cross;
        Background = Brushes.Transparent;

        _wheelImage = new Image
        {
            Width = 176,
            Height = 176,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(_wheelImage, BitmapScalingMode.HighQuality);

        _overlay = new Canvas
        {
            Width = 176,
            Height = 176,
            IsHitTestVisible = false
        };

        _markerOuter = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2
        };
        _markerInner = new Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = Brushes.Black,
            StrokeThickness = 1.5
        };

        _overlay.Children.Add(_markerOuter);
        _overlay.Children.Add(_markerInner);
        Children.Add(_wheelImage);
        Children.Add(_overlay);

        RedrawBitmap();
        UpdateMarker();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    public double Hue => _hue;
    public double Saturation => _saturation;

    public double ValueFactor
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_value - clamped) > 0.002)
            {
                _value = clamped;
                RedrawBitmap();
            }
        }
    }

    public (byte R, byte G, byte B) SelectedRgb => HsvToRgb(_hue, _saturation, _value);

    public void SetRgb(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out double h, out double s, out double v);
        if (s > 0.001) _hue = h;
        _saturation = s;
        if (Math.Abs(_value - v) > 0.002)
        {
            _value = v;
            RedrawBitmap();
        }
        UpdateMarker();
    }

    public void SetHsv(double h, double s, double v)
    {
        _hue = ((h % 1.0) + 1.0) % 1.0;
        _saturation = Math.Clamp(s, 0.0, 1.0);
        double nv = Math.Clamp(v, 0.0, 1.0);
        if (Math.Abs(_value - nv) > 0.002)
        {
            _value = nv;
            RedrawBitmap();
        }
        UpdateMarker();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        PickAt(e.GetPosition(this));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        PickAt(e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        PickAt(e.GetPosition(this));
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void PickAt(Point pt)
    {
        double radius = Width / 2.0 - 2.0;
        double cx = Width / 2.0;
        double cy = Height / 2.0;
        double dx = pt.X - cx;
        double dy = pt.Y - cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        _saturation = Math.Clamp(dist / radius, 0.0, 1.0);
        if (dist > 0.5)
        {
            double angle = Math.Atan2(dy, dx) / (2.0 * Math.PI);
            _hue = (angle % 1.0 + 1.0) % 1.0;
        }
        if (_value < 0.05)
        {
            _value = 1.0;
            RedrawBitmap();
        }

        UpdateMarker();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMarker()
    {
        double radius = Width / 2.0 - 2.0;
        double cx = Width / 2.0;
        double cy = Height / 2.0;
        double angle = _hue * 2.0 * Math.PI;
        double mx = cx + Math.Cos(angle) * (_saturation * radius);
        double my = cy + Math.Sin(angle) * (_saturation * radius);

        Canvas.SetLeft(_markerOuter, mx - 7);
        Canvas.SetTop(_markerOuter, my - 7);
        Canvas.SetLeft(_markerInner, mx - 5);
        Canvas.SetTop(_markerInner, my - 5);
    }

    private void RedrawBitmap()
    {
        var wb = new WriteableBitmap(BitmapSize, BitmapSize, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[BitmapSize * BitmapSize * 4];
        double cx = BitmapSize / 2.0;
        double cy = BitmapSize / 2.0;
        double radius = BitmapSize / 2.0 - 2.0;

        for (int y = 0; y < BitmapSize; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < BitmapSize; x++)
            {
                double dx = x - cx;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int idx = (y * BitmapSize + x) * 4;

                if (dist > radius + 1.2)
                {
                    pixels[idx + 3] = 0;
                    continue;
                }

                double sat = Math.Clamp(dist / radius, 0.0, 1.0);
                double hue = (Math.Atan2(dy, dx) / (2.0 * Math.PI) % 1.0 + 1.0) % 1.0;
                var (r, g, b) = HsvToRgb(hue, sat, _value);

                byte alpha = dist <= radius - 0.5
                    ? (byte)255
                    : (byte)Math.Clamp((int)((radius + 1.0 - dist) / 1.5 * 255.0), 0, 255);

                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = alpha;
            }
        }

        wb.WritePixels(new Int32Rect(0, 0, BitmapSize, BitmapSize), pixels, BitmapSize * 4, 0);
        wb.Freeze();
        _wheelImage.Source = wb;
    }

    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double hh = ((h % 1.0 + 1.0) % 1.0) * 6.0;
        int sector = (int)Math.Floor(hh);
        double f = hh - sector;
        double p = v * (1.0 - s);
        double q = v * (1.0 - s * f);
        double tt = v * (1.0 - s * (1.0 - f));
        (double r, double g, double b) = sector switch
        {
            0 => (v, tt, p),
            1 => (q, v, p),
            2 => (p, v, tt),
            3 => (p, q, v),
            4 => (tt, p, v),
            _ => (v, p, q),
        };
        return (
            (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255)
        );
    }

    public static void RgbToHsv(byte rByte, byte gByte, byte bByte, out double h, out double s, out double v)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 1e-6 ? 0.0 : delta / max;

        if (delta <= 1e-6)
        {
            h = 0.0;
            return;
        }

        double hueDeg;
        if (Math.Abs(max - r) < 1e-6)
            hueDeg = 60.0 * (((g - b) / delta) % 6.0);
        else if (Math.Abs(max - g) < 1e-6)
            hueDeg = 60.0 * (((b - r) / delta) + 2.0);
        else
            hueDeg = 60.0 * (((r - g) / delta) + 4.0);

        if (hueDeg < 0) hueDeg += 360.0;
        h = hueDeg / 360.0;
    }
}
