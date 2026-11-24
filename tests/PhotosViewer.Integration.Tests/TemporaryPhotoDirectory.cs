using System.IO;

namespace PhotosViewer.Integration.Tests;

internal sealed class TemporaryPhotoDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("photosviewer-m1-tests-");
    private readonly List<string> _files = [];
    private readonly List<DirectoryInfo> _children = [];

    public string Path => _directory.FullName;

    public string FilePath(string name, string? subdirectory = null)
    {
        var folder = _directory;
        if (subdirectory is not null)
        {
            folder = _directory.CreateSubdirectory(subdirectory);
            _children.Add(folder);
        }

        string path = System.IO.Path.Combine(folder.FullName, name);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }

        foreach (var directory in _children.DistinctBy(directory => directory.FullName))
        {
            directory.Delete();
        }

        _directory.Delete();
    }
}
