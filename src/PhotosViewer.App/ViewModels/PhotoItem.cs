using System.Windows.Media.Imaging;
using PhotosViewer.Core;

namespace PhotosViewer.App.ViewModels;

internal sealed class PhotoItem(PhotoFile file, int? savedRating = null) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private PhotoMetadata? _metadata;
    private DateTime? _capturedAt;
    private bool _isSelected;
    private bool _thumbnailFailed;
    private int? _embeddedRating;
    private int? _savedRating = savedRating;
    private bool _ratingSaving;

    public PhotoFile File { get; } = file;
    public string FormatLabel { get; } = file.Extension[1..].ToUpperInvariant();
    public bool CaptureDateRead { get; private set; }
    public DateTime? CapturedAt => _capturedAt;
    public string CaptureLabel => CapturedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public PhotoMetadata? Metadata => _metadata;
    public BitmapSource? Thumbnail => _thumbnail;
    public bool ThumbnailFailed => _thumbnailFailed;
    public bool RatingRead { get; private set; }
    public int? Rating => _savedRating ?? _embeddedRating;
    public bool CanEditRating => !_ratingSaving;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void SetThumbnail(BitmapSource? bitmap) => Set(ref _thumbnail, bitmap, nameof(Thumbnail));

    public void SetThumbnailFailed() => Set(ref _thumbnailFailed, true, nameof(ThumbnailFailed));

    public void SetMetadata(PhotoMetadata metadata)
    {
        SetCaptureDate(metadata.CapturedAt);
        SetEmbeddedRating(metadata.Rating);
        Set(ref _metadata, metadata, nameof(Metadata));
    }

    public void SetEmbeddedRating(int? rating)
    {
        RatingRead = true;
        int? previous = Rating;
        _embeddedRating = rating;
        if (previous != Rating)
        {
            Notify(nameof(Rating));
        }
    }

    public void SetSavedRating(int rating)
    {
        int? previous = Rating;
        _savedRating = rating;
        if (previous != Rating)
        {
            Notify(nameof(Rating));
        }
    }

    public void SetRatingSaving(bool saving) => Set(ref _ratingSaving, saving, nameof(CanEditRating));

    public void SetCaptureDate(DateTime? capturedAt)
    {
        CaptureDateRead = true;
        if (Set(ref _capturedAt, capturedAt, nameof(CapturedAt)))
        {
            Notify(nameof(CaptureLabel));
        }
    }
}
