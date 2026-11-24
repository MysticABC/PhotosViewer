using System.Collections.Immutable;
using PhotosViewer.Core;
using PhotosViewer.Infrastructure;

namespace PhotosViewer.App.ViewModels;

internal sealed partial class GalleryViewModel
{
    private HashSet<PhotoItem> _selection = [];
    private PhotoItem? _selectionAnchor;
    private bool _isDeleting;
    private string? _deletionStatus;

    public bool IsDeleting => _isDeleting;
    public bool CanSelectAll => !_isDeleting && !IsViewerOpen && HasPhotos;
    public bool CanDelete => !_isDeleting && (IsViewerOpen ? SelectedPhoto is not null : _selection.Count > 0);
    public string SelectionLabel => $"已选 {(IsViewerOpen ? 1 : _selection.Count):N0} 张";

    public void SelectPhoto(PhotoItem? photo)
    {
        SetSelection(photo is null ? [] : [photo]);
        _selectionAnchor = photo;
        SetActivePhoto(photo);
        NotifySelectionState();
    }

    public void SelectPhoto(PhotoItem photo, bool extend, bool toggle)
    {
        int clicked = _matches.IndexOf(photo);
        if (clicked < 0)
        {
            return;
        }

        int anchor = _selectionAnchor is null ? -1 : _matches.IndexOf(_selectionAnchor);
        if (extend && anchor >= 0)
        {
            var range = _matches.Skip(Math.Min(clicked, anchor)).Take(Math.Abs(clicked - anchor) + 1);
            SetSelection(toggle ? _selection.Concat(range) : range);
            SetActivePhoto(photo);
        }
        else if (toggle)
        {
            if (_selection.Remove(photo))
            {
                photo.IsSelected = false;
                SetActivePhoto(_matches.FirstOrDefault(_selection.Contains) ?? photo);
            }
            else
            {
                _selection.Add(photo);
                photo.IsSelected = true;
                SetActivePhoto(photo);
            }

            _selectionAnchor = photo;
        }
        else
        {
            SelectPhoto(photo);
            return;
        }

        NotifySelectionState();
    }

    public void SelectAll()
    {
        if (!CanSelectAll)
        {
            return;
        }

        SetSelection(_matches);
        _selectionAnchor = SelectedPhoto ?? _matches[0];
        SetActivePhoto(_selectionAnchor);
        NotifySelectionState();
    }

    public ImmutableArray<PhotoFile> DeletionSelection()
    {
        if (!CanDelete)
        {
            return [];
        }

        return IsViewerOpen ? [SelectedPhoto!.File] : _matches.Where(_selection.Contains).Select(photo => photo.File).ToImmutableArray();
    }

    public void BeginDeletion()
    {
        _deletionStatus = null;
        SetDeleting(true);
    }

    public async Task ReleaseForDeletionAsync(ImmutableArray<PhotoFile> files)
    {
        Cancel(ref _catalogRead);
        Cancel(ref _detailsRead);
        Cancel(ref _viewerDecode);
        Set(ref _viewerImage, null, nameof(ViewerImage));
        Set(ref _viewerStatus, IsViewerOpen ? "正在删除照片…" : null, nameof(ViewerStatus));

        var thumbnails = _thumbnails.PauseAsync();
        foreach (var file in files)
        {
            if (_photos.TryGetValue(file, out var photo))
            {
                _thumbnails.Forget(photo);
            }
        }

        await Task.WhenAll(thumbnails, _viewerWork);
    }

    public void ApplyDeletionReport(PhotoDeletionReport report, PhotoDeletionMode mode)
    {
        foreach (var file in report.Deleted)
        {
            _photos.Remove(file);
        }

        string action = mode == PhotoDeletionMode.Recycle ? "已移入回收站" : "已永久删除";
        _deletionStatus = report.Failures.IsEmpty
            ? $"{action} {report.Deleted.Length:N0} 个文件"
            : $"{action} {report.Deleted.Length:N0} 个文件 · 失败 {report.Failures.Length:N0} 项";
        ApplyQuery();
    }

    public void EndDeletion()
    {
        SetDeleting(false);
        if (IsViewerOpen && ViewerImage is null && SelectedPhoto is { } photo)
        {
            ShowPhoto(photo);
        }

        if (_detailsRead is null)
        {
            ReadSelectedDetails(SelectedPhoto);
        }

        ReadMissingCatalogMetadata();
        ScheduleThumbnails();
        Notify(nameof(Activity));
    }

    private void SetDeleting(bool value)
    {
        Set(ref _isDeleting, value, nameof(IsDeleting));
        Notify(nameof(CanRefresh));
        Notify(nameof(Activity));
        NotifySelectionState();
    }

    private void ReconcileSelection()
    {
        var visible = _matches.ToHashSet();
        SetSelection(_selection.Where(visible.Contains));
        if (_selectionAnchor is not null && !visible.Contains(_selectionAnchor))
        {
            _selectionAnchor = null;
        }

        if (SelectedPhoto is null || !visible.Contains(SelectedPhoto))
        {
            var next = _matches.FirstOrDefault(_selection.Contains);
            if (next is null)
            {
                SelectPhoto(_matches.FirstOrDefault());
                return;
            }

            SetActivePhoto(next);
        }

        NotifySelectionState();
    }

    private void SetSelection(IEnumerable<PhotoItem> photos)
    {
        var selection = photos.ToHashSet();
        foreach (var photo in _selection)
        {
            if (!selection.Contains(photo))
            {
                photo.IsSelected = false;
            }
        }

        foreach (var photo in selection)
        {
            photo.IsSelected = true;
        }

        _selection = selection;
    }

    private void NotifySelectionState()
    {
        Notify(nameof(CanSelectAll));
        Notify(nameof(CanDelete));
        Notify(nameof(SelectionLabel));
    }
}
