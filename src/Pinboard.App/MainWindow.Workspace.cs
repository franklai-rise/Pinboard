using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Pinboard.App.Models;
using Pinboard.App.Services;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Pinboard.App;

public partial class MainWindow
{
    private bool _focusMode;
    private bool _workspaceReady;
    private DateOnly _sidebarDate = DateOnly.FromDateTime(DateTime.Now);
    private readonly DispatcherTimer _dateRolloverTimer = new();

    private void InitializeWorkspacePresentation()
    {
        _workspaceReady = true;
        _dateRolloverTimer.Tick += (_, _) => RefreshCalendarPresentation();
        Activated += (_, _) => RefreshCalendarPresentation();
        ScheduleCalendarRefresh();
        UpdateFocusButton();
    }

    private void ScheduleCalendarRefresh()
    {
        _dateRolloverTimer.Stop();
        _dateRolloverTimer.Interval = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromMilliseconds(200);
        _dateRolloverTimer.Start();
    }

    private void RefreshCalendarPresentation()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        if (date != _sidebarDate)
        {
            _sidebarDate = date;
            RequestSidebarRefresh();
            UpdateCaptureTargetText();
        }
        ScheduleCalendarRefresh();
    }

    private void WorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspaceReady) ApplySidebarState();
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.SidebarWidth = WorkspacePresentation.SidebarWidth(SidebarColumn.ActualWidth, WorkspaceGrid.ActualWidth);
        SaveWorkspacePreferences();
    }

    private void SidebarSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        _settings.SidebarWidth = WorkspacePresentation.DefaultSidebarWidth;
        ApplySidebarState();
        SaveWorkspacePreferences();
        e.Handled = true;
    }

    private void SaveWorkspacePreferences()
    {
        try { _settings.Save(); }
        catch (Exception error)
        {
            DiagnosticLog.Write("workspace-preferences", error);
            SetStatus(UiText("Could not save layout preferences.", "无法保存布局设置。"));
        }
    }

    private async void FocusModeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_focusMode && IsCanvasVisible) await FlushBeforeDocumentSwitchAsync();
            SetFocusMode(!_focusMode);
        }
        catch (Exception error)
        {
            SetStatus(error.Message);
        }
    }

    private void SetFocusMode(bool enabled)
    {
        _focusMode = enabled;
        _textReader?.SetFocusMode(enabled);
        PostToCanvas("SetFocusMode", new { enabled }, _activeDocument?.FilePath);
        UpdateFocusButton();
    }

    private void UpdateFocusButton()
    {
        FocusModeButton.Visibility = !_showAiSessions && _activeDocument is not null ? Visibility.Visible : Visibility.Collapsed;
        FocusModeButton.Content = _focusMode ? UiText("Exit focus", "退出纯净") : UiText("Focus", "纯净模式");
        FocusModeButton.ToolTip = _focusMode ? UiText("Restore tools (Esc)", "恢复工具（Esc）")
            : UiText("Hide tools and read without distractions", "隐藏工具，专注浏览内容");
        System.Windows.Automation.AutomationProperties.SetName(FocusModeButton,
            _focusMode ? "Exit focus mode" : "Enter focus mode");
    }

    private void TextModuleAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        var board = new MenuItem { Header = L("TextNewBoardHeading") };
        board.Click += NewTextBoardButton_Click;
        var folder = new MenuItem { Header = L("TextNewFolder") };
        folder.Click += NewTextFolderButton_Click;
        menu.Items.Add(board);
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void SidebarGroup_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Expander { DataContext: CollectionViewGroup group } expander
            && e.GetPosition(expander).Y <= 34 && group.Items[0] is SidebarDocument { CanDeleteProject: true } item)
        {
            ShowProjectMenu(expander, item.ProjectName);
            e.Handled = true;
        }
    }

    private void ProjectActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string projectName } button) ShowProjectMenu(button, projectName);
        e.Handled = true;
    }

    private void ShowProjectMenu(FrameworkElement anchor, string projectName)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var delete = new MenuItem { Header = UiText("Delete project…", "删除项目…") };
        delete.Click += async (_, _) => await DeleteProjectAsync(projectName);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }
}
