using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotosViewer.App;
using PhotosViewer.App.ViewModels;
using PhotosViewer.Core;
using PhotosViewer.Imaging.Wpf;
using PhotosViewer.Infrastructure;
using Xunit;

namespace PhotosViewer.Integration.Tests;

[CollectionDefinition("WPF window", DisableParallelization = true)]
public sealed class WpfWindowCollection
{
}

[Collection("WPF window")]
public sealed class GalleryWindowTests
{
    [Fact]
    public async Task VirtualizesTheGalleryAndSupportsRatingTheCurrentPhoto()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await ExerciseGalleryAsync();
                    completed.SetResult();
                }
                catch (Exception exception)
                {
                    completed.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });

            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task ExerciseGalleryAsync()
    {
        using var directory = new TemporaryPhotoDirectory();
        var sample = JpegDecoderTests.CreatePhoto(directory.FilePath("IMG_0000.JPG"), 1);
        byte[] bytes = File.ReadAllBytes(sample.FullPath);
        for (int index = 1; index < 800; index++)
        {
            string path = directory.FilePath($"IMG_{index:D4}.JPG");
            if (index == 3)
            {
                JpegDecoderTests.CreatePhoto(path, 6, 1920, 960);
            }
            else if (index == 799)
            {
                JpegDecoderTests.CreatePhoto(path, 1, simpleRating: 4);
            }
            else
            {
                File.WriteAllBytes(path, bytes);
            }
        }

        File.WriteAllBytes(directory.FilePath("破损 样片.jpg"), []);
        File.WriteAllBytes(directory.FilePath("img_0000.CR3"), [1, 2, 3, 4]);
        File.WriteAllBytes(directory.FilePath("IMG_0006.cr3"), [1, 2, 3, 4]);
        File.WriteAllBytes(directory.FilePath("IMG_0010.CR3"), [1, 2, 3, 4]);

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PhotosViewer;component/WorkspaceTheme.xaml", UriKind.Relative)
        });
        string ratingsPath = directory.FilePath("ratings.json");
        var viewModel = new GalleryViewModel(new PhotoDirectory(), new JpegDecoder(), new PhotoRatingStore(ratingsPath));
        var window = new MainWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30_000,
            Top = -30_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        try
        {
            window.Show();
            await viewModel.OpenFolderAsync(directory.Path);
            await WaitForAsync(() => viewModel.Rows.Length > 50 && viewModel.Rows[0].Items[0].Thumbnail is not null);

            var gallery = Assert.IsType<ItemsControl>(window.FindName("Gallery"));
            var scroll = Assert.IsType<ScrollViewer>(gallery.Template.FindName("PART_ScrollViewer", gallery));
            var panel = Assert.IsType<VirtualizingStackPanel>(FindDescendant<VirtualizingStackPanel>(gallery));
            Assert.InRange(panel.Children.Count, 1, viewModel.Rows.Length / 4);
            var tileRating = Assert.IsType<RatingEditor>(FindDescendant<RatingEditor>(gallery));
            RequestRating(tileRating, 5);
            await viewModel.RatingWrites;
            await WaitForAsync(() => tileRating.Value == 5);
            Assert.False(viewModel.IsViewerOpen);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "gallery.png");

            int initialRows = viewModel.Rows.Length;
            window.Width = 800;
            await WaitForAsync(() => viewModel.Rows.Length > initialRows);
            scroll.ScrollToVerticalOffset(50 * GalleryLayout.TileHeight);
            await WaitForAsync(() => scroll.VerticalOffset >= 50 * GalleryLayout.TileHeight &&
                viewModel.Rows[50].Items[0].Thumbnail is not null);
            Assert.InRange(panel.Children.Count, 1, viewModel.Rows.Length / 4);

            viewModel.OpenViewer();
            viewModel.MoveViewer(1);
            viewModel.MoveViewer(1);
            viewModel.MoveViewer(1);
            await WaitForAsync(() => viewModel.ViewerStatus is null && viewModel.ViewerImage?.Metadata.Orientation == 6);
            Assert.Equal("IMG_0003.JPG", viewModel.ViewerPhoto?.File.Name);
            Assert.InRange(viewModel.ViewerImage!.Bitmap.PixelHeight, 1, 1919);

            var surface = Assert.IsType<PhotoViewport>(window.FindName("PhotoSurface"));
            surface.ShowActualPixels();
            await WaitForAsync(() => viewModel.ViewerImage?.Bitmap.PixelHeight == 1920);
            surface.FitToWindow();
            Assert.NotEqual(1d.ToString("P0"), surface.ZoomLabel);
            var viewerRating = Assert.IsType<RatingEditor>(window.FindName("ViewerRating"));
            RequestRating(viewerRating, 3);
            await viewModel.RatingWrites;
            await WaitForAsync(() => viewerRating.Value == 3);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "viewer.png");
            viewModel.CloseViewer();
            Assert.Null(viewModel.ViewerImage);

            var ratingFilter = Assert.IsType<ComboBox>(window.FindName("RatingFilter"));
            ratingFilter.SelectedItem = viewModel.RatingOptions.Single(option => option.Value == 4);
            await WaitForAsync(() => viewModel.SelectedPhoto?.File.Name == "IMG_0799.JPG" && viewModel.Activity is null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var ratedTile = Assert.IsType<RatingEditor>(FindDescendant<RatingEditor>(gallery));
            Assert.Equal(4, ratedTile.Value);
            RequestRating(ratedTile, PhotoRating.Unrated);
            await viewModel.RatingWrites;
            Assert.False(viewModel.HasPhotos);

            await viewModel.OpenFolderAsync(directory.Path);
            await WaitForAsync(() => viewModel.Activity is null);
            Assert.False(viewModel.HasPhotos);
            var saved = await new PhotoRatingStore(ratingsPath).ReadAsync(CancellationToken.None);
            Assert.Equal(5, saved[sample.FullPath].Rating);
            Assert.Equal(3, saved[Path.Combine(directory.Path, "IMG_0003.JPG")].Rating);
            Assert.Equal(PhotoRating.Unrated, saved[Path.Combine(directory.Path, "IMG_0799.JPG")].Rating);
            Assert.Equal(4, (await new JpegDecoder().ReadCatalogMetadataAsync(
                new PhotoFile(Path.Combine(directory.Path, "IMG_0799.JPG"), 0, DateTime.UnixEpoch, DateTime.UnixEpoch),
                CancellationToken.None)).Rating);
            ratingFilter.SelectedItem = viewModel.RatingOptions[0];

            viewModel.NameFilter = "破损";
            await WaitForAsync(() => viewModel.SelectedPhoto?.ThumbnailFailed == true);
            viewModel.OpenViewer();
            await WaitForAsync(() => viewModel.ViewerStatus == "无法解码此照片");
            Assert.Null(viewModel.ViewerImage);
            viewModel.CloseViewer();

            await ExerciseBatchDeletionAsync(application, window, viewModel, directory.Path);
        }
        finally
        {
            window.Close();
            application.Shutdown();
        }
    }

    private static async Task ExerciseBatchDeletionAsync(Application application, MainWindow window, GalleryViewModel viewModel, string folder)
    {
        viewModel.NameFilter = "IMG_000";
        var photos = viewModel.Rows.SelectMany(row => row.Items).ToArray();
        Assert.Equal(10, photos.Length);
        viewModel.SelectPhoto(photos[1]);
        viewModel.SelectPhoto(photos[3], extend: true, toggle: false);
        Assert.Equal(photos[1..4].Select(photo => photo.File), viewModel.DeletionSelection());
        viewModel.SelectPhoto(photos[2], extend: false, toggle: true);
        Assert.Equal(new[] { photos[1].File, photos[3].File }, viewModel.DeletionSelection());

        var selectAll = Assert.IsType<Button>(window.FindName("SelectAllAction"));
        selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var selectedPaths = photos.Select(photo => photo.File.FullPath).ToArray();
        Assert.Equal(selectedPaths, viewModel.DeletionSelection().Select(file => file.FullPath));
        viewModel.NameFilter = "IMG_0000";
        Assert.Equal(selectedPaths[0], Assert.Single(viewModel.DeletionSelection()).FullPath);
        viewModel.NameFilter = "IMG_000";
        Assert.Single(viewModel.DeletionSelection());
        selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => photos.All(photo => photo.Thumbnail is not null));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture(window, "batch-selection.png");

        viewModel.OpenViewer();
        Assert.Single(viewModel.DeletionSelection());
        viewModel.CloseViewer();
        Assert.Equal(photos.Length, viewModel.DeletionSelection().Length);

        var delete = Assert.IsType<Button>(window.FindName("PermanentDeleteAction"));
        var expectedFiles = new List<string>(selectedPaths);
        expectedFiles.Insert(7, Path.Combine(folder, "IMG_0006.cr3"));
        expectedFiles.Insert(1, Path.Combine(folder, "img_0000.CR3"));
        await RespondToDeletionAsync(application, delete, viewModel, expectedFiles.ToArray(), confirm: false);
        Assert.All(expectedFiles, path => Assert.True(File.Exists(path)));
        Assert.Equal(photos.Length, viewModel.DeletionSelection().Length);

        await RespondToDeletionAsync(application, delete, viewModel, expectedFiles.ToArray(), confirm: true);
        Assert.All(expectedFiles, path => Assert.False(File.Exists(path)));
        Assert.False(viewModel.HasPhotos);
        Assert.True(File.Exists(Path.Combine(folder, "IMG_0010.JPG")));
        Assert.True(File.Exists(Path.Combine(folder, "IMG_0010.CR3")));
        Assert.True(File.Exists(Path.Combine(folder, "IMG_0799.JPG")));
    }

    private static async Task RespondToDeletionAsync(Application application, Button delete, GalleryViewModel viewModel,
        string[] paths, bool confirm)
    {
        Exception? failure = null;
        bool observed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var dialog = application.Windows.OfType<DeletionDialog>().FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog is null)
            {
                return;
            }

            timer.Stop();
            observed = true;
            try
            {
                var list = Assert.IsType<ListBox>(dialog.FindName("FileList"));
                Assert.Equal(paths, list.Items.OfType<DeletionDialogEntry>().Select(entry => entry.Path));
                Assert.Contains("含 2 个关联 CR3", Assert.IsType<TextBlock>(dialog.FindName("SummaryText")).Text);
                Assert.True(Assert.IsType<Button>(dialog.FindName("CancelAction")).IsDefault);
                Assert.False(Assert.IsType<Button>(dialog.FindName("ConfirmAction")).IsDefault);
                Capture(dialog, "delete-confirmation.png");
                dialog.DialogResult = confirm;
            }
            catch (Exception exception)
            {
                failure = exception;
                dialog.DialogResult = false;
            }
        };

        try
        {
            timer.Start();
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => observed && !viewModel.IsDeleting);
            Assert.Null(failure);
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void RequestRating(RatingEditor editor, int value) => editor.Children.OfType<Button>().Single(button => (int)button.Tag == value)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            var match = child as T ?? FindDescendant<T>(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "The WPF view did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PHOTOSVIEWER_QA_CAPTURE_DIRECTORY");
        if (directory is null)
        {
            return;
        }

        var layout = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)layout.ActualWidth, (int)layout.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(layout);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
