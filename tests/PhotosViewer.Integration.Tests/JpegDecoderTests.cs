using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotosViewer.Core;
using PhotosViewer.Imaging.Wpf;
using Xunit;

namespace PhotosViewer.Integration.Tests;

public sealed class JpegDecoderTests
{
    [Theory]
    [InlineData(1, 320, 160, 0)]
    [InlineData(2, 320, 160, 1)]
    [InlineData(3, 320, 160, 3)]
    [InlineData(4, 320, 160, 2)]
    [InlineData(5, 160, 320, 0)]
    [InlineData(6, 160, 320, 2)]
    [InlineData(7, 160, 320, 3)]
    [InlineData(8, 160, 320, 1)]
    public async Task DecodesBoundedThumbnailsWithAllExifOrientations(int orientation, int width, int height, int corner)
    {
        using var directory = new TemporaryPhotoDirectory();
        var photo = CreatePhoto(directory.FilePath("样片 01.JPG"), (ushort)orientation);

        var image = await new JpegDecoder().DecodeAsync(photo, 320, CancellationToken.None);

        Assert.True(image.Bitmap.IsFrozen);
        Assert.Equal(width, image.Bitmap.PixelWidth);
        Assert.Equal(height, image.Bitmap.PixelHeight);
        Assert.Equal(orientation, image.Metadata.Orientation);
        Assert.Equal(orientation >= 5 ? 320 : 640, image.Metadata.PixelWidth);
        using var exclusive = new FileStream(photo.FullPath, FileMode.Open, FileAccess.Read, FileShare.None);
        AssertCorner(image.Bitmap, corner);
    }

    [Fact]
    public async Task ReadsBasicExifAndLeavesMissingFieldsNull()
    {
        using var directory = new TemporaryPhotoDirectory();
        var photo = CreatePhoto(directory.FilePath("sample.jpg"), 1);
        var decoder = new JpegDecoder();

        var metadata = await decoder.ReadMetadataAsync(photo, CancellationToken.None);

        Assert.Equal("Sample Camera", metadata.CameraModel);
        Assert.Equal(200, metadata.Iso);
        Assert.Equal(2.8, metadata.Aperture!.Value, 2);
        Assert.Equal(0.004, metadata.ExposureSeconds!.Value, 4);
        Assert.Equal(new DateTime(2026, 2, 3, 9, 8, 7), metadata.CapturedAt);
        Assert.Equal(metadata.CapturedAt, await decoder.ReadCaptureDateAsync(photo, CancellationToken.None));
        Assert.Null(metadata.Lens);
    }

    [Fact]
    public async Task PreservesAtLeastOnePixelOnTheShortEdgeOfAnExtremePanorama()
    {
        using var directory = new TemporaryPhotoDirectory();
        var photo = CreatePhoto(directory.FilePath("panorama.jpg"), 1, 2048, 1);

        var image = await new JpegDecoder().DecodeAsync(photo, 320, CancellationToken.None);

        Assert.Equal(320, image.Bitmap.PixelWidth);
        Assert.Equal(1, image.Bitmap.PixelHeight);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("-1", PhotoRating.Rejected)]
    public async Task ReadsEmbeddedXmpRating(string rating, int expected)
    {
        using var directory = new TemporaryPhotoDirectory();
        var photo = CreatePhoto(directory.FilePath("xmp.jpg"), 1);
        WriteXmpRating(photo.FullPath, rating);
        var decoder = new JpegDecoder();

        var metadata = await decoder.ReadMetadataAsync(photo, CancellationToken.None);
        var catalog = await decoder.ReadCatalogMetadataAsync(photo, CancellationToken.None);

        Assert.Equal(expected, metadata.Rating);
        Assert.Equal(expected, catalog.Rating);
    }

    [Fact]
    public async Task ReadsWindowsRatingsAndTreatsMissingMetadataAsUnrated()
    {
        using var directory = new TemporaryPhotoDirectory();
        var simple = CreatePhoto(directory.FilePath("simple.jpg"), 1, simpleRating: 3);
        var percent = CreatePhoto(directory.FilePath("percent.jpg"), 1, windowsRating: 75);
        var missing = CreatePhoto(directory.FilePath("unrated.jpg"), 1);
        WriteXmpRating(percent.FullPath, "0");
        var decoder = new JpegDecoder();

        Assert.Equal(3, (await decoder.ReadCatalogMetadataAsync(simple, CancellationToken.None)).Rating);
        Assert.Equal(4, (await decoder.ReadCatalogMetadataAsync(percent, CancellationToken.None)).Rating);
        Assert.Equal(PhotoRating.Unrated, (await decoder.ReadCatalogMetadataAsync(missing, CancellationToken.None)).Rating);
    }

    internal static PhotoFile CreatePhoto(string path, ushort orientation, int width = 640, int height = 320,
        ushort? simpleRating = null, ushort? windowsRating = null)
    {
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (red, green, blue) = CornerColor((y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0));
                int offset = (y * width + x) * 3;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        metadata.SetQuery("/app1/ifd/{ushort=272}", "Sample Camera");
        metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:02:03 09:08:07");
        metadata.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)200);
        metadata.SetQuery("/app1/ifd/exif/{ushort=33437}", (10UL << 32) | 28UL);
        metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", (250UL << 32) | 1UL);
        if (simpleRating is ushort stars)
        {
            metadata.SetQuery("/app1/ifd/{ushort=18246}", stars);
        }

        if (windowsRating is ushort score)
        {
            metadata.SetQuery("/app1/ifd/{ushort=18249}", score);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var file = new FileInfo(path);
        return new PhotoFile(file.FullName, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
    }

    private static void WriteXmpRating(string path, string rating)
    {
        byte[] original = File.ReadAllBytes(path);
        byte[] xmp = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + $"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="{rating}" />
            </rdf:RDF></x:xmpmeta>
            """);
        int length = xmp.Length + 2;
        using var file = File.Create(path);
        file.Write(original.AsSpan(0, 2));
        file.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        file.Write(xmp);
        file.Write(original.AsSpan(2));
    }

    private static void AssertCorner(BitmapSource source, int corner)
    {
        var bgr = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var pixel = new byte[3];
        bgr.CopyPixels(new Int32Rect(12, 12, 1, 1), pixel, 3, 0);
        var (red, green, blue) = CornerColor(corner);
        Assert.InRange(Math.Abs(pixel[0] - blue), 0, 20);
        Assert.InRange(Math.Abs(pixel[1] - green), 0, 20);
        Assert.InRange(Math.Abs(pixel[2] - red), 0, 20);
    }

    private static (byte Red, byte Green, byte Blue) CornerColor(int corner) => corner switch
    {
        0 => (240, 20, 20),
        1 => (20, 220, 20),
        2 => (20, 20, 240),
        _ => (240, 220, 20)
    };
}
