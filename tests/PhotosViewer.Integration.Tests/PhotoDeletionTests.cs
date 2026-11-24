using System.Collections.Immutable;
using System.IO;
using PhotosViewer.Core;
using PhotosViewer.Infrastructure;
using Xunit;

namespace PhotosViewer.Integration.Tests;

public sealed class PhotoDeletionTests
{
    [Fact]
    public async Task PermanentlyDeletesEligibleFilesAndReportsAnOccupiedFile()
    {
        using var directory = new TemporaryPhotoDirectory();
        var locked = CreateFile(directory, "locked.jpg");
        var ready = CreateFile(directory, "中文 空格.JPG");
        var untouched = CreateFile(directory, "untouched.jpg");
        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [locked, ready]);
        Assert.Equal(new[] { locked, ready }, plan.Files);

        using var lockHandle = new FileStream(locked.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var report = await service.ExecuteAsync(plan, PhotoDeletionMode.Permanent);

        Assert.Same(ready, Assert.Single(report.Deleted));
        Assert.Same(locked, Assert.Single(report.Failures).File);
        Assert.False(File.Exists(ready.FullPath));
        Assert.True(File.Exists(locked.FullPath));
        Assert.True(File.Exists(untouched.FullPath));
    }

    [Theory]
    [InlineData(PhotoDeletionMode.Permanent)]
    [InlineData(PhotoDeletionMode.Recycle)]
    public async Task RejectsAReplacementWithIdenticalSizeAndTimestamps(PhotoDeletionMode mode)
    {
        using var directory = new TemporaryPhotoDirectory();
        var original = CreateFile(directory, "confirmed.jpg");
        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [original]);
        Assert.Single(plan.Files);

        string savedOriginal = directory.FilePath("original.bak");
        File.Move(original.FullPath, savedOriginal);
        File.WriteAllBytes(original.FullPath, [8, 7, 6, 5]);
        File.SetCreationTimeUtc(original.FullPath, original.CreationTimeUtc);
        File.SetLastWriteTimeUtc(original.FullPath, original.LastWriteTimeUtc);
        var replacement = new FileInfo(original.FullPath);
        Assert.Equal(original.Length, replacement.Length);
        Assert.Equal(original.CreationTimeUtc, replacement.CreationTimeUtc);
        Assert.Equal(original.LastWriteTimeUtc, replacement.LastWriteTimeUtc);

        var report = await service.ExecuteAsync(plan, mode);

        Assert.Empty(report.Deleted);
        Assert.Same(original, Assert.Single(report.Failures).File);
        Assert.Equal(new byte[] { 8, 7, 6, 5 }, File.ReadAllBytes(original.FullPath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(savedOriginal));
    }

    [Fact]
    public async Task RejectsFilesOutsideTheCurrentDirectoryAndDeduplicatesThePlan()
    {
        using var directory = new TemporaryPhotoDirectory();
        using var other = new TemporaryPhotoDirectory();
        var own = CreateFile(directory, "own.jpg");
        var outside = CreateFile(other, "outside.jpg");
        var snapshot = ImmutableArray.Create(own, own, outside);

        var plan = await new PhotoDeletionService().PrepareAsync(directory.Path, snapshot);

        Assert.Same(own, Assert.Single(plan.Files));
        Assert.Same(outside, Assert.Single(plan.Failures).File);
        Assert.True(File.Exists(outside.FullPath));
    }

    [Fact]
    public async Task AddsTheUniqueSameDirectoryCr3ToEachSelectedJpegInTheBatch()
    {
        using var directory = new TemporaryPhotoDirectory();
        var first = CreateFile(directory, "Canon 01.JpEg");
        var firstRaw = CreateFile(directory, "CANON 01.Cr3");
        var second = CreateFile(directory, "Canon 02.JPG");
        var secondRaw = CreateFile(directory, "Canon 02.CR3");
        var sidecar = CreateFile(directory, "Canon 01.xmp");
        var unmatched = CreateFile(directory, "unmatched.CR3");
        string nested = directory.FilePath("Canon 01.CR3", "backup");
        File.WriteAllBytes(nested, [1, 2, 3, 4]);

        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [first, second]);
        var expected = new[] { first.FullPath, firstRaw.FullPath, second.FullPath, secondRaw.FullPath };
        Assert.Equal(expected, plan.Files.Select(file => file.FullPath));
        var report = await service.ExecuteAsync(plan, PhotoDeletionMode.Permanent);

        Assert.Equal(expected, report.Deleted.Select(file => file.FullPath));
        Assert.Empty(report.Failures);
        Assert.All(expected, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(sidecar.FullPath));
        Assert.True(File.Exists(unmatched.FullPath));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task KeepsTheCr3WhenMoreThanOneJpegHasTheSameBaseName()
    {
        using var directory = new TemporaryPhotoDirectory();
        var selected = CreateFile(directory, "repeated.jpg");
        var otherJpeg = CreateFile(directory, "repeated.JPEG");
        var raw = CreateFile(directory, "repeated.CR3");
        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [selected]);

        Assert.Same(selected, Assert.Single(plan.Files));
        await service.ExecuteAsync(plan, PhotoDeletionMode.Permanent);
        Assert.False(File.Exists(selected.FullPath));
        Assert.True(File.Exists(otherJpeg.FullPath));
        Assert.True(File.Exists(raw.FullPath));
    }

    [Theory]
    [InlineData(PhotoDeletionMode.Permanent)]
    [InlineData(PhotoDeletionMode.Recycle)]
    public async Task SkipsThePairWhenItsConfirmedCr3HasBeenReplaced(PhotoDeletionMode mode)
    {
        using var directory = new TemporaryPhotoDirectory();
        var jpeg = CreateFile(directory, "pair.jpg");
        var raw = CreateFile(directory, "pair.CR3");
        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [jpeg]);
        Assert.Equal(2, plan.Files.Length);

        string savedRaw = directory.FilePath("original.bak");
        File.Move(raw.FullPath, savedRaw);
        File.WriteAllBytes(raw.FullPath, [8, 7, 6, 5]);
        File.SetCreationTimeUtc(raw.FullPath, raw.CreationTimeUtc);
        File.SetLastWriteTimeUtc(raw.FullPath, raw.LastWriteTimeUtc);
        var report = await service.ExecuteAsync(plan, mode);

        Assert.Empty(report.Deleted);
        Assert.Equal(2, report.Failures.Length);
        Assert.True(File.Exists(jpeg.FullPath));
        Assert.Equal(new byte[] { 8, 7, 6, 5 }, File.ReadAllBytes(raw.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipsThePairWhenOneFileIsOccupiedAndContinuesTheBatch(bool lockRaw)
    {
        using var directory = new TemporaryPhotoDirectory();
        var jpeg = CreateFile(directory, "pair.jpg");
        var raw = CreateFile(directory, "pair.CR3");
        var other = CreateFile(directory, "other.jpg");
        var service = new PhotoDeletionService();
        var plan = await service.PrepareAsync(directory.Path, [jpeg, other]);
        Assert.Equal(3, plan.Files.Length);

        var occupied = lockRaw ? raw : jpeg;
        using var lockHandle = new FileStream(occupied.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var report = await service.ExecuteAsync(plan, PhotoDeletionMode.Permanent);

        Assert.Same(other, Assert.Single(report.Deleted));
        Assert.Equal(2, report.Failures.Length);
        Assert.True(File.Exists(jpeg.FullPath));
        Assert.True(File.Exists(raw.FullPath));
    }

    [Fact]
    public async Task PreservesThePairWhenTheCr3IsReadOnlyAndDeletionIsPermanent()
    {
        using var directory = new TemporaryPhotoDirectory();
        var jpeg = CreateFile(directory, "pair.jpg");
        var raw = CreateFile(directory, "pair.CR3");
        FileAttributes original = File.GetAttributes(raw.FullPath);
        File.SetAttributes(raw.FullPath, original | FileAttributes.ReadOnly);
        try
        {
            var service = new PhotoDeletionService();
            var plan = await service.PrepareAsync(directory.Path, [jpeg]);
            Assert.Equal(2, plan.Files.Length);

            var report = await service.ExecuteAsync(plan, PhotoDeletionMode.Permanent);

            Assert.Empty(report.Deleted);
            Assert.Equal(2, report.Failures.Length);
            Assert.True(File.Exists(jpeg.FullPath));
            Assert.True(File.Exists(raw.FullPath));
        }
        finally
        {
            if (File.Exists(raw.FullPath))
            {
                File.SetAttributes(raw.FullPath, original);
            }
        }
    }

    private static PhotoFile CreateFile(TemporaryPhotoDirectory directory, string name)
    {
        string path = directory.FilePath(name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var file = new FileInfo(path);
        return new PhotoFile(file.FullName, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
    }
}
