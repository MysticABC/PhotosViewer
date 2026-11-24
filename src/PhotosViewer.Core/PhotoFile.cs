namespace PhotosViewer.Core;

public sealed class PhotoFile(string fullPath, long length, DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
{
    public string FullPath { get; } = fullPath;
    public string Name { get; } = Path.GetFileName(fullPath);
    public string BaseName { get; } = Path.GetFileNameWithoutExtension(fullPath);
    public string Extension { get; } = Path.GetExtension(fullPath);
    public long Length { get; } = length;
    public DateTime CreationTimeUtc { get; } = creationTimeUtc;
    public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    public static bool IsJpeg(string extension) =>
        string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);

    public static bool IsCr3(string extension) => string.Equals(extension, ".cr3", StringComparison.OrdinalIgnoreCase);
}
