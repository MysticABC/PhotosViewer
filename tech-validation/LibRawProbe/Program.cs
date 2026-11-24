using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sdcb.LibRaw;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int ProgressCallback(IntPtr data, int stage, int iteration, int expected);

static class Native
{
    [DllImport("raw_r", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_init(uint flags);

    [DllImport("raw_r", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern int libraw_open_wfile(IntPtr context, string path);

    [DllImport("raw_r", CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_close(IntPtr context);

    [DllImport("raw_r", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libraw_strerror(int errorCode);

    [DllImport("raw_r", CallingConvention = CallingConvention.Cdecl)]
    public static extern void libraw_set_progress_handler(IntPtr context, ProgressCallback callback, IntPtr data);
}

static class Program
{
    private static int _cancelCallbackCount;
    private static readonly ProgressCallback CancelImmediately = (_, _, _, _) =>
    {
        Interlocked.Increment(ref _cancelCallbackCount);
        return 1;
    };

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: LibRawProbe <raw-directory> <unicode-copy.CR3> <output-directory>");
            return 2;
        }

        string rawDirectory = args[0];
        string unicodePath = args[1];
        string outputDirectory = args[2];
        Directory.CreateDirectory(outputDirectory);

        var result = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O"),
            ["runtime"] = Environment.Version.ToString(),
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["rawDirectory"] = rawDirectory,
            ["unicodePath"] = unicodePath
        };

        string[] rawFiles = Directory.GetFiles(rawDirectory, "*.CR3", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        result["rawFileCount"] = rawFiles.Length;
        result["pairJpegCount"] = Directory.GetFiles(rawDirectory, "*.JPG", SearchOption.TopDirectoryOnly).Length;

        Console.WriteLine($"RAW files: {rawFiles.Length}");
        Console.WriteLine($"JPG files: {result["pairJpegCount"]}");

        result["nativeExports"] = CheckNativeExports();
        result["directUnicodeOpen"] = TestDirectUnicodeOpen(unicodePath);

        using (var context = RawContext.OpenFile(unicodePath))
        {
            result["libRawVersion"] = GetStaticOrInstanceValue(context, "Version")?.ToString();
            result["libRawVersionNumber"] = GetStaticOrInstanceValue(context, "VersionNumber")?.ToString();
            result["camera"] = DumpPublicProperties(context.ImageParams);
            result["size"] = new
            {
                context.Width,
                context.Height,
                context.RawWidth,
                context.RawHeight,
                Decoder = context.DecoderInfo.DecoderName
            };

            Console.WriteLine($"LibRaw: {result["libRawVersion"]} ({result["libRawVersionNumber"]})");
            Console.WriteLine($"Camera: {JsonSerializer.Serialize(result["camera"])}");
            Console.WriteLine($"Size: {context.Width}x{context.Height}, raw {context.RawWidth}x{context.RawHeight}, decoder {context.DecoderInfo.DecoderName}");

            using ProcessedImage thumbnail = context.ExportThumbnail(0);
            string extension = thumbnail.ImageType.ToString().Contains("Jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".bin";
            string thumbnailPath = Path.Combine(outputDirectory, "libraw-embedded-preview" + extension);
            File.WriteAllBytes(thumbnailPath, thumbnail.AsSpan<byte>().ToArray());
            result["embeddedPreview"] = new
            {
                Path = thumbnailPath,
                Type = thumbnail.ImageType.ToString(),
                thumbnail.Width,
                thumbnail.Height,
                thumbnail.DataSize,
                thumbnail.Channels,
                thumbnail.Bits
            };
            Console.WriteLine($"Embedded preview: {thumbnail.Width}x{thumbnail.Height}, {thumbnail.ImageType}, {thumbnail.DataSize} bytes");
        }

        result["allEmbeddedPreviews"] = TestAllEmbeddedPreviews(rawFiles);
        result["allFullSizePreviews"] = TestAllFullSizePreviews(rawFiles);
        result["thumbnailIndexes"] = TestThumbnailIndexes(unicodePath, outputDirectory);
        result["fullDecode"] = TestFullDecode(rawFiles[0], outputDirectory);
        result["repeatedFullDecodeMemoryMiB"] = TestRepeatedFullDecode(rawFiles, 3);
        result["cancellation"] = TestCancellation(rawFiles[0]);

        string jsonPath = Path.Combine(outputDirectory, "libraw-validation.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report: {jsonPath}");

        return 0;
    }

    static object CheckNativeExports()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "raw_r.dll");
        if (!File.Exists(dll))
        {
            dll = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget/packages/sdcb.libraw.runtime.win64/0.21.1/runtimes/win-x64/native/raw_r.dll");
        }

        IntPtr handle = NativeLibrary.Load(dll);
        string[] names =
        [
            "libraw_init", "libraw_close", "libraw_open_file", "libraw_open_wfile",
            "libraw_unpack", "libraw_unpack_thumb", "libraw_unpack_thumb_ex",
            "libraw_dcraw_process", "libraw_dcraw_make_mem_image", "libraw_dcraw_make_mem_thumb",
            "libraw_dcraw_clear_mem", "libraw_set_progress_handler", "libraw_recycle", "libraw_strerror"
        ];

        var exports = new Dictionary<string, bool>();
        foreach (string name in names)
        {
            exports[name] = NativeLibrary.TryGetExport(handle, name, out _);
        }
        NativeLibrary.Free(handle);

        Console.WriteLine("Native exports: " + string.Join(", ", exports.Where(x => x.Value).Select(x => x.Key)));
        return new { Dll = dll, Exports = exports };
    }

    static object TestDirectUnicodeOpen(string path)
    {
        IntPtr context = Native.libraw_init(0);
        if (context == IntPtr.Zero)
        {
            return new { Passed = false, Error = "libraw_init returned NULL" };
        }

        try
        {
            int code = Native.libraw_open_wfile(context, path);
            string error = Marshal.PtrToStringAnsi(Native.libraw_strerror(code)) ?? string.Empty;
            Console.WriteLine($"Direct libraw_open_wfile: code={code}, error={error}");
            return new { Passed = code == 0, Code = code, Error = error };
        }
        finally
        {
            Native.libraw_close(context);
        }
    }

    static object TestAllEmbeddedPreviews(string[] rawFiles)
    {
        var rows = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        foreach (string file in rawFiles)
        {
            try
            {
                using var context = RawContext.OpenFile(file);
                using ProcessedImage thumbnail = context.ExportThumbnail(0);
                Span<byte> thumbnailBytes = thumbnail.AsSpan<byte>();
                byte[] header = thumbnailBytes[..Math.Min(4, thumbnailBytes.Length)].ToArray();
                rows.Add(new
                {
                    File = Path.GetFileName(file),
                    Passed = true,
                    Type = thumbnail.ImageType.ToString(),
                    thumbnail.Width,
                    thumbnail.Height,
                    thumbnail.DataSize,
                    Header = Convert.ToHexString(header)
                });
            }
            catch (Exception ex)
            {
                rows.Add(new { File = Path.GetFileName(file), Passed = false, Error = ex.GetType().Name + ": " + ex.Message });
            }
        }
        stopwatch.Stop();

        int passed = rows.Count(x => (bool)x.GetType().GetProperty("Passed")!.GetValue(x)!);
        Console.WriteLine($"Embedded previews: {passed}/{rawFiles.Length} passed in {stopwatch.ElapsedMilliseconds} ms");
        return new { Passed = passed, Total = rawFiles.Length, ElapsedMs = stopwatch.ElapsedMilliseconds, Rows = rows };
    }

    static object TestThumbnailIndexes(string rawPath, string outputDirectory)
    {
        var rows = new List<object>();
        int bestIndex = -1;
        int bestPixels = 0;
        string? bestPath = null;

        for (int index = 0; index < 10; index++)
        {
            try
            {
                using var context = RawContext.OpenFile(rawPath);
                using ProcessedImage thumbnail = context.ExportThumbnail(index);
                byte[] thumbnailBytes = thumbnail.AsSpan<byte>().ToArray();
                bool isJpeg = thumbnail.ImageType.ToString().Contains("Jpeg", StringComparison.OrdinalIgnoreCase);
                (int width, int height) = isJpeg
                    ? ReadJpegDimensions(thumbnailBytes)
                    : (thumbnail.Width, thumbnail.Height);
                int pixels = width * height;
                string extension = isJpeg ? ".jpg" : ".bin";
                string path = Path.Combine(outputDirectory, $"preview-index-{index}{extension}");
                File.WriteAllBytes(path, thumbnailBytes);

                rows.Add(new
                {
                    Index = index,
                    Passed = true,
                    Type = thumbnail.ImageType.ToString(),
                    Width = width,
                    Height = height,
                    thumbnail.DataSize,
                    Path = path
                });

                if (pixels > bestPixels)
                {
                    bestPixels = pixels;
                    bestIndex = index;
                    bestPath = path;
                }
            }
            catch (Exception ex)
            {
                rows.Add(new { Index = index, Passed = false, Error = ex.GetType().Name + ": " + ex.Message });
                break;
            }
        }

        Console.WriteLine($"Largest embedded preview: index={bestIndex}, pixels={bestPixels}, path={bestPath}");
        return new { BestIndex = bestIndex, BestPixels = bestPixels, BestPath = bestPath, Rows = rows };
    }

    static object TestAllFullSizePreviews(string[] rawFiles)
    {
        var rows = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        foreach (string file in rawFiles)
        {
            try
            {
                using var context = RawContext.OpenFile(file);
                using ProcessedImage thumbnail = context.ExportThumbnail(2);
                byte[] bytes = thumbnail.AsSpan<byte>().ToArray();
                (int width, int height) = ReadJpegDimensions(bytes);
                rows.Add(new
                {
                    File = Path.GetFileName(file),
                    Passed = width >= 5000 && height >= 3000,
                    Width = width,
                    Height = height,
                    DataSize = bytes.Length
                });
            }
            catch (Exception ex)
            {
                rows.Add(new { File = Path.GetFileName(file), Passed = false, Error = ex.GetType().Name + ": " + ex.Message });
            }
        }
        stopwatch.Stop();

        int passed = rows.Count(x => (bool)x.GetType().GetProperty("Passed")!.GetValue(x)!);
        Console.WriteLine($"Full-size previews: {passed}/{rawFiles.Length} passed in {stopwatch.ElapsedMilliseconds} ms");
        return new { Passed = passed, Total = rawFiles.Length, ElapsedMs = stopwatch.ElapsedMilliseconds, Rows = rows };
    }

    static (int Width, int Height) ReadJpegDimensions(byte[] data)
    {
        int offset = 2;
        while (offset + 9 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (offset + 2 > data.Length)
            {
                break;
            }

            int length = (data[offset] << 8) | data[offset + 1];
            bool isStartOfFrame = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
            if (isStartOfFrame && offset + 7 < data.Length)
            {
                int height = (data[offset + 3] << 8) | data[offset + 4];
                int width = (data[offset + 5] << 8) | data[offset + 6];
                return (width, height);
            }
            offset += Math.Max(length, 2);
        }
        return (0, 0);
    }

    static object TestFullDecode(string rawPath, string outputDirectory)
    {
        var stopwatch = Stopwatch.StartNew();
        using var context = RawContext.OpenFile(rawPath);
        context.Unpack();
        context.DcrawProcess(parameters =>
        {
            SetProperty(parameters, "OutputBps", 8);
            SetProperty(parameters, "HalfSize", false);
            SetProperty(parameters, "UseCameraWb", true);
        });

        string tiffPath = Path.Combine(outputDirectory, "libraw-full-decode.tiff");
        context.WriteDcrawPpmTiff(tiffPath);
        using ProcessedImage image = context.MakeDcrawMemoryImage();

        ReadOnlySpan<byte> pixels = image.AsSpan<byte>();
        long sampleSum = 0;
        for (int i = 0; i < 64; i++)
        {
            sampleSum += pixels[(int)((long)i * pixels.Length / 64)];
        }

        Process process = Process.GetCurrentProcess();
        process.Refresh();
        stopwatch.Stop();

        var summary = new
        {
            Passed = image.Width > 0 && image.Height > 0 && image.DataSize > 0 && sampleSum > 0,
            File = Path.GetFileName(rawPath),
            image.Width,
            image.Height,
            image.Channels,
            image.Bits,
            image.DataSize,
            SampleSum = sampleSum,
            TiffPath = tiffPath,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            PrivateMemoryMiB = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
            PeakWorkingSetMiB = Math.Round(process.PeakWorkingSet64 / 1024d / 1024d, 1)
        };

        Console.WriteLine($"Full decode: {summary.Width}x{summary.Height}, {summary.Channels}ch/{summary.Bits}bit, {summary.DataSize} bytes, {summary.ElapsedMs} ms, private {summary.PrivateMemoryMiB} MiB");
        return summary;
    }

    static object TestRepeatedFullDecode(string[] rawFiles, int iterations)
    {
        var samples = new List<object>();
        for (int i = 0; i < iterations; i++)
        {
            string file = rawFiles[i % rawFiles.Length];
            var stopwatch = Stopwatch.StartNew();
            using (var context = RawContext.OpenFile(file))
            {
                context.Unpack();
                context.DcrawProcess(parameters =>
                {
                    SetProperty(parameters, "OutputBps", 8);
                    SetProperty(parameters, "HalfSize", false);
                });
                using ProcessedImage image = context.MakeDcrawMemoryImage();
                _ = image.DataSize;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            stopwatch.Stop();
            samples.Add(new
            {
                Iteration = i + 1,
                File = Path.GetFileName(file),
                PrivateMemoryMiB = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
                ManagedHeapMiB = Math.Round(GC.GetTotalMemory(true) / 1024d / 1024d, 1),
                ElapsedMs = stopwatch.ElapsedMilliseconds
            });
        }

        Console.WriteLine("Repeated full decode memory: " + string.Join(", ", samples.Select(x => x.GetType().GetProperty("PrivateMemoryMiB")!.GetValue(x) + " MiB")));
        return samples;
    }

    static object TestCancellation(string rawPath)
    {
        _cancelCallbackCount = 0;
        string? exception = null;
        try
        {
            using var context = RawContext.OpenFile(rawPath);
            Native.libraw_set_progress_handler(context.UnsafeGetHandle(), CancelImmediately, IntPtr.Zero);
            context.Unpack();
            context.DcrawProcess();
        }
        catch (Exception ex)
        {
            exception = ex.GetType().Name + ": " + ex.Message;
        }

        bool passed = _cancelCallbackCount > 0 && exception is not null;
        Console.WriteLine($"Cancellation: callbacks={_cancelCallbackCount}, exception={exception ?? "none"}");
        return new { Passed = passed, CallbackCount = _cancelCallbackCount, Exception = exception };
    }

    static object? GetStaticOrInstanceValue(object instance, string propertyName)
    {
        PropertyInfo? property = typeof(RawContext).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        if (property?.GetMethod is null)
        {
            return null;
        }
        return property.GetMethod.IsStatic ? property.GetValue(null) : property.GetValue(instance);
    }

    static Dictionary<string, object?> DumpPublicProperties(object value)
    {
        var result = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0)
            .ToDictionary(x => x.Name, x =>
            {
                try { return (object?)x.GetValue(value)?.ToString(); }
                catch (Exception ex) { return "ERROR: " + ex.Message; }
            });

        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (result.ContainsKey(field.Name))
            {
                continue;
            }
            try { result[field.Name] = field.GetValue(value)?.ToString(); }
            catch (Exception ex) { result[field.Name] = "ERROR: " + ex.Message; }
        }
        return result;
    }

    static void SetProperty(object target, string name, object value)
    {
        PropertyInfo? property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanWrite == true && property.PropertyType == value.GetType())
        {
            property.SetValue(target, value);
        }
    }
}
