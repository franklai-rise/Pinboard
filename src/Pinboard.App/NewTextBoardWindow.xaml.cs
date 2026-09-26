using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class NewTextBoardWindow : Window
{
    private readonly string _libraryPath;

    public NewTextBoardWindow(string libraryPath, string? suggestedFolder = null)
    {
        InitializeComponent();
        _libraryPath = libraryPath;
        FolderBox.ItemsSource = TextClipsLibrary.GetManualFolders(libraryPath);
        FolderBox.Text = suggestedFolder ?? string.Empty;
        BoardTitleBox.Text = LocalizationService.T("DefaultNewTextBoardName", DateTime.Now);
        Loaded += (_, _) =>
        {
            BoardTitleBox.Focus();
            BoardTitleBox.SelectAll();
        };
    }

    public string? ResultPath { get; private set; }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var title = BoardTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ValidationText.Text = LocalizationService.T("BoardNameInvalidChars");
            ValidationText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            ResultPath = TextClipsLibrary.CreateManualBoardPath(_libraryPath, FolderBox.Text, title);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }
}
