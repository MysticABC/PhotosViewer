using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotosViewer.Core;

namespace PhotosViewer.Imaging.Wpf;

public sealed record DecodedJpeg(BitmapSource Bitmap, PhotoMetadata Metadata);

public sealed class JpegDecoder
{
    public Task<DecodedJpeg> DecodeAsync(PhotoFile file, int? longEdge, CancellationToken cancellationToken) =>
        Task.Run(() => Decode(file, longEdge, cancellationToken), cancellationToken);

    public Task<PhotoMetadata> ReadMetadataAsync(PhotoFile file, CancellationToken cancellationToken) =>
        ReadHeaderAsync(file, JpegMetadata.Read, cancellationToken);

    public Task<DateTime?> ReadCaptureDateAsync(PhotoFile file, CancellationToken cancellationToken) =>
        ReadHeaderAsync(file, JpegMetadata.ReadCaptureDate, cancellationToken);

    public Task<(DateTime? CapturedAt, int Rating)> ReadCatalogMetadataAsync(PhotoFile file, CancellationToken cancellationToken) =>
        ReadHeaderAsync(file, JpegMetadata.ReadCatalogMetadata, cancellationToken);

    private static Task<T> ReadHeaderAsync<T>(PhotoFile file, Func<BitmapFrame, T> read, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = Open(file);
            var result = read(ReadHeader(stream));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);

    private static DecodedJpeg Decode(PhotoFile file, int? longEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = Open(file);
        var frame = ReadHeader(stream);
        var metadata = JpegMetadata.Read(frame);
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        cancellationToken.ThrowIfCancellationRequested();

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (longEdge is > 0 && longEdge < Math.Max(width, height))
        {
            if (width >= height)
            {
                bitmap.DecodePixelWidth = longEdge.Value;
                if ((long)height * longEdge.Value < width)
                {
                    bitmap.DecodePixelHeight = 1;
                }
            }
            else
            {
                bitmap.DecodePixelHeight = longEdge.Value;
                if ((long)width * longEdge.Value < height)
                {
                    bitmap.DecodePixelWidth = 1;
                }
            }
        }

        bitmap.EndInit();
        bitmap.Freeze();
        var oriented = ApplyOrientation(bitmap, metadata.Orientation);
        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedJpeg(oriented, metadata);
    }

    private static FileStream Open(PhotoFile file) =>
        new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static BitmapFrame ReadHeader(Stream stream) =>
        new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];

    private static BitmapSource ApplyOrientation(BitmapSource bitmap, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => null
        };

        if (transform is null)
        {
            return bitmap;
        }

        var oriented = new TransformedBitmap(bitmap, transform);
        oriented.Freeze();
        return oriented;
    }
}
