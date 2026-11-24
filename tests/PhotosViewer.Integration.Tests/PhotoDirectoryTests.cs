using System.IO;
using PhotosViewer.Infrastructure;
using Xunit;

namespace PhotosViewer.Integration.Tests;

public sealed class PhotoDirectoryTests
{
    [Fact]
    public async Task ScansOnlyJpegsInTheSelectedDirectory()
    {
        using var directory = new TemporaryPhotoDirectory();
        await File.WriteAllBytesAsync(directory.FilePath("照片 01.JPG"), new byte[20]);
        await File.WriteAllBytesAsync(directory.FilePath("photo.jpeg"), new byte[5]);
        await File.WriteAllBytesAsync(directory.FilePath("photo.CR3"), []);
        await File.WriteAllBytesAsync(directory.FilePath("nested.jpg", "backup"), []);

        var photos = await new PhotoDirectory().ScanAsync(directory.Path, CancellationToken.None);

        Assert.Equal(new[] { "photo.jpeg", "照片 01.JPG" }, photos.Select(photo => photo.Name).Order(StringComparer.Ordinal));
        Assert.All(photos, photo => Assert.Equal(directory.Path, Path.GetDirectoryName(photo.FullPath)));
        Assert.Equal(25, photos.Sum(photo => photo.Length));
    }

    [Fact]
    public async Task HonorsCancellationBeforeEnumeration()
    {
        using var directory = new TemporaryPhotoDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhotoDirectory().ScanAsync(directory.Path, cancellation.Token));
    }
}
