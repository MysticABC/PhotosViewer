using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using PhotosViewer.Core;

namespace PhotosViewer.Infrastructure;

public sealed record SavedPhotoRating(long Length, DateTime LastWriteTimeUtc, int Rating)
{
    public bool Matches(PhotoFile file) => Length == file.Length && LastWriteTimeUtc == file.LastWriteTimeUtc;
}

public sealed class PhotoRatingStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public Task<ImmutableDictionary<string, SavedPhotoRating>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var ratings = ReadRatings().ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            return ratings;
        }, cancellationToken);

    public Task<SavedPhotoRating> SaveAsync(PhotoFile file, int rating)
    {
        if (!PhotoRating.IsValid(rating))
        {
            throw new ArgumentOutOfRangeException(nameof(rating));
        }

        return Task.Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var writeLock = await AcquireWriteLockAsync().ConfigureAwait(false);
            var ratings = ReadRatings();
            var saved = new SavedPhotoRating(file.Length, file.LastWriteTimeUtc, rating);
            ratings[file.FullPath] = saved;
            WriteRatings(ratings);
            return saved;
        });
    }

    private Dictionary<string, SavedPhotoRating> ReadRatings()
    {
        var ratings = new Dictionary<string, SavedPhotoRating>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var entries = JsonSerializer.Deserialize<Dictionary<string, SavedPhotoRating>>(stream) ??
                throw new JsonException("The ratings file does not contain rating entries.");
            foreach (var (key, entry) in entries)
            {
                if (entry is null || !PhotoRating.IsValid(entry.Rating))
                {
                    throw new JsonException("The ratings file contains an invalid rating.");
                }

                ratings[key] = entry;
            }
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ratings;
        }

        return ratings;
    }

    private async Task<FileStream> AcquireWriteLockAsync()
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33 &&
                Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    private void WriteRatings(Dictionary<string, SavedPhotoRating> ratings)
    {
        string tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, ratings);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Could not remove the temporary rating file: {0}", exception);
                }
            }
        }
    }
}
