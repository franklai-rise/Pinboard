namespace Pinboard.App.Services.AiSessions;

public sealed record SessionProgressPresentation(string Title, string Summary, string Current, string Percentage, string Tooltip)
{
    public static SessionProgressPresentation Create(SessionBackupProgress progress, bool chinese)
    {
        string T(string en, string zh) => chinese ? zh : en;
        var target = progress.CloudTarget ? T("Google Drive", "Google Drive") : T("local storage", "本机");
        var title = progress.Total == 0 ? T("No conversations selected", "尚未选择备份对话")
            : progress.Paused ? T("Backups paused", "备份已暂停")
            : progress.IsComplete ? progress.CloudTarget ? T("Cloud backup complete", "云端备份已完成")
                : T("Local backup complete · cloud not connected", "本地备份已完成 · 尚未连接云端")
            : progress.Active > 0 || progress.Waiting > 0 ? T($"Backing up to {target}", $"正在备份到{target}")
            : T("Backup incomplete · attention needed", "备份未完成 · 仍有条目需要处理");
        var summary = T($"{progress.Completed:N0} / {progress.Total:N0} complete · {progress.Waiting:N0} waiting · {progress.AutoRetrying:N0} retrying · {progress.Warnings:N0} need review",
            $"已完成 {progress.Completed:N0} / {progress.Total:N0} 条 · 排队 {progress.Waiting:N0} · 需重试 {progress.AutoRetrying:N0} · 待检查 {progress.Warnings:N0}");
        if (progress.Blocked > 0) summary += T($" · {progress.Blocked:N0} need action", $" · 需处理 {progress.Blocked:N0}");
        var current = progress.Current is { } item && progress.Active > 0
            ? Stage(item.Stage, chinese) + " · " + item.Title + (item.TotalBytes > 0 ? $" · {Bytes(item.CompletedBytes)} / {Bytes(item.TotalBytes)}" : "")
            : progress.Paused ? T("Existing backups and the pending queue are kept.", "已有备份与未完成队列均已保留。")
            : progress.IsComplete ? T("Future changes will be checked automatically. Original files are unchanged.", "后续变化将自动检查，原始文件保持不变。")
            : progress.Problems > 0 ? T("Open View issues to inspect failures. Failed or partial items are not counted as complete.", "点击“查看问题”检查原因；失败和不完整的条目不计为完成。")
            : T("Waiting for the next conversation. Uploads continue in the background.", "等待处理下一条对话，切换页面或隐藏窗口不会停止备份。");
        var tooltip = T($"All selected conversations, independent of list filters. {progress.LocalSaved:N0} have local versions; {progress.CloudSaved:N0} have a cloud version in the current account. Overall progress counts conversations, not bytes. Completion requires confirmation and no pending work.",
            $"统计全部已勾选对话，不随列表筛选变化。本地已有版本 {progress.LocalSaved:N0} 条，当前账号云端已有版本 {progress.CloudSaved:N0} 条。总进度按对话数计算，当前传输按字节显示；只有确认保存且不再排队的条目才计为完成。");
        return new(title, summary, current, $"{progress.Percentage:0.0}%", tooltip);
    }
    public static string Stage(SessionBackupStage stage, bool chinese) => (stage, chinese) switch
    {
        (SessionBackupStage.Snapshot, true) => "正在生成快照", (SessionBackupStage.Snapshot, false) => "Creating snapshot",
        (SessionBackupStage.LocalStore, true) => "正在保存本地数据", (SessionBackupStage.LocalStore, false) => "Saving local data",
        (SessionBackupStage.Upload, true) => "正在上传", (SessionBackupStage.Upload, false) => "Uploading",
        (SessionBackupStage.Confirm, true) => "正在确认云端版本", _ => "Confirming cloud version"
    };
    private static string Bytes(long bytes)
    {
        double value = Math.Max(0, bytes); var units = new[] { "B", "KB", "MB", "GB", "TB" }; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.0} {units[unit]}";
    }
}
