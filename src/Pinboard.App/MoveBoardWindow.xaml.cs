using System.Windows;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class MoveBoardWindow : Window
{
    private readonly Dictionary<string, string> _displayToProject = new(StringComparer.CurrentCultureIgnoreCase);

    public MoveBoardWindow(string boardTitle, string currentProject, IEnumerable<string> destinationProjects)
    {
        InitializeComponent();
        BoardTitleText.Text = boardTitle;
        CurrentProjectText.Text = LocalizationService.T("CurrentProjectFormat", LocalizationService.ProjectDisplayName(currentProject));
        foreach (var project in destinationProjects)
        {
            _displayToProject[LocalizationService.ProjectDisplayName(project)] = project;
        }
        ProjectBox.ItemsSource = _displayToProject.Keys.ToList();
        ProjectBox.SelectedIndex = _displayToProject.Count > 0 ? 0 : -1;
    }

    public string? ResultProjectName { get; private set; }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectBox.SelectedItem is not string projectName)
        {
            return;
        }
        ResultProjectName = _displayToProject[projectName];
        DialogResult = true;
    }
}
