using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PhotosViewer.Core;
using PhotosViewer.Imaging.Wpf;
using PhotosViewer.Infrastructure;

namespace PhotosViewer.App.ViewModels;

internal sealed record FormatOption(string Label, string? Extension);
internal sealed record RatingOption(string Label, int? Value);
internal sealed record SortOption(string Label, PhotoSort Sort);
internal sealed record PhotoRow(ImmutableArray<PhotoItem> Items);

internal sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly PhotoDirectory _directory;
    private readonly JpegDecoder _decoder;
    private readonly PhotoRatingStore _ratings;
    private readonly ThumbnailLoader _thumbnails;
    private Dictionary<PhotoFile, PhotoItem> _photos = [];
    private ImmutableArray<PhotoItem> _matches = [];
    private CancellationTokenSource? _scan;
    private CancellationTokenSource? _detailsRead;
    private CancellationTokenSource? _catalogRead;
    private CancellationTokenSource? _viewerDecode;
    private Task _viewerWork = Task.CompletedTask;
    private Task _ratingWrites = Task.CompletedTask;
    private PhotoItem? _selectedPhoto;
    private DecodedJpeg? _viewerImage;
    private string? _folderError;
    private string? _ratingError;
    private string? _detailsStatus;
    private string? _viewerStatus;
    private string _nameFilter = string.Empty;
    private FormatOption _format;
    private RatingOption _ratingFilter;
    private SortOption _sort;
    private bool _descending;
    private bool _viewerOpen;
    private bool _fullResolutionRequested;
    private bool _closed;
    private int _columns = 1;
    private double _scrollOffset;
    private double _viewportHeight;

    public GalleryViewModel(PhotoDirectory directory, JpegDecoder decoder, PhotoRatingStore ratings)
    {
        _directory = directory;
        _decoder = decoder;
        _ratings = ratings;
        _thumbnails = new ThumbnailLoader(decoder);
        _format = Formats[0];
        _ratingFilter = RatingOptions[0];
        _sort = Sorts[0];
    }

    public ImmutableArray<FormatOption> Formats { get; } = [new("全部格式", null), new("JPG", ".jpg"), new("JPEG", ".jpeg")];
    public ImmutableArray<RatingOption> RatingOptions { get; } =
        [new("全部星级", null), new("未评分", PhotoRating.Unrated), new("1 星", 1), new("2 星", 2),
         new("3 星", 3), new("4 星", 4), new("5 星", PhotoRating.Highest), new("已拒绝", PhotoRating.Rejected)];
    public ImmutableArray<SortOption> Sorts { get; } =
        [new("文件名", PhotoSort.Name), new("拍摄时间", PhotoSort.CapturedAt), new("文件大小", PhotoSort.FileSize)];

    public ImmutableArray<PhotoRow> Rows { get; private set; } = [];
    public string? Folder { get; private set; }
    public bool CanRefresh => Folder is not null && !IsDeleting;
    public bool HasPhotos => !_matches.IsEmpty;
    public string CountLabel => $"显示 {_matches.Length:N0} 张  ·  共 {_photos.Count:N0} 张";

    public string? Activity => IsDeleting ? "正在处理删除…" : _scan is not null ? "正在读取目录…" : _folderError ?? _ratingError ??
        (_catalogRead is null ? _deletionStatus : SelectedRating.Value.HasValue ? "正在读取照片评分…" : "正在读取拍摄时间…");

    public string EmptyLabel => _scan is not null ? "正在读取照片…" : _folderError ??
        (_catalogRead is not null && SelectedRating.Value.HasValue ? "正在读取照片评分…" : null) ??
        (Folder is null ? "选择照片目录" : _photos.Count == 0 ? "此目录没有 JPG 或 JPEG 照片" : "没有符合条件的照片");

    public PhotoItem? SelectedPhoto => _selectedPhoto;
    public bool HasSelection => SelectedPhoto is not null;
    public ImmutableArray<PhotoDetail> Details => PhotoDetails.Format(SelectedPhoto);
    public string? DetailsStatus => _detailsStatus;

    public bool IsViewerOpen => _viewerOpen;
    public PhotoItem? ViewerPhoto => IsViewerOpen ? SelectedPhoto : null;
    public DecodedJpeg? ViewerImage => _viewerImage;
    public string? ViewerStatus => _viewerStatus;
    public bool CanMoveBack => IsViewerOpen && SelectionIndex > 0;
    public bool CanMoveForward => IsViewerOpen && SelectionIndex >= 0 && SelectionIndex < _matches.Length - 1;
    public string ViewerPosition => $"{SelectionIndex + 1} / {_matches.Length}";
    public int SelectedRow => SelectionIndex < 0 ? -1 : SelectionIndex / _columns;
    public int ViewerLongEdge { get; set; } = 1440;
    public Task RatingWrites => _ratingWrites;

    public string NameFilter
    {
        get => _nameFilter;
        set
        {
            if (Set(ref _nameFilter, value ?? string.Empty))
            {
                ApplyQuery();
            }
        }
    }

    public FormatOption SelectedFormat
    {
        get => _format;
        set
        {
            if (value is not null && Set(ref _format, value))
            {
                ApplyQuery();
            }
        }
    }

    public RatingOption SelectedRating
    {
        get => _ratingFilter;
        set
        {
            if (value is not null && Set(ref _ratingFilter, value))
            {
                ReadMissingCatalogMetadata();
                ApplyQuery();
            }
        }
    }

    public SortOption SelectedSort
    {
        get => _sort;
        set
        {
            if (value is not null && Set(ref _sort, value))
            {
                ReadMissingCatalogMetadata();
                ApplyQuery();
            }
        }
    }

    public bool Descending
    {
        get => _descending;
        set
        {
            if (Set(ref _descending, value))
            {
                ApplyQuery();
            }
        }
    }

    private int SelectionIndex => SelectedPhoto is null ? -1 : _matches.IndexOf(SelectedPhoto);

    public async Task OpenFolderAsync(string path)
    {
        Cancel(ref _scan);
        var request = new CancellationTokenSource();
        _scan = request;
        Cancel(ref _catalogRead);
        CloseViewer();
        _thumbnails.Clear();

        Folder = path;
        _folderError = null;
        _ratingError = null;
        _deletionStatus = null;
        _photos.Clear();
        _scrollOffset = 0;
        Notify(nameof(Folder));
        Notify(nameof(CanRefresh));
        ApplyQuery();

        try
        {
            var files = await _directory.ScanAsync(path, request.Token);
            var savedRatings = await ReadSavedRatingsAsync(request);
            if (ReferenceEquals(request, _scan))
            {
                _photos = files.ToDictionary(file => file, file => new PhotoItem(file,
                    savedRatings.TryGetValue(file.FullPath, out var saved) && saved.Matches(file) ? saved.Rating : null));
                ReadMissingCatalogMetadata();
                ApplyQuery();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("Could not open photo directory {0}: {1}", path, exception);
            if (ReferenceEquals(request, _scan))
            {
                _folderError = exception is UnauthorizedAccessException ? "没有权限读取此目录" : "无法读取此目录";
            }
        }
        finally
        {
            Finish(ref _scan, request);
            NotifyListState();
        }
    }

    public void UpdateViewport(double width, double height, double scrollOffset)
    {
        if (!(width > 0 && height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        _viewportHeight = height;
        _scrollOffset = Math.Max(0, scrollOffset);
        int columns = Math.Max(1, (int)(width / GalleryLayout.TileWidth));
        if (_columns != columns)
        {
            _columns = columns;
            PublishRows();
        }

        ScheduleThumbnails();
    }

    public Task SetRatingAsync(PhotoItem photo, int rating)
    {
        if (_closed || !photo.CanEditRating || !PhotoRating.IsValid(rating) || photo.Rating == rating)
        {
            return Task.CompletedTask;
        }

        photo.SetRatingSaving(true);
        _ratingWrites = SaveRatingAsync(_ratingWrites, photo, rating);
        return _ratingWrites;
    }

    private void SetActivePhoto(PhotoItem? photo)
    {
        if (ReferenceEquals(_selectedPhoto, photo))
        {
            return;
        }

        Cancel(ref _detailsRead);
        if (_selectedPhoto is not null)
        {
            _selectedPhoto.PropertyChanged -= OnSelectedPhotoChanged;
        }

        _selectedPhoto = photo;
        if (photo is not null)
        {
            photo.PropertyChanged += OnSelectedPhotoChanged;
        }

        Notify(nameof(SelectedPhoto));
        Notify(nameof(HasSelection));
        Notify(nameof(Details));
        NotifyViewerNavigation();
        ReadSelectedDetails(photo);
        if (IsViewerOpen)
        {
            if (photo is null)
            {
                CloseViewer();
            }
            else if (!IsDeleting)
            {
                ShowPhoto(photo);
            }
        }
    }

    public void OpenViewer()
    {
        if (SelectedPhoto is not { } photo || IsViewerOpen)
        {
            return;
        }

        Set(ref _viewerOpen, true, nameof(IsViewerOpen));
        NotifyViewerNavigation();
        NotifySelectionState();
        ScheduleThumbnails();
        ShowPhoto(photo);
    }

    public void CloseViewer()
    {
        if (!Set(ref _viewerOpen, false, nameof(IsViewerOpen)))
        {
            return;
        }

        Cancel(ref _viewerDecode);
        Set(ref _viewerImage, null, nameof(ViewerImage));
        Set(ref _viewerStatus, null, nameof(ViewerStatus));
        Notify(nameof(ViewerPhoto));
        NotifyViewerNavigation();
        NotifySelectionState();
        ScheduleThumbnails();
    }

    public void MoveViewer(int delta)
    {
        int next = SelectionIndex + delta;
        if (IsViewerOpen && next >= 0 && next < _matches.Length)
        {
            SelectPhoto(_matches[next]);
        }
    }

    public void RequestFullResolution()
    {
        if (ViewerPhoto is not { } photo || _fullResolutionRequested)
        {
            return;
        }

        if (ViewerImage is { } image && image.Bitmap.PixelWidth == image.Metadata.PixelWidth &&
            image.Bitmap.PixelHeight == image.Metadata.PixelHeight)
        {
            return;
        }

        _fullResolutionRequested = true;
        StartViewerDecode(photo, null);
    }

    public void Dispose()
    {
        _closed = true;
        Cancel(ref _scan);
        Cancel(ref _catalogRead);
        CloseViewer();
        SelectPhoto(null);
        _thumbnails.Clear();
    }

    private void ApplyQuery()
    {
        int? rating = SelectedRating.Value;
        var candidates = rating.HasValue ? _photos.Keys.Where(file => _photos[file].Rating == rating) : _photos.Keys;
        _matches = PhotoQuery.Apply(candidates, NameFilter, SelectedFormat.Extension, SelectedSort.Sort, Descending,
                file => _photos[file].CapturedAt)
            .Select(file => _photos[file])
            .ToImmutableArray();

        PublishRows();
        ReconcileSelection();

        NotifyListState();
        NotifyViewerNavigation();
        ScheduleThumbnails();
    }

    private void PublishRows()
    {
        Rows = _matches.Chunk(_columns).Select(items => new PhotoRow(items.ToImmutableArray())).ToImmutableArray();
        Notify(nameof(Rows));
    }

    private void ScheduleThumbnails()
    {
        if (_closed || IsDeleting || IsViewerOpen || _matches.IsEmpty || _viewportHeight <= 0)
        {
            _thumbnails.SetTargets([], []);
            return;
        }

        double offset = Math.Min(_scrollOffset, Math.Max(0, Rows.Length * GalleryLayout.TileHeight - _viewportHeight));
        int first = (int)Math.Floor(offset / GalleryLayout.TileHeight) * _columns;
        int end = Math.Min(_matches.Length, (int)Math.Ceiling((offset + _viewportHeight) / GalleryLayout.TileHeight) * _columns);
        int page = end - first;
        var visible = _matches.Skip(first).Take(page);
        var nearby = _matches.Skip(end).Take(page).Concat(_matches.Skip(Math.Max(0, first - page)).Take(Math.Min(page, first)));
        _thumbnails.SetTargets(visible, nearby);
    }

    private void OnSelectedPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoItem.Metadata) or nameof(PhotoItem.CapturedAt))
        {
            Notify(nameof(Details));
        }
    }

    private void ReadSelectedDetails(PhotoItem? photo)
    {
        if (photo is null || photo.Metadata is not null)
        {
            Set(ref _detailsStatus, null, nameof(DetailsStatus));
            return;
        }

        var request = new CancellationTokenSource();
        _detailsRead = request;
        Set(ref _detailsStatus, "正在读取照片信息…", nameof(DetailsStatus));
        _ = ReadDetailsAsync(photo, request);
    }

    private async Task ReadDetailsAsync(PhotoItem photo, CancellationTokenSource request)
    {
        try
        {
            var metadata = await _decoder.ReadMetadataAsync(photo.File, request.Token);
            if (ReferenceEquals(request, _detailsRead))
            {
                photo.SetMetadata(metadata);
                Set(ref _detailsStatus, null, nameof(DetailsStatus));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (PhotoReadError.IsExpected(exception))
        {
            PhotoReadError.Log(photo.File.FullPath, exception);
            if (ReferenceEquals(request, _detailsRead))
            {
                Set(ref _detailsStatus, "无法读取照片信息", nameof(DetailsStatus));
            }
        }
        finally
        {
            Finish(ref _detailsRead, request);
        }
    }

    private async Task<ImmutableDictionary<string, SavedPhotoRating>> ReadSavedRatingsAsync(CancellationTokenSource request)
    {
        try
        {
            await _ratingWrites.WaitAsync(request.Token);
            return await _ratings.ReadAsync(request.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not read saved photo ratings: {0}", exception);
            if (ReferenceEquals(request, _scan))
            {
                _ratingError = "无法读取本机评分";
            }

            return ImmutableDictionary<string, SavedPhotoRating>.Empty;
        }
    }

    private async Task SaveRatingAsync(Task previous, PhotoItem photo, int rating)
    {
        try
        {
            await previous;
            var saved = await _ratings.SaveAsync(photo.File, rating);
            photo.SetSavedRating(saved.Rating);
            foreach (var current in _photos.Values)
            {
                if (string.Equals(current.File.FullPath, photo.File.FullPath, StringComparison.OrdinalIgnoreCase) && saved.Matches(current.File))
                {
                    current.SetSavedRating(saved.Rating);
                    break;
                }
            }

            _ratingError = null;
            if (!_closed && SelectedRating.Value.HasValue)
            {
                ApplyQuery();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not save the photo rating for {0}: {1}", photo.File.FullPath, exception);
            _ratingError = "无法保存评分";
        }
        finally
        {
            photo.SetRatingSaving(false);
            Notify(nameof(Activity));
        }
    }

    private void ReadMissingCatalogMetadata()
    {
        bool readDates = SelectedSort.Sort == PhotoSort.CapturedAt;
        bool readRatings = SelectedRating.Value.HasValue;
        if (!readDates && !readRatings)
        {
            Cancel(ref _catalogRead);
            return;
        }

        if (_catalogRead is not null)
        {
            return;
        }

        var missing = _photos.Values.Where(photo => (readDates && !photo.CaptureDateRead) ||
            (readRatings && photo.Rating is null && !photo.RatingRead)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var request = new CancellationTokenSource();
        _catalogRead = request;
        _ = ReadCatalogMetadataAsync(missing, request);
    }

    private async Task ReadCatalogMetadataAsync(PhotoItem[] photos, CancellationTokenSource request)
    {
        bool updated = false;
        try
        {
            var metadata = await CollectCatalogMetadataAsync(photos, request.Token);
            if (!ReferenceEquals(request, _catalogRead))
            {
                return;
            }

            for (int index = 0; index < photos.Length; index++)
            {
                if (!photos[index].CaptureDateRead)
                {
                    photos[index].SetCaptureDate(metadata[index].CapturedAt);
                }

                if (!photos[index].RatingRead)
                {
                    photos[index].SetEmbeddedRating(metadata[index].Rating);
                }
            }

            updated = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            Finish(ref _catalogRead, request);
            if (updated)
            {
                ReadMissingCatalogMetadata();
                ApplyQuery();
            }
        }
    }

    private async Task<(DateTime? CapturedAt, int? Rating)[]> CollectCatalogMetadataAsync(PhotoItem[] photos, CancellationToken cancellationToken)
    {
        var results = new (DateTime? CapturedAt, int? Rating)[photos.Length];
        for (int index = 0; index < photos.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = photos[index].File;
            try
            {
                var metadata = await _decoder.ReadCatalogMetadataAsync(file, cancellationToken).ConfigureAwait(false);
                results[index] = (metadata.CapturedAt, metadata.Rating);
            }
            catch (Exception exception) when (PhotoReadError.IsExpected(exception))
            {
                PhotoReadError.Log(file.FullPath, exception);
            }
        }

        return results;
    }

    private void ShowPhoto(PhotoItem photo)
    {
        _fullResolutionRequested = false;
        Notify(nameof(ViewerPhoto));
        DecodedJpeg? preview = photo.Thumbnail is { } bitmap && photo.Metadata is { } metadata ? new(bitmap, metadata) : null;
        Set(ref _viewerImage, preview, nameof(ViewerImage));
        StartViewerDecode(photo, ViewerLongEdge);
    }

    private void StartViewerDecode(PhotoItem photo, int? longEdge)
    {
        Cancel(ref _viewerDecode);
        var request = new CancellationTokenSource();
        _viewerDecode = request;
        Set(ref _viewerStatus, longEdge.HasValue ? "正在加载大图…" : "正在加载原始分辨率…", nameof(ViewerStatus));
        _viewerWork = DecodeViewerAsync(_viewerWork, photo, longEdge, request);
    }

    private async Task DecodeViewerAsync(Task previous, PhotoItem photo, int? longEdge, CancellationTokenSource request)
    {
        try
        {
            await previous;
            request.Token.ThrowIfCancellationRequested();
            var image = await _decoder.DecodeAsync(photo.File, longEdge, request.Token);
            if (ReferenceEquals(request, _viewerDecode))
            {
                photo.SetMetadata(image.Metadata);
                Set(ref _viewerImage, image, nameof(ViewerImage));
                Set(ref _viewerStatus, null, nameof(ViewerStatus));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (PhotoReadError.IsExpected(exception))
        {
            PhotoReadError.Log(photo.File.FullPath, exception);
            if (ReferenceEquals(request, _viewerDecode))
            {
                _fullResolutionRequested = false;
                Set(ref _viewerStatus, "无法解码此照片", nameof(ViewerStatus));
            }
        }
        finally
        {
            Finish(ref _viewerDecode, request);
        }
    }

    private void NotifyListState()
    {
        Notify(nameof(HasPhotos));
        Notify(nameof(EmptyLabel));
        Notify(nameof(CountLabel));
        Notify(nameof(Activity));
    }

    private void NotifyViewerNavigation()
    {
        Notify(nameof(CanMoveBack));
        Notify(nameof(CanMoveForward));
        Notify(nameof(ViewerPosition));
    }

    private static void Cancel(ref CancellationTokenSource? active)
    {
        var request = active;
        active = null;
        request?.Cancel();
    }

    private static void Finish(ref CancellationTokenSource? active, CancellationTokenSource request)
    {
        if (ReferenceEquals(active, request))
        {
            active = null;
        }

        request.Dispose();
    }
}
