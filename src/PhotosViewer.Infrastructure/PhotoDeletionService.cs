using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PhotosViewer.Core;

namespace PhotosViewer.Infrastructure;

public enum PhotoDeletionMode { Recycle, Permanent }

public sealed record PhotoDeletionFailure(PhotoFile File, string Reason);

public sealed record PhotoDeletionReport(ImmutableArray<PhotoFile> Deleted, ImmutableArray<PhotoDeletionFailure> Failures);

public sealed class PhotoDeletionPlan
{
    internal PhotoDeletionPlan(ImmutableArray<DeletionGroup> groups, ImmutableArray<PhotoDeletionFailure> failures)
    {
        Groups = groups;
        var files = ImmutableArray.CreateBuilder<PhotoFile>();
        foreach (var group in groups)
        {
            files.Add(group.Primary.File);
            if (group.Companion is { } companion)
            {
                files.Add(companion.File);
            }
        }

        Files = files.ToImmutable();
        Failures = failures;
    }

    internal ImmutableArray<DeletionGroup> Groups { get; }
    public ImmutableArray<PhotoFile> Files { get; }
    public ImmutableArray<PhotoDeletionFailure> Failures { get; }
}

internal sealed record DeletionTarget(PhotoFile File, string Path, WindowsFileSnapshot Snapshot);
internal sealed record DeletionGroup(DeletionTarget Primary, DeletionTarget? Companion);

[SupportedOSPlatform("windows")]
public sealed class PhotoDeletionService
{
    public Task<PhotoDeletionPlan> PrepareAsync(string folder, ImmutableArray<PhotoFile> files) =>
        Task.Run(() => Prepare(folder, files));

    public Task<PhotoDeletionReport> ExecuteAsync(PhotoDeletionPlan plan, PhotoDeletionMode mode, nint ownerWindow = 0) => mode switch
    {
        PhotoDeletionMode.Recycle => RunOnSta(() => Execute(plan, mode, ownerWindow)),
        PhotoDeletionMode.Permanent => Task.Run(() => Execute(plan, mode, ownerWindow)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or NotSupportedException;

    private static PhotoDeletionPlan Prepare(string folder, ImmutableArray<PhotoFile> files)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        ILookup<string, FileInfo> pairs;
        try
        {
            pairs = ReadPairingCandidates(directory, files);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            Trace.TraceWarning("Could not check CR3 companions in {0}: {1}", directory, exception);
            return new PhotoDeletionPlan([], files.Select(file => new PhotoDeletionFailure(file,
                $"Could not check for a paired CR3. {exception.Message}")).ToImmutableArray());
        }

        var groups = ImmutableArray.CreateBuilder<DeletionGroup>();
        var failures = ImmutableArray.CreateBuilder<PhotoDeletionFailure>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                string path = Path.GetFullPath(file.FullPath);
                if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The file is outside the selected directory.");
                }

                if (!paths.Add(path))
                {
                    continue;
                }

                var primary = PrepareTarget(file, path);
                DeletionTarget? companion = null;
                var entry = FindCompanion(pairs, primary);
                if (entry is not null && paths.Add(entry.FullName))
                {
                    try
                    {
                        companion = PrepareTarget(PhotoDirectory.Snapshot(entry), entry.FullName);
                    }
                    catch (Exception exception) when (IsFileError(exception))
                    {
                        throw new IOException($"Could not confirm the paired CR3 {entry.FullName}. {exception.Message}", exception);
                    }
                }

                groups.Add(new DeletionGroup(primary, companion));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                failures.Add(new PhotoDeletionFailure(file, exception.Message));
            }
        }

        return new PhotoDeletionPlan(groups.ToImmutable(), failures.ToImmutable());
    }

    private static ILookup<string, FileInfo> ReadPairingCandidates(string directory, ImmutableArray<PhotoFile> files)
    {
        var names = files.Where(file => PhotoFile.IsJpeg(file.Extension)).Select(file => file.BaseName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> entries = names.Count == 0 ? [] : new DirectoryInfo(directory).EnumerateFiles();
        return entries.Where(entry => (PhotoFile.IsJpeg(entry.Extension) || PhotoFile.IsCr3(entry.Extension)) &&
                names.Contains(Path.GetFileNameWithoutExtension(entry.Name)))
            .ToLookup(entry => Path.GetFileNameWithoutExtension(entry.Name), StringComparer.OrdinalIgnoreCase);
    }

    private static FileInfo? FindCompanion(ILookup<string, FileInfo> pairs, DeletionTarget primary)
    {
        if (!PhotoFile.IsJpeg(primary.File.Extension))
        {
            return null;
        }

        var candidates = pairs[primary.File.BaseName];
        return candidates.Count() == 2 && candidates.Any(entry => PhotoFile.IsJpeg(entry.Extension) &&
            string.Equals(entry.FullName, primary.Path, StringComparison.OrdinalIgnoreCase))
            ? candidates.FirstOrDefault(entry => PhotoFile.IsCr3(entry.Extension))
            : null;
    }

    private static DeletionTarget PrepareTarget(PhotoFile file, string path)
    {
        var snapshot = WindowsDeletionFile.Capture(path);
        if (!snapshot.Matches(file))
        {
            throw new IOException("The file changed before confirmation. Refresh the folder and try again.");
        }

        return new DeletionTarget(file, path, snapshot);
    }

    private static PhotoDeletionReport Execute(PhotoDeletionPlan plan, PhotoDeletionMode mode, nint ownerWindow)
    {
        var deleted = ImmutableArray.CreateBuilder<PhotoFile>();
        var failures = plan.Failures.ToBuilder();
        foreach (var group in plan.Groups)
        {
            if (group.Companion is { } companion)
            {
                try
                {
                    WindowsDeletionFile.VerifyDeletionAccess(companion, mode == PhotoDeletionMode.Permanent);
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    Trace.TraceWarning("Paired CR3 verification failed for {0}: {1}", companion.Path, exception);
                    failures.Add(new PhotoDeletionFailure(companion.File, exception.Message));
                    failures.Add(new PhotoDeletionFailure(group.Primary.File, "Skipped because the paired CR3 could not be verified for deletion."));
                    continue;
                }
            }

            var failure = DeleteTarget(group.Primary, mode, ownerWindow);
            if (failure is not null)
            {
                failures.Add(failure);
                if (group.Companion is { } skipped)
                {
                    failures.Add(new PhotoDeletionFailure(skipped.File, "Skipped because the paired JPEG could not be deleted."));
                }

                continue;
            }

            deleted.Add(group.Primary.File);
            if (group.Companion is { } ready)
            {
                failure = DeleteTarget(ready, mode, ownerWindow);
                if (failure is null)
                {
                    deleted.Add(ready.File);
                }
                else
                {
                    failures.Add(failure);
                }
            }
        }

        return new PhotoDeletionReport(deleted.ToImmutable(), failures.ToImmutable());
    }

    private static PhotoDeletionFailure? DeleteTarget(DeletionTarget target, PhotoDeletionMode mode, nint ownerWindow)
    {
        try
        {
            if (mode == PhotoDeletionMode.Recycle)
            {
                WindowsRecycleBin.Move(target, ownerWindow);
            }
            else
            {
                WindowsDeletionFile.Delete(target);
            }

            Trace.TraceInformation("{0}: {1}", mode, target.Path);
            return null;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            Trace.TraceWarning("{0} failed for {1}: {2}", mode, target.Path, exception);
            return new PhotoDeletionFailure(target.File, exception.Message);
        }
    }

    private static Task<PhotoDeletionReport> RunOnSta(Func<PhotoDeletionReport> action)
    {
        var completion = new TaskCompletionSource<PhotoDeletionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
