using System.Collections.Immutable;

namespace PhotosViewer.Core;

public enum PhotoSort
{
    Name,
    CapturedAt,
    FileSize
}

public static class PhotoQuery
{
    public static ImmutableArray<PhotoFile> Apply(
        IEnumerable<PhotoFile> files,
        string nameFilter,
        string? extension,
        PhotoSort sort,
        bool descending,
        Func<PhotoFile, DateTime?> capturedAt)
    {
        string term = nameFilter.Trim();
        var matches = files.Where(file =>
            file.BaseName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
            (extension is null || file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)));

        var ordered = sort switch
        {
            PhotoSort.FileSize => descending
                ? matches.OrderByDescending(file => file.Length)
                : matches.OrderBy(file => file.Length),
            PhotoSort.CapturedAt => matches.OrderBy(capturedAt, Comparer<DateTime?>.Create(
                (left, right) => CompareDates(left, right, descending))),
            _ => descending
                ? matches.OrderByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                : matches.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
        };

        return ordered.ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FullPath, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static int CompareDates(DateTime? left, DateTime? right, bool descending)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return right.HasValue.CompareTo(left.HasValue);
        }

        return descending ? right.Value.CompareTo(left.Value) : left.Value.CompareTo(right.Value);
    }
}
