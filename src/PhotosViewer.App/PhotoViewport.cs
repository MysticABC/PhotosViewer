using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PhotosViewer.Imaging.Wpf;

namespace PhotosViewer.App;

public sealed class PhotoViewport : FrameworkElement
{
    private enum ZoomMode { Fit, ActualPixels, Custom }

    private DecodedJpeg? _image;
    private ZoomMode _mode;
    private int _rotation;
    private double _zoom;
    private Vector _pan;
    private Point? _dragOrigin;

    public PhotoViewport()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => UpdateView();
    }

    public event EventHandler? ViewChanged;
    public event EventHandler? FullResolutionRequested;

    public string ZoomLabel => _image is null ? string.Empty : $"{Scale / ActualScale:P0}";

    private double ActualScale => 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
    private double FitScale => _image is null || ActualWidth <= 0 || ActualHeight <= 0
        ? ActualScale
        : Math.Min(ActualScale, Math.Min(ActualWidth / RotatedWidth, ActualHeight / RotatedHeight));
    private double Scale => _mode switch
    {
        ZoomMode.Custom => _zoom,
        ZoomMode.ActualPixels => ActualScale,
        _ => FitScale
    };

    private int RotatedWidth => _rotation % 180 == 0 ? _image!.Metadata.PixelWidth : _image!.Metadata.PixelHeight;
    private int RotatedHeight => _rotation % 180 == 0 ? _image!.Metadata.PixelHeight : _image!.Metadata.PixelWidth;
    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    public void SetImage(DecodedJpeg? image)
    {
        _image = image;
        UpdateView();
    }

    public void ResetView()
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _image = null;
        _rotation = 0;
        FitToWindow();
    }

    public void FitToWindow()
    {
        _mode = ZoomMode.Fit;
        _pan = default;
        UpdateView();
    }

    public void ShowActualPixels()
    {
        _mode = ZoomMode.ActualPixels;
        _pan = default;
        UpdateView();
        FullResolutionRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RotateClockwise()
    {
        _rotation = (_rotation + 90) % 360;
        _pan = default;
        UpdateView();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_image is null)
        {
            return;
        }

        double width = _image.Metadata.PixelWidth;
        double height = _image.Metadata.PixelHeight;
        var center = Center + _pan;
        drawingContext.PushTransform(new TranslateTransform(center.X, center.Y));
        drawingContext.PushTransform(new ScaleTransform(Scale, Scale));
        drawingContext.PushTransform(new RotateTransform(_rotation));
        drawingContext.DrawImage(_image.Bitmap, new Rect(-width / 2, -height / 2, width, height));
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_image is null)
        {
            return;
        }

        double before = Scale;
        _zoom = Math.Clamp(before * Math.Pow(1.2, e.Delta / 120d), FitScale / 4, ActualScale * 8);
        Vector anchor = e.GetPosition(this) - Center;
        _pan = anchor - (anchor - _pan) * (_zoom / before);
        _mode = ZoomMode.Custom;
        UpdateView();

        if (_zoom / ActualScale * _image.Metadata.PixelWidth > _image.Bitmap.PixelWidth * 1.1)
        {
            FullResolutionRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image is not null && CaptureMouse())
        {
            Focus();
            _dragOrigin = e.GetPosition(this);
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOrigin is { } previous)
        {
            Point point = e.GetPosition(this);
            _pan += point - previous;
            _dragOrigin = point;
            UpdateView();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragOrigin = null;
        Cursor = Cursors.Arrow;
    }

    private void UpdateView()
    {
        if (_image is not null)
        {
            double limitX = Math.Max(0, (RotatedWidth * Scale - ActualWidth) / 2);
            double limitY = Math.Max(0, (RotatedHeight * Scale - ActualHeight) / 2);
            _pan = new Vector(Math.Clamp(_pan.X, -limitX, limitX), Math.Clamp(_pan.Y, -limitY, limitY));
        }

        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
