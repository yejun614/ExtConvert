using Avalonia.Controls;
using Avalonia.Interactivity;
using ExtConvert.Media;

namespace ExtConvert;

public partial class Mp3OptionsWindow : Window
{
    public Mp3OptionsWindow()
    {
        InitializeComponent();

        BitrateBox.ItemsSource = Mp3Exporter.Bitrates.Select(b => $"{b} kbps").ToArray();
        BitrateBox.SelectedIndex = Array.IndexOf(Mp3Exporter.Bitrates, new MediaExportOptions().AudioBitrateKbps);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) =>
        Close(new MediaExportOptions { AudioBitrateKbps = Mp3Exporter.Bitrates[BitrateBox.SelectedIndex] });

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
