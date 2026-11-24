using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotosViewer.App.ViewModels;

internal static class PhotoReadError
{
    public static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or COMException or ArgumentException;

    public static void Log(string path, Exception exception) =>
        Trace.TraceWarning("Could not read photo {0}: {1}", path, exception);
}
