namespace Pinboard.App.Services.AiSessions;

public enum SessionIssueFilter { All, Source, Attachments, Transfer }
public static class SessionIssuePresentation
{
    public static bool Matches(SessionEntry entry, SessionIssueFilter filter) => SessionSelection.HasIssue(entry) && filter switch
    {
        SessionIssueFilter.Source => entry.Descriptor.Availability == "Missing source" || entry.Status.Contains("missing source", StringComparison.Ordinal) || entry.Status == "Needs source",
        SessionIssueFilter.Attachments => entry.Status.Contains("missing attachments", StringComparison.Ordinal),
        SessionIssueFilter.Transfer => entry.Error is not null && entry.Status != "Needs source" && entry.Descriptor.Availability != "Missing source",
        _ => true
    };
    public static string Help(SessionEntry entry, SessionJobInfo? job, bool chinese)
    {
        string T(string en, string zh) => chinese ? zh : en;
        if (entry.Status.Contains("missing source", StringComparison.Ordinal))
            return T("Available selected history is saved, but the original recording is missing. This is a partial backup, not a complete conversation. Restore the original recording and refresh to complete it.",
                "仍可读取的所选历史已保存，但原始记录文件缺失。这是不完整备份。找回原始记录后刷新，才能补齐；不会覆盖旧版本。");
        if (entry.Status.Contains("missing attachments", StringComparison.Ordinal))
            return T("Conversation text is saved; some image files no longer exist. Retrying cannot recreate deleted temporary images. View the missing list, restore files to those paths, then refresh; recovered images are picked up automatically.",
                "对话记录已保存，部分图片文件已不存在。重试不能重新生成已删除的临时截图。查看缺失清单，将找回的文件放回对应位置再刷新，程序会自动补齐。");
        if (entry.Descriptor.Availability == "Missing source" || entry.Status == "Needs source")
            return T("The original recording is unavailable and no usable selected history could be saved. Restore the original file, or import an official export as a separate archive. Existing versions are kept. Automatic retries wait for the source to change.",
                "原始记录不可用，也没有可读取的所选历史。请找回原文件，或将官方导出文件单独导入归档。已有版本保留；自动重试会等待源文件恢复。");
        if (job is { Blocked: true }) return job.FailureKind switch
        {
            SessionFailureKind.Authorization => T("Reconnect Google Drive in settings, then retry. Local data is kept.", "在 Google Drive 设置中重新连接，再重试。本地数据保留。"),
            SessionFailureKind.Quota => T("Free up Drive storage or choose another account, then retry. Nothing is automatically deleted.", "腾出 Drive 空间或连接其他账号，再重试。程序不会自动删文件。"),
            SessionFailureKind.Permission => T("Check access permissions to the source and Google Drive, then retry.", "检查源文件及 Google Drive 的访问权限，再重试。"),
            _ => T("This needs review, not repeated uploads. Check the error below; source files and existing backups are unchanged.", "此问题需要检查，反复上传不能解决。请查看下方错误；源文件和已有备份均未改动。")
        };
        if (job is { Attempts: > 0 }) return T($"Automatically retries at {job.DueAt.ToLocalTime():MM-dd HH:mm:ss} (attempts: {job.Attempts}). You can retry this item now; uploaded chunks are reused.",
            $"下次自动重试：{job.DueAt.ToLocalTime():MM-dd HH:mm:ss}（已尝试 {job.Attempts} 次）。也可立即重试这一条，已上传的资源块会复用。");
        return "";
    }
}
