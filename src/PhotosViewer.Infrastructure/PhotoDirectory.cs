using System.Collections.Immutable;
using System.Diagnostics;
using PhotosViewer.Core;

namespace PhotosViewer.Infrastructure;

public sealed class PhotoDirectory
{
    public Task<ImmutableArray<PhotoFile>> ScanAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(path, cancellationToken), cancellationToken);

    private static ImmutableArray<PhotoFile> Scan(string path, CancellationToken cancellationToken)
    {
        var files = ImmutableArray.CreateBuilder<PhotoFile>();
        foreach (var entry in new DirectoryInfo(path).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PhotoFile.IsJpeg(entry.Extension))
            {
                continue;
            }

            try
            {
                files.Add(Snapshot(entry));
            }
            catch (FileNotFoundException exception)
            {
                Trace.TraceInformation("Photo disappeared while scanning {0}: {1}", entry.FullName, exception.Message);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return files.ToImmutable();
    }

    internal static PhotoFile Snapshot(FileInfo entry) => new(entry.FullName, entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc);
}
