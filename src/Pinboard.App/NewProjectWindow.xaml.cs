using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class NewProjectWindow : Window
{
    private readonly ProjectLibraryService _projectLibrary;

    public NewProjectWindow(ProjectLibraryService projectLibrary)
    {
        InitializeComponent();
        _projectLibrary = projectLibrary;
        ProjectNameBox.Text = LocalizationService.T("DefaultNewProjectName");
        Loaded += (_, _) =>
        {
            ProjectNameBox.Focus();
            ProjectNameBox.SelectAll();
        };
    }

    public string? ResultProjectName { get; private set; }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultProjectName = _projectLibrary.CreateProject(ProjectNameBox.Text);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }
}
