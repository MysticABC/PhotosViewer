using System.Collections.Immutable;
using PhotosViewer.Imaging.Wpf;

namespace PhotosViewer.App.ViewModels;

internal sealed class ThumbnailLoader(JpegDecoder decoder)
{
    private const int LongEdge = 320;
    private const int CacheLimit = 96;
    private const int Concurrency = 2;

    private sealed class ThumbnailRequest
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
    }

    private readonly Dictionary<PhotoItem, ThumbnailRequest> _running = [];
    private readonly Dictionary<PhotoItem, LinkedListNode<PhotoItem>> _resident = [];
    private readonly LinkedList<PhotoItem> _lru = [];
    private ImmutableArray<PhotoItem> _targets = [];
    private HashSet<PhotoItem> _visible = [];
    private HashSet<PhotoItem> _wanted = [];

    public void SetTargets(IEnumerable<PhotoItem> visible, IEnumerable<PhotoItem> nearby)
    {
        var visibleItems = visible.ToImmutableArray();
        _visible = visibleItems.ToHashSet();
        _targets = visibleItems.Concat(nearby).Take(Capacity).ToImmutableArray();
        _wanted = _targets.ToHashSet();

        foreach (var (photo, request) in _running)
        {
            if (!_wanted.Contains(photo))
            {
                request.Cancellation.Cancel();
            }
        }

        foreach (var photo in _targets)
        {
            if (_resident.TryGetValue(photo, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
        }

        Trim();
        FillSlots();
    }

    public void Clear()
    {
        SetTargets([], []);
        foreach (var photo in _lru)
        {
            photo.SetThumbnail(null);
        }

        _resident.Clear();
        _lru.Clear();
    }

    public Task PauseAsync()
    {
        SetTargets([], []);
        return Task.WhenAll(_running.Values.Select(request => request.Work));
    }

    public void Forget(PhotoItem photo)
    {
        if (_resident.Remove(photo, out var node))
        {
            _lru.Remove(node);
            photo.SetThumbnail(null);
        }
    }

    // A large monitor can show more cells than the usual cache budget.
    private int Capacity => Math.Max(CacheLimit, _visible.Count);

    private void FillSlots()
    {
        foreach (var photo in _targets)
        {
            if (_running.Count >= Concurrency)
            {
                return;
            }

            if (photo.Thumbnail is not null || photo.ThumbnailFailed || _running.ContainsKey(photo))
            {
                continue;
            }

            var request = new ThumbnailRequest();
            _running.Add(photo, request);
            request.Work = LoadAsync(photo, request.Cancellation);
        }
    }

    private async Task LoadAsync(PhotoItem photo, CancellationTokenSource request)
    {
        try
        {
            var result = await decoder.DecodeAsync(photo.File, LongEdge, request.Token);
            if (!request.IsCancellationRequested && _wanted.Contains(photo))
            {
                photo.SetMetadata(result.Metadata);
                photo.SetThumbnail(result.Bitmap);
                _resident.Add(photo, _lru.AddFirst(photo));
                Trim();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (PhotoReadError.IsExpected(exception))
        {
            PhotoReadError.Log(photo.File.FullPath, exception);
            if (!request.IsCancellationRequested)
            {
                photo.SetThumbnailFailed();
            }
        }
        finally
        {
            _running.Remove(photo);
            request.Dispose();
            FillSlots();
        }
    }

    private void Trim()
    {
        while (_resident.Count > Capacity)
        {
            var node = _lru.Last!;
            while (_visible.Contains(node.Value))
            {
                node = node.Previous!;
            }

            _lru.Remove(node);
            _resident.Remove(node.Value);
            node.Value.SetThumbnail(null);
        }
    }
}
