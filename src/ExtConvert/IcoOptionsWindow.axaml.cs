using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ExtConvert.Conversion;

namespace ExtConvert;

public partial class IcoOptionsWindow : Window
{
    private readonly List<(int Size, CheckBox Box)> _boxes = [];

    // XAML 디자이너용
    public IcoOptionsWindow() : this(new PixelSize(256, 256)) { }

    public IcoOptionsWindow(PixelSize sourceSize)
    {
        InitializeComponent();

        foreach (var size in IcoExporter.AllSizes)
        {
            var box = new CheckBox
            {
                Content = $"{size}×{size}",
                IsChecked = IcoExporter.DefaultSizes.Contains(size),
                Margin = new Thickness(0, 0, 12, 0),
                MinWidth = 88,
            };
            _boxes.Add((size, box));
            SizePanel.Children.Add(box);
        }

        var maxSide = Math.Max(sourceSize.Width, sourceSize.Height);
        HintText.Text = $"원본 크기: {sourceSize.Width}×{sourceSize.Height}"
            + (maxSide < 256 ? "\n원본보다 큰 크기는 확대되어 흐려질 수 있습니다." : "")
            + (sourceSize.Width != sourceSize.Height ? "\n정사각형이 아니므로 투명 여백이 추가됩니다." : "");
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var sizes = _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Size).ToArray();
        if (sizes.Length == 0)
        {
            HintText.Text = "크기를 하나 이상 선택하세요.";
            return;
        }
        Close(sizes);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
