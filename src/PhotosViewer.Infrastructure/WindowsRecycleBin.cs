using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PhotosViewer.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WindowsRecycleBin
{
    private const uint Silent = 0x0004;
    private const uint NoConfirmation = 0x0010;
    private const uint NoErrorUi = 0x0400;
    private const uint NoRecursion = 0x1000;
    private const uint NoConnectedElements = 0x2000;
    private const uint RecycleOnDelete = 0x00080000;
    private const uint EarlyFailure = 0x00100000;
    private const uint RecycleTransfer = 0x80;
    private const int Abort = unchecked((int)0x80004004);
    private static readonly Guid OperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static void Move(DeletionTarget target, nint ownerWindow)
    {
        WindowsDeletionFile.Verify(target);
        VerifyVolume(target);
        IShellItem? item = null;
        IFileOperation? operation = null;
        try
        {
            Guid shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(target.Path, 0, in shellItemId, out item));
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(OperationClass, throwOnError: true)!)!;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(Silent | NoConfirmation | NoErrorUi | NoRecursion |
                NoConnectedElements | RecycleOnDelete | EarlyFailure));
            if (ownerWindow != 0)
            {
                Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(ownerWindow));
            }

            var sink = new RecycleProgress(target);
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item!, sink));
            int result = operation.PerformOperations();
            int query = operation.GetAnyOperationsAborted(out bool aborted);
            if (sink.Error is { } reason)
            {
                throw new IOException(reason);
            }

            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(query);
            if (aborted || !sink.Recycled)
            {
                throw new IOException("Windows did not report a completed move to the Recycle Bin.");
            }
        }
        finally
        {
            if (operation is not null)
            {
                Marshal.ReleaseComObject(operation);
            }

            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private static void VerifyVolume(DeletionTarget target)
    {
        string root = Path.GetPathRoot(target.Snapshot.FinalPath) ?? throw new IOException("The file volume could not be determined.");
        if (root.Length == 7 && root.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            root = root[4..];
        }

        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        int result = SHQueryRecycleBinW(root, ref info);
        if (result < 0)
        {
            throw new IOException($"The Recycle Bin is unavailable on volume {root} (0x{result:X8}).");
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleProgress(DeletionTarget target) : IFileOperationProgressSink
    {
        public string? Error { get; private set; }
        public bool Recycled { get; private set; }

        public int PreDeleteItem(uint flags, nint item)
        {
            if ((flags & RecycleTransfer) == 0)
            {
                Error = "Windows did not stage this file for recycling.";
                return Abort;
            }

            try
            {
                WindowsDeletionFile.Verify(target);
                return 0;
            }
            catch (Exception exception) when (PhotoDeletionService.IsFileError(exception))
            {
                Error = exception.Message;
                return Abort;
            }
        }

        public int PostDeleteItem(uint flags, nint item, int result, nint recycledItem)
        {
            Recycled = result >= 0 && recycledItem != 0;
            if (result < 0 && Error is null)
            {
                Error = Marshal.GetExceptionForHR(result)?.Message ?? $"Windows could not recycle this file (0x{result:X8}).";
            }

            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, nint item, nint name) => 0;
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint destination, nint name) => 0;
        public int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint destination, nint name) => 0;
        public int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreNewItem(uint flags, nint folder, nint name) => 0;
        public int PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public uint Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string root, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem;

    // The unused slots preserve the Windows Shell vtable layout.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage(nint message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, nint name, nint sink);
        [PreserveSig] int RenameItems(nint items, nint name);
        [PreserveSig] int MoveItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int MoveItems(nint items, nint destination);
        [PreserveSig] int CopyItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int CopyItems(nint items, nint destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint folder, uint attributes, nint name, nint template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint recycledItem);
        [PreserveSig] int PreNewItem(uint flags, nint folder, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint folder, nint name, nint template, uint attributes, int result, nint created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
