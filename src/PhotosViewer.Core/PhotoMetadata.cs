namespace PhotosViewer.Core;

public sealed record PhotoMetadata(
    int PixelWidth,
    int PixelHeight,
    int Orientation,
    DateTime? CapturedAt,
    string? CameraMake,
    string? CameraModel,
    string? Lens,
    int? Iso,
    double? Aperture,
    double? ExposureSeconds,
    double? FocalLength)
{
    public int Rating { get; init; }
}
