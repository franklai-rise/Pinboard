using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class NewBoardWindow : Window
{
    private readonly ProjectLibraryService _projectLibrary;
    private readonly Dictionary<string, string> _displayToProject = new(StringComparer.CurrentCultureIgnoreCase);

    public NewBoardWindow(ProjectLibraryService projectLibrary, string? suggestedProject)
    {
        InitializeComponent();
        _projectLibrary = projectLibrary;

        var availableProjects = projectLibrary.GetProjectNames();
        foreach (var project in availableProjects)
        {
            _displayToProject[LocalizationService.ProjectDisplayName(project)] = project;
        }
        ProjectBox.ItemsSource = availableProjects.Select(LocalizationService.ProjectDisplayName).ToList();
        var selectedProject = availableProjects.FirstOrDefault(project => project.Equals(suggestedProject, StringComparison.CurrentCultureIgnoreCase))
            ?? ProjectLibraryService.DefaultProjectName;
        ProjectBox.Text = LocalizationService.ProjectDisplayName(selectedProject);
        BoardTitleBox.Text = LocalizationService.T("DefaultNewBoardName", DateTime.Now);

        Loaded += (_, _) =>
        {
            BoardTitleBox.Focus();
            BoardTitleBox.SelectAll();
        };
    }

    public string? ResultPath { get; private set; }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var projectText = ProjectBox.Text.Trim();
            var projectName = _displayToProject.TryGetValue(projectText, out var internalName) ? internalName : projectText;
            ResultPath = _projectLibrary.CreateBoardPath(BoardTitleBox.Text, projectName);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

}
