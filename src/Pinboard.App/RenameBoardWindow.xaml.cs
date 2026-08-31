using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class RenameBoardWindow : Window
{
    public RenameBoardWindow(string currentDisplayName, string actualPath)
    {
        InitializeComponent();
        DisplayNameBox.Text = currentDisplayName;
        ActualPathText.Text = actualPath;
        Loaded += (_, _) =>
        {
            DisplayNameBox.Focus();
            DisplayNameBox.SelectAll();
        };
    }

    public string? ResultDisplayName { get; private set; }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var value = DisplayNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowValidation(LocalizationService.T("RenameBoardEmpty"));
            return;
        }
        if (value.Length > 120)
        {
            ShowValidation(LocalizationService.T("RenameBoardTooLong"));
            return;
        }

        ResultDisplayName = value;
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
