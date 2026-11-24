using Xunit;

namespace PhotosViewer.Core.Tests;

public sealed class PhotoQueryTests
{
    [Fact]
    public void SearchesBasenamesAndMatchesExtensionCaseInsensitively()
    {
        var files = new[] { File("IMG_200.JPG"), File("img_100.jpg"), File("IMG_300.jpeg"), File("other.jpg") };

        var result = PhotoQuery.Apply(files, "  ImG  ", ".jpg", PhotoSort.Name, false, _ => null);

        Assert.Equal(new[] { "img_100.jpg", "IMG_200.JPG" }, result.Select(file => file.Name));
        Assert.Empty(PhotoQuery.Apply(files, "jpeg", null, PhotoSort.Name, false, _ => null));
    }

    [Fact]
    public void BreaksSizeTiesByName()
    {
        var files = new[] { File("c.jpg", 5), File("b.jpg", 8), File("a.jpg", 8) };

        var result = PhotoQuery.Apply(files, "", null, PhotoSort.FileSize, true, _ => null);

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, result.Select(file => file.Name));
    }

    [Theory]
    [InlineData(false, "old.jpg", "new.jpg")]
    [InlineData(true, "new.jpg", "old.jpg")]
    public void PlacesUnknownCaptureDatesLastInEitherDirection(bool descending, string first, string second)
    {
        var old = File("old.jpg");
        var recent = File("new.jpg");
        var unknown = File("a.jpg");
        var dates = new Dictionary<PhotoFile, DateTime> { [old] = new(2020, 1, 1), [recent] = new(2025, 1, 1) };

        var result = PhotoQuery.Apply(new[] { unknown, recent, old }, "", null, PhotoSort.CapturedAt, descending,
            file => dates.TryGetValue(file, out var date) ? date : null);

        Assert.Equal(new[] { first, second, "a.jpg" }, result.Select(file => file.Name));
    }

    private static PhotoFile File(string name, long length = 0) => new(name, length, DateTime.UnixEpoch, DateTime.UnixEpoch);
}
