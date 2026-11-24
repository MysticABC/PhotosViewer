using System.Collections.Immutable;

namespace PhotosViewer.App.ViewModels;

internal sealed record PhotoDetail(string Label, string Value);

internal static class PhotoDetails
{
    public static ImmutableArray<PhotoDetail> Format(PhotoItem? photo)
    {
        if (photo is null)
        {
            return [];
        }

        var file = photo.File;
        var metadata = photo.Metadata;
        string camera = string.Join(' ', new[] { metadata?.CameraMake, metadata?.CameraModel }.OfType<string>());
        if (metadata?.CameraModel is { } model && metadata.CameraMake is { } make &&
            model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
        {
            camera = model;
        }

        return
        [
            new("格式", photo.FormatLabel),
            new("文件大小", FormatSize(file.Length)),
            new("分辨率", metadata is null ? "—" : $"{metadata.PixelWidth} × {metadata.PixelHeight}"),
            new("拍摄时间", photo.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"),
            new("相机", camera.Length > 0 ? camera : "—"),
            new("镜头", metadata?.Lens ?? "—"),
            new("ISO", metadata?.Iso?.ToString() ?? "—"),
            new("光圈", metadata?.Aperture is double aperture ? $"f/{aperture:0.#}" : "—"),
            new("快门", FormatExposure(metadata?.ExposureSeconds)),
            new("焦距", metadata?.FocalLength is double focalLength ? $"{focalLength:0.#} mm" : "—"),
            new("修改时间", file.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            new("创建时间", file.CreationTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        ];
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576d:0.##} MiB",
        >= 1_024 => $"{bytes / 1_024d:0.##} KiB",
        _ => $"{bytes} B"
    };

    private static string FormatExposure(double? seconds) => seconds switch
    {
        > 0 and < 1 => $"1/{1 / seconds.Value:0.##} s",
        > 0 => $"{seconds.Value:0.##} s",
        _ => "—"
    };
}
