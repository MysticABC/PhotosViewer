using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PhotosViewer.Core;

namespace PhotosViewer.Infrastructure;

internal readonly record struct WindowsFileSnapshot(ulong Volume, ulong IdLow, ulong IdHigh, string FinalPath,
    long Length, DateTime LastWriteUtc, DateTime CreationUtc, bool IsReadOnly)
{
    public bool Matches(PhotoFile file) => Length == file.Length && LastWriteUtc == file.LastWriteTimeUtc &&
        CreationUtc == file.CreationTimeUtc;
}

internal static class WindowsDeletionFile
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileIdInfo = 18;
    private const int FileDispositionInfo = 4;

    public static WindowsFileSnapshot Capture(string path)
    {
        using var handle = Open(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete);
        return ReadSnapshot(handle);
    }

    public static void Verify(DeletionTarget target) => VerifySnapshot(target, Capture(target.Path));

    public static void VerifyDeletionAccess(DeletionTarget target, bool permanent)
    {
        using var handle = OpenForDeletion(target);
        if (permanent && target.Snapshot.IsReadOnly)
        {
            throw new UnauthorizedAccessException("The file is read-only.");
        }
    }

    public static void Delete(DeletionTarget target)
    {
        using var handle = OpenForDeletion(target);
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static SafeFileHandle OpenForDeletion(DeletionTarget target)
    {
        var handle = Open(target.Path, ReadAttributes | DeleteAccess, FileShare.Read);
        try
        {
            VerifySnapshot(target, ReadSnapshot(handle));
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void VerifySnapshot(DeletionTarget target, WindowsFileSnapshot current)
    {
        if (current != target.Snapshot)
        {
            throw new IOException("The confirmed file has changed or moved. Refresh the folder and confirm again.");
        }
    }

    private static SafeFileHandle Open(string path, uint access, FileShare share)
    {
        var handle = CreateFileW(NativePath(path), access, share, 0, OpenExisting, OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    private static WindowsFileSnapshot ReadSnapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var entry))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The target is a directory or a file system link.");
        }

        string finalPath = ReadFinalPath(handle);
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("This file is on a network share. Select a local photo file.");
        }

        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            int error = Marshal.GetLastWin32Error();
            ulong index = ((ulong)entry.IndexHigh << 32) | entry.IndexLow;
            if (error is not (1 or 50 or 87) || index == 0)
            {
                throw new Win32Exception(error);
            }

            identity = new FileIdentity { Volume = entry.Volume, Low = index };
        }

        return new WindowsFileSnapshot(identity.Volume, identity.Low, identity.High, finalPath,
            ((long)entry.SizeHigh << 32) | entry.SizeLow, ToDateTime(entry.LastWrite), ToDateTime(entry.Creation),
            (entry.Attributes & FileAttributes.ReadOnly) != 0);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (length < path.Capacity)
            {
                return path.ToString();
            }

            path.EnsureCapacity(checked((int)length + 1));
        }
    }

    private static string NativePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static DateTime ToDateTime(FILETIME value) =>
        DateTime.FromFileTimeUtc(((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public FILETIME Creation;
        public FILETIME LastAccess;
        public FILETIME LastWrite;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint securityAttributes,
        uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int type, out FileIdentity identity, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, ref byte value, uint size);
}
