using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PhotosViewer.Core;

namespace PhotosViewer.Imaging.Wpf;

internal static class JpegMetadata
{
    private const string Ifd = "/app1/ifd";
    private const string Exif = Ifd + "/exif";

    public static PhotoMetadata Read(BitmapFrame frame)
    {
        BitmapMetadata? metadata = ReadMetadata(frame);
        int orientation = Query(metadata, Ifd + "/{ushort=274}") is ushort value and >= 1 and <= 8 ? value : 1;
        bool swapsAxes = orientation >= 5;

        return new PhotoMetadata(
            swapsAxes ? frame.PixelHeight : frame.PixelWidth,
            swapsAxes ? frame.PixelWidth : frame.PixelHeight,
            orientation,
            ReadDate(metadata),
            ReadText(metadata, Ifd + "/{ushort=271}"),
            ReadText(metadata, Ifd + "/{ushort=272}"),
            ReadText(metadata, Exif + "/{ushort=42036}"),
            Query(metadata, Exif + "/{ushort=34855}") is ushort iso and > 0 ? iso : null,
            ReadRational(metadata, Exif + "/{ushort=33437}"),
            ReadRational(metadata, Exif + "/{ushort=33434}"),
            ReadRational(metadata, Exif + "/{ushort=37386}"))
        {
            Rating = ReadRating(metadata)
        };
    }

    public static DateTime? ReadCaptureDate(BitmapFrame frame) => ReadDate(ReadMetadata(frame));

    public static (DateTime? CapturedAt, int Rating) ReadCatalogMetadata(BitmapFrame frame)
    {
        var metadata = ReadMetadata(frame);
        return (ReadDate(metadata), ReadRating(metadata));
    }

    private static int ReadRating(BitmapMetadata? metadata)
    {
        if (ReadNumber(Query(metadata, "/xmp/xmp:Rating")) is double xmp &&
            (xmp == PhotoRating.Rejected || xmp is > PhotoRating.Unrated and <= PhotoRating.Highest))
        {
            return (int)Math.Round(xmp, MidpointRounding.AwayFromZero);
        }

        if (Query(metadata, Ifd + "/{ushort=18246}") is ushort stars and > PhotoRating.Unrated and <= PhotoRating.Highest)
        {
            return stars;
        }

        return ReadNumber(Query(metadata, "System.Rating")) switch
        {
            >= 1 and <= 12 => 1,
            >= 13 and <= 37 => 2,
            >= 38 and <= 62 => 3,
            >= 63 and <= 87 => 4,
            >= 88 and <= 99 => PhotoRating.Highest,
            _ => PhotoRating.Unrated
        };
    }

    private static double? ReadNumber(object? value) =>
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : null;

    private static DateTime? ReadDate(BitmapMetadata? metadata)
    {
        string? date = ReadText(metadata, Exif + "/{ushort=36867}");
        return DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : null;
    }

    private static string? ReadText(BitmapMetadata? metadata, string path)
    {
        string? value = (Query(metadata, path) as string)?.TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double? ReadRational(BitmapMetadata? metadata, string path)
    {
        if (Query(metadata, path) is not ulong rational)
        {
            return null;
        }

        // WIC packs the numerator into the low word of VT_UI8.
        uint numerator = (uint)rational;
        uint denominator = (uint)(rational >> 32);
        return numerator > 0 && denominator > 0 ? (double)numerator / denominator : null;
    }

    private static BitmapMetadata? ReadMetadata(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata as BitmapMetadata;
        }
        catch (Exception exception) when (IsMetadataError(exception))
        {
            Trace.TraceInformation("JPEG metadata could not be read: {0}", exception.Message);
            return null;
        }
    }

    private static object? Query(BitmapMetadata? metadata, string path)
    {
        try
        {
            return metadata?.GetQuery(path);
        }
        catch (Exception exception) when (IsMetadataError(exception))
        {
            Trace.TraceInformation("JPEG metadata query {0} failed: {1}", path, exception.Message);
            return null;
        }
    }

    private static bool IsMetadataError(Exception exception) =>
        exception is NotSupportedException or ArgumentException or InvalidOperationException or COMException or FormatException;
}
