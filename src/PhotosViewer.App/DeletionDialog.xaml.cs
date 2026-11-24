using System.Windows;

namespace PhotosViewer.App;

internal sealed record DeletionDialogEntry(string Path, string? Reason = null);

public partial class DeletionDialog : Window
{
    internal DeletionDialog(string title, string summary, IReadOnlyList<DeletionDialogEntry> entries, string? action = null,
        bool permanent = false)
    {
        InitializeComponent();
        Title = title;
        SummaryText.Text = summary;
        FileList.ItemsSource = entries;
        if (action is null)
        {
            CancelAction.Content = "确定";
            ConfirmAction.Visibility = Visibility.Collapsed;
        }
        else
        {
            ConfirmAction.Content = action;
        }

        if (permanent)
        {
            SummaryText.SetResourceReference(ForegroundProperty, "DangerBrush");
            ConfirmAction.SetResourceReference(ForegroundProperty, "DangerBrush");
        }

        Loaded += (_, _) => CancelAction.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
