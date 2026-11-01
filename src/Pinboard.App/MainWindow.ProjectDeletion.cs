using System.Windows;
using Pinboard.App.Services;
using Pinboard.App.Models;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace Pinboard.App;

public partial class MainWindow
{
    private async Task DeleteProjectAsync(string projectName)
    {
        ProjectDeletionPlan plan;
        try { plan = ProjectDeletion.Prepare(_settings.LibraryPath, projectName); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var location = plan.DirectoryPath ?? _settings.LibraryPath + "\n" + UiText("Ordinary root boards only; automatic screenshot and text boards are excluded.", "仅普通画板，不包含自动截图和文字画板。");
        var preview = string.Join("\n", plan.BoardPaths.Take(6).Select(path => "  " + Path.GetFileName(path)));
        var message = UiText(
            $"Move this project to the Recycle Bin?\n\n{LocalizationService.ProjectDisplayName(projectName)}\n{location}\n{plan.BoardPaths.Count} boards · {plan.Files.Count} files\n{preview}\n\nOther files inside this project are included. You can restore them from the Recycle Bin.",
            $"将这个项目放入回收站？\n\n{LocalizationService.ProjectDisplayName(projectName)}\n{location}\n{plan.BoardPaths.Count} 张画板 · {plan.Files.Count} 个文件\n{preview}\n\n包含项目中的其他文件，可从回收站恢复。");
        if (MessageBox.Show(this, message, UiText("Delete project", "删除项目"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await _captureRoutingGate.WaitAsync();
        await _captureGate.WaitAsync();
        var completed = false;
        try
        {
            if (_captureInbox.ReadPending().Any(job => plan.Contains(job.TargetPath)))
                throw new InvalidOperationException(UiText("This project has pending captures. Wait for them to finish, then try again.",
                    "这个项目还有待处理的收录，请完成收录后再删除。"));
            if (_activeDocument is { } active && plan.Contains(active.FilePath) && IsCanvasVisible)
                await FlushBeforeDocumentSwitchAsync();

            await _ocrQueue.RunWhilePausedAsync(() =>
            {
                ProjectDeletion.Execute(_settings.LibraryPath, plan,
                    path => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin),
                    path => FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
                return Task.CompletedTask;
            });
            completed = true;
            SetStatus(UiText("Project moved to the Recycle Bin.", "项目已放入回收站。"));
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, UiText("Could not delete the entire project", "未能完成整个项目的删除"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // A multi-file virtual project can be partly recycled by Windows.
            // Retain every surviving board's settings and clean up only removed files.
            var removed = plan.BoardPaths.Concat(_documents.Keys.Where(plan.Contains))
                .Where(path => !File.Exists(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (removed.Count > 0)
            {
                foreach (var path in removed)
                {
                    _documents.Remove(path);
                    _lastInsertedElements.Remove(path);
                    _readerStates.Remove(path);
                    _titleCache.Remove(path);
                }
                _settings.RecentFiles.RemoveAll(removed.Contains);
                if (_activeDocument is { } active && removed.Contains(active.FilePath))
                {
                    PostToCanvas("CloseDocument", new { }, active.FilePath);
                    _activeDocument = null;
                    _readerDocumentPath = null;
                    _textReader?.Clear();
                    HeaderDocumentTitle.Text = L("MainNoBoardOpen");
                    Title = "Pinboard";
                }
            }
            var targetChanged = false;
            if (_settings.FixedCaptureTarget is { } image && (removed.Contains(image) || completed && plan.Contains(image)))
            { _settings.FixedCaptureTarget = null; targetChanged = true; }
            if (_settings.FixedTextCaptureTarget is { } text && (removed.Contains(text) || completed && plan.Contains(text)))
            { _settings.FixedTextCaptureTarget = null; _settings.TextCaptureBoardMode = TextCaptureBoardMode.Daily; targetChanged = true; }
            if (completed) _settings.RecentFiles.RemoveAll(path => plan.Contains(path) && !File.Exists(path));
            if (completed || removed.Count > 0 || targetChanged) SaveWorkspacePreferences();
            _captureGate.Release();
            _captureRoutingGate.Release();
            RefreshSidebarDocuments();
            UpdateCaptureTargetText();
            UpdateTextViewToggle();
        }
    }
}
