using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotosViewer.App.ViewModels;
using PhotosViewer.Core;
using PhotosViewer.Infrastructure;

namespace PhotosViewer.App;

public partial class MainWindow : Window
{
    private readonly GalleryViewModel _viewModel;
    private readonly PhotoDeletionService _deletions = new();
    private ScrollViewer? _galleryScroll;
    private bool _viewportUpdatePending;
    private bool _closeAfterRatingWrite;

    internal MainWindow(GalleryViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        PhotoSurface.ViewChanged += (_, _) => ZoomValue.Text = PhotoSurface.ZoomLabel;
        PhotoSurface.FullResolutionRequested += (_, _) => viewModel.RequestFullResolution();
        Loaded += (_, _) => QueueViewportUpdate();
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Dispose();
        };
    }

    private async Task ChooseFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择照片目录",
            InitialDirectory = _viewModel.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.OpenFolderAsync(dialog.FolderName);
        }
    }

    private Task RefreshFolderAsync() => _viewModel.Folder is { } folder ? _viewModel.OpenFolderAsync(folder) : Task.CompletedTask;

    private async void OnOpenFolder(object sender, RoutedEventArgs e) => await ChooseFolderAsync();

    private async void OnRefreshFolder(object sender, RoutedEventArgs e) => await RefreshFolderAsync();

    private void OnSelectPhoto(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PhotoItem photo })
        {
            var modifiers = Keyboard.Modifiers;
            _viewModel.SelectPhoto(photo, modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    private void OnOpenPhoto(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = OpenFromTile(sender);
        }
    }

    private void OnPhotoTileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = OpenFromTile(sender);
        }
    }

    private bool OpenFromTile(object sender)
    {
        if (sender is FrameworkElement { DataContext: PhotoItem photo })
        {
            _viewModel.SelectPhoto(photo);
            _viewModel.OpenViewer();
            return true;
        }

        return false;
    }

    private async void OnRatingRequested(object? sender, RatingRequestedEventArgs e)
    {
        if (sender is RatingEditor { DataContext: PhotoItem photo })
        {
            await _viewModel.SetRatingAsync(photo, e.Value);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel.IsDeleting)
        {
            e.Cancel = true;
            return;
        }

        if (e.Cancel || _viewModel.RatingWrites.IsCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_closeAfterRatingWrite)
        {
            return;
        }

        _closeAfterRatingWrite = true;
        await _viewModel.RatingWrites;
        _closeAfterRatingWrite = false;
        Close();
    }

    private void OnOpenSelectedPhoto(object sender, RoutedEventArgs e) => _viewModel.OpenViewer();
    private void OnCloseViewer(object sender, RoutedEventArgs e) => _viewModel.CloseViewer();
    private void OnPreviousPhoto(object sender, RoutedEventArgs e) => _viewModel.MoveViewer(-1);
    private void OnNextPhoto(object sender, RoutedEventArgs e) => _viewModel.MoveViewer(1);
    private void OnFitToWindow(object sender, RoutedEventArgs e) => PhotoSurface.FitToWindow();
    private void OnActualPixels(object sender, RoutedEventArgs e) => PhotoSurface.ShowActualPixels();
    private void OnRotatePhoto(object sender, RoutedEventArgs e) => PhotoSurface.RotateClockwise();
    private void OnSelectAll(object sender, RoutedEventArgs e) => _viewModel.SelectAll();
    private async void OnRecycleSelection(object sender, RoutedEventArgs e) => await DeleteSelectionAsync(PhotoDeletionMode.Recycle);
    private async void OnPermanentlyDeleteSelection(object sender, RoutedEventArgs e) => await DeleteSelectionAsync(PhotoDeletionMode.Permanent);

    private async Task DeleteSelectionAsync(PhotoDeletionMode mode)
    {
        var files = _viewModel.DeletionSelection();
        if (_viewModel.Folder is not { } folder || files.IsEmpty)
        {
            return;
        }

        _viewModel.BeginDeletion();
        try
        {
            var plan = await _deletions.PrepareAsync(folder, files);
            if (!plan.Files.IsEmpty && !ConfirmDeletion(plan, mode))
            {
                return;
            }

            PhotoDeletionReport report;
            if (plan.Files.IsEmpty)
            {
                report = new PhotoDeletionReport([], plan.Failures);
            }
            else
            {
                await _viewModel.ReleaseForDeletionAsync(plan.Files);
                report = await _deletions.ExecuteAsync(plan, mode, new WindowInteropHelper(this).Handle);
            }

            _viewModel.ApplyDeletionReport(report, mode);
            if (!report.Failures.IsEmpty)
            {
                var entries = report.Failures.Select(failure => new DeletionDialogEntry(failure.File.FullPath, failure.Reason)).ToArray();
                var dialog = new DeletionDialog("删除结果", $"成功 {report.Deleted.Length:N0} 个文件，失败 {entries.Length:N0} 个文件。", entries) { Owner = this };
                dialog.ShowDialog();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Photo deletion failed: {0}", exception);
            MessageBox.Show(this, $"删除操作未完成。\n{exception.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _viewModel.EndDeletion();
        }
    }

    private bool ConfirmDeletion(PhotoDeletionPlan plan, PhotoDeletionMode mode)
    {
        bool permanent = mode == PhotoDeletionMode.Permanent;
        string action = permanent ? "永久删除" : "移入回收站";
        string size = PhotoDetails.FormatSize(plan.Files.Sum(file => file.Length));
        int cr3Count = plan.Files.Count(file => PhotoFile.IsCr3(file.Extension));
        string details = cr3Count > 0 ? $"{size}，含 {cr3Count:N0} 个关联 CR3" : size;
        string files = $"{plan.Files.Length:N0} 个文件（{details}）";
        string summary = permanent
            ? $"永久删除 {files}，无法通过回收站恢复。"
            : $"将 {files} 移入 Windows 回收站。";
        if (!plan.Failures.IsEmpty)
        {
            summary += $" 有 {plan.Failures.Length:N0} 项无法处理，确认后只处理以下文件。";
        }

        var entries = plan.Files.Select(file => new DeletionDialogEntry(file.FullPath)).ToArray();
        var dialog = new DeletionDialog(action, summary, entries, action, permanent) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void OnPhotoRegionSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateViewerSize();
        QueueViewportUpdate();
    }

    private void OnGalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scroll)
        {
            _galleryScroll = scroll;
            QueueViewportUpdate();
        }
    }

    private void QueueViewportUpdate()
    {
        if (_viewportUpdatePending)
        {
            return;
        }

        _viewportUpdatePending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _viewportUpdatePending = false;
            double width = _galleryScroll is { ViewportWidth: > 0 } ? _galleryScroll.ViewportWidth : Gallery.ActualWidth;
            double height = _galleryScroll is { ViewportHeight: > 0 } ? _galleryScroll.ViewportHeight : Gallery.ActualHeight;
            _viewModel.UpdateViewport(width, height, _galleryScroll?.VerticalOffset ?? 0);
        }, DispatcherPriority.Loaded);
    }

    private void UpdateViewerSize()
    {
        double edge = Math.Max(PhotoRegion.ActualWidth, PhotoRegion.ActualHeight);
        _viewModel.ViewerLongEdge = Math.Clamp((int)Math.Ceiling(edge * VisualTreeHelper.GetDpi(this).DpiScaleX), 320, 4096);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GalleryViewModel.ViewerPhoto):
                PhotoSurface.ResetView();
                break;
            case nameof(GalleryViewModel.ViewerImage):
                PhotoSurface.SetImage(_viewModel.ViewerImage);
                break;
            case nameof(GalleryViewModel.IsViewerOpen):
                UpdateViewerSize();
                Dispatcher.InvokeAsync(FocusActiveView, DispatcherPriority.Input);
                break;
        }
    }

    private void FocusActiveView()
    {
        if (_viewModel.IsViewerOpen)
        {
            PhotoSurface.Focus();
            return;
        }

        if (_galleryScroll is { } scroll && _viewModel.SelectedRow >= 0)
        {
            double target = _viewModel.SelectedRow * GalleryLayout.TileHeight;
            if (target < scroll.VerticalOffset || target + GalleryLayout.TileHeight > scroll.VerticalOffset + scroll.ViewportHeight)
            {
                scroll.ScrollToVerticalOffset(target);
            }
        }

        Gallery.Focus();
    }

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.IsDeleting)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await ChooseFolderAsync();
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RefreshFolderAsync();
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase or ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.A && modifiers == ModifierKeys.Control && _viewModel.CanSelectAll)
        {
            _viewModel.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                await DeleteSelectionAsync(modifiers == ModifierKeys.Shift ? PhotoDeletionMode.Permanent : PhotoDeletionMode.Recycle);
            }

            return;
        }

        if (modifiers != ModifierKeys.None || Keyboard.FocusedElement is ComboBox)
        {
            return;
        }

        if (!_viewModel.IsViewerOpen)
        {
            return;
        }

        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.CloseViewer();
                break;
            case Key.Left:
                _viewModel.MoveViewer(-1);
                break;
            case Key.Right:
                _viewModel.MoveViewer(1);
                break;
            case Key.D0 or Key.NumPad0:
                PhotoSurface.FitToWindow();
                break;
            case Key.D1 or Key.NumPad1:
                PhotoSurface.ShowActualPixels();
                break;
            case Key.R:
                PhotoSurface.RotateClockwise();
                break;
            default:
                e.Handled = false;
                break;
        }
    }
}
