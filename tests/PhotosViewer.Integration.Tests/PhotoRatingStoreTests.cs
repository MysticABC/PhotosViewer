using System.IO;
using System.Text.Json;
using PhotosViewer.Core;
using PhotosViewer.Infrastructure;
using Xunit;

namespace PhotosViewer.Integration.Tests;

public sealed class PhotoRatingStoreTests
{
    [Fact]
    public async Task PreservesUpdatesFromSeparateWritersAndAnExplicitUnratedOverride()
    {
        using var directory = new TemporaryPhotoDirectory();
        string path = directory.FilePath("ratings.json");
        var left = new PhotoRatingStore(path);
        var right = new PhotoRatingStore(path);
        var first = new PhotoFile(Path.Combine(directory.Path, "first.jpg"), 123, DateTime.UnixEpoch, DateTime.UnixEpoch);
        var second = new PhotoFile(Path.Combine(directory.Path, "second.jpg"), 456, DateTime.UnixEpoch, DateTime.UnixEpoch);

        Assert.Empty(await left.ReadAsync(CancellationToken.None));
        await Task.WhenAll(left.SaveAsync(first, 5), right.SaveAsync(second, 3));
        await right.SaveAsync(first, PhotoRating.Unrated);
        var saved = await new PhotoRatingStore(path).ReadAsync(CancellationToken.None);

        Assert.Equal(2, saved.Count);
        Assert.Equal(PhotoRating.Unrated, saved[first.FullPath].Rating);
        Assert.Equal(3, saved[second.FullPath].Rating);
        Assert.True(saved[first.FullPath].Matches(first));
        Assert.False(saved[first.FullPath].Matches(new PhotoFile(first.FullPath, 124, first.CreationTimeUtc, first.LastWriteTimeUtc)));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task KeepsAnUnreadableStoreIntactWhenSavingFails()
    {
        using var directory = new TemporaryPhotoDirectory();
        string path = directory.FilePath("ratings.json");
        const string content = "{ invalid";
        await File.WriteAllTextAsync(path, content);
        var photo = new PhotoFile(Path.Combine(directory.Path, "sample.jpg"), 123, DateTime.UnixEpoch, DateTime.UnixEpoch);

        await Assert.ThrowsAsync<JsonException>(() => new PhotoRatingStore(path).SaveAsync(photo, 4));

        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }
}
