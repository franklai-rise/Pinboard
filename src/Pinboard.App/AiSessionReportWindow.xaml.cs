using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class AiSessionReportWindow : Window
{
    public AiSessionReportWindow(string title, string report)
    {
        InitializeComponent(); Title = Heading.Text = title; Report.Text = report;
        var chinese = LocalizationService.CurrentLanguage == LocalizationService.Chinese;
        CopyButton.Content = chinese ? "复制报告" : "Copy report";
        CloseButton.Content = new System.Windows.Controls.TextBlock { Text = chinese ? "完成" : "Done", Foreground = System.Windows.Media.Brushes.White };
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardCaptureService.SetTextWithoutCapture(Report.Text);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
