using System.Windows;
using Pinboard.App.Services;
using Pinboard.App.Services.AiSessions;

namespace Pinboard.App;

public partial class AiSessionSelectionWindow : Window
{
    public AiSessionSelectionWindow(IReadOnlyList<SessionEntry> scope, bool filtered)
    {
        InitializeComponent();
        var chinese = LocalizationService.CurrentLanguage == LocalizationService.Chinese;
        var added = scope.Count(entry => !entry.Selected);
        var children = scope.Count(entry => !entry.Selected && entry.Descriptor.ParentKey is not null);
        Title = Heading.Text = chinese ? "全选对话进行备份？" : "Select conversations for backup?";
        ScopeLabel.Text = chinese
            ? $"范围：{(filtered ? "当前筛选的匹配项" : "当前资料库中的全部对话")}（{scope.Count:N0} 条）。\n\n将新增勾选 {added:N0} 条，其中 {children:N0} 条为子对话（包括折叠项）。\n以后新发现的对话仍默认不勾选。"
            : $"Scope: {(filtered ? "matches for the current filters" : "all conversations currently in the library")} ({scope.Count:N0}).\n\nThis will select {added:N0} additional conversations, including {children:N0} child sessions (also when folded).\nNewly discovered conversations will still be unchecked.";
        WarningLabel.Text = chinese
            ? "确认后将持续备份所选对话。连接 Google Drive 后，原始记录及其中的敏感信息会上传到你的账号，不额外加密。"
            : "Confirming enables ongoing backups. When Google Drive is connected, original records, including sensitive content, are uploaded to your account without additional encryption.";
        CancelButton.Content = chinese ? "取消" : "Cancel";
        ConfirmButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = chinese ? "确认全选并备份" : "Select & back up",
            Foreground = System.Windows.Media.Brushes.White
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
