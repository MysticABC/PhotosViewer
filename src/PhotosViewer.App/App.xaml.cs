using System.IO;
using System.Windows;
using PhotosViewer.App.ViewModels;
using PhotosViewer.Imaging.Wpf;
using PhotosViewer.Infrastructure;

namespace PhotosViewer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ratings = new PhotoRatingStore(Path.Combine(appData, "PhotosViewer", "ratings.json"));
        var gallery = new GalleryViewModel(new PhotoDirectory(), new JpegDecoder(), ratings);
        MainWindow = new MainWindow(gallery);
        MainWindow.Show();
    }
}
