using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhotosViewer.Core;

namespace PhotosViewer.App;

public sealed class RatingRequestedEventArgs(int value) : EventArgs
{
    public int Value { get; } = value;
}

public sealed class RatingEditor : StackPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int?),
        typeof(RatingEditor), new PropertyMetadata(null, (element, _) => ((RatingEditor)element).UpdateButtons()));

    private readonly Button[] _stars = new Button[PhotoRating.Highest];
    private readonly Button _clear;

    public RatingEditor()
    {
        Orientation = Orientation.Horizontal;
        for (int index = 0; index < _stars.Length; index++)
        {
            int value = index + 1;
            _stars[index] = AddButton(value, $"设为 {value} 星");
        }

        _clear = AddButton(PhotoRating.Unrated, "清除评分");
        UpdateButtons();
    }

    public int? Value
    {
        get => (int?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public event EventHandler<RatingRequestedEventArgs>? RatingRequested;

    private Button AddButton(int value, string label)
    {
        var button = new Button { Tag = value, ToolTip = label };
        button.SetResourceReference(StyleProperty, "RatingButton");
        AutomationProperties.SetName(button, label);
        button.Click += OnRatingClick;
        Children.Add(button);
        return button;
    }

    private void OnRatingClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int value })
        {
            RatingRequested?.Invoke(this, new RatingRequestedEventArgs(value));
        }
    }

    private void UpdateButtons()
    {
        int rating = Value.GetValueOrDefault();
        for (int index = 0; index < _stars.Length; index++)
        {
            bool filled = index < rating;
            _stars[index].Content = filled ? "★" : "☆";
            _stars[index].SetResourceReference(Control.ForegroundProperty, filled ? "RatingBrush" : "MutedBrush");
        }

        bool rejected = rating == PhotoRating.Rejected;
        _clear.Visibility = rating == PhotoRating.Unrated ? Visibility.Collapsed : Visibility.Visible;
        _clear.Content = rejected ? "拒" : "×";
        _clear.ToolTip = rejected ? "已拒绝，点击清除评分" : "清除评分";
        _clear.SetResourceReference(Control.ForegroundProperty, rejected ? "RatingBrush" : "MutedBrush");
    }
}
