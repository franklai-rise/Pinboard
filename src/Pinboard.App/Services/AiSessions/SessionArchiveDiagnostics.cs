using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pinboard.App.Services.AiSessions;

public sealed record CloudArchiveInventory(int Roots, int Objects, int Manifests, int DuplicateGroups, int ExtraFiles,
    long ExtraBytes, int ConflictingHashes, int InvalidMetadata);
public interface ISessionCloudInspector
{
    // Metadata only, confined to folders created by this feature. Never creates, deletes or shares files.
    Task<CloudArchiveInventory> InspectAsync(CancellationToken ct);
}
public sealed record SessionDuplicateGroup(IReadOnlyList<string> SessionKeys, int Copies);
public sealed record SessionArchiveAudit(int Conversations, int Versions, int SameTitleGroups,
    int RepeatedVersions, IReadOnlyList<SessionDuplicateGroup> IdenticalSnapshots, long SharedBytesSaved,
    int MissingManifests, int MissingObjects, int MissingAttachmentReferences, CloudArchiveInventory? Cloud);

public static class SessionArchiveDiagnostics
{
    public static async Task<SessionArchiveAudit> InspectAsync(SessionArchiveService service, ISessionCloud? cloud,
        bool includeCloud, CancellationToken ct)
    {
        var entries = service.Catalog.List(); var versions = entries.SelectMany(entry => service.Catalog.Versions(entry.Key)).ToArray();
        var contents = new List<(string Key, string Fingerprint)>(); var objects = new Dictionary<string, int>(StringComparer.Ordinal);
        long referenceBytes = 0; int missingManifests = 0, missingObjects = 0, missingAttachments = 0;
        foreach (var version in versions)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(version.ManifestPath)) { missingManifests++; continue; }
            var manifest = await SessionArchiveService.ReadManifestAsync(version.ManifestPath, ct);
            var content = JsonSerializer.Serialize(manifest.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => new { file.RelativePath, file.Length, file.Sha256 }));
            contents.Add((version.SessionKey, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
            foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks))
            {
                referenceBytes += chunk.Length;
                if (objects.TryAdd(chunk.Hash, chunk.Length)
                    && (!File.Exists(service.ObjectPath(chunk.Hash)) || new FileInfo(service.ObjectPath(chunk.Hash)).Length != chunk.Length)) missingObjects++;
            }
        }
        foreach (var entry in entries.Where(entry => entry.Status.Contains("missing attachments", StringComparison.Ordinal)))
        {
            if (service.Catalog.Versions(entry.Key).FirstOrDefault() is not { } version || !File.Exists(version.ManifestPath)) continue;
            missingAttachments += (await service.MissingAttachmentsAsync(await SessionArchiveService.ReadManifestAsync(version.ManifestPath, ct), ct)).Count;
        }
        var repeated = contents.GroupBy(item => (item.Key, item.Fingerprint)).Sum(group => Math.Max(0, group.Count() - 1));
        var identical = contents.GroupBy(item => item.Fingerprint).Where(group => group.Select(item => item.Key).Distinct().Count() > 1)
            .Select(group => new SessionDuplicateGroup(group.Select(item => item.Key).Distinct().ToArray(), group.Count())).ToArray();
        var remote = includeCloud && cloud is { IsConnected: true } && cloud is ISessionCloudInspector inspector ? await inspector.InspectAsync(ct) : null;
        return new(entries.Count, versions.Length, entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
            .GroupBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase).Count(group => group.Count() > 1), repeated, identical,
            Math.Max(0, referenceBytes - objects.Values.Sum(length => (long)length)), missingManifests, missingObjects, missingAttachments, remote);
    }
    public static string Present(SessionArchiveAudit audit, bool chinese)
    {
        string T(string en, string zh) => chinese ? zh : en;
        var lines = new List<string>
        {
            T($"Checked {audit.Conversations:N0} conversations and {audit.Versions:N0} local versions.", $"已检查 {audit.Conversations:N0} 条对话、{audit.Versions:N0} 个本地版本。"), "",
            T($"Identical extra versions in the same conversation: {audit.RepeatedVersions:N0}", $"同一对话内容完全相同的多余版本：{audit.RepeatedVersions:N0}"),
            T($"Identical snapshots across conversations: {audit.IdenticalSnapshots.Count:N0} groups", $"跨对话的相同快照：{audit.IdenticalSnapshots.Count:N0} 组"),
            T($"Same-title groups: {audit.SameTitleGroups:N0} (names alone are not duplicates)", $"同名标题：{audit.SameTitleGroups:N0} 组（同名不等于重复）"),
            T($"Shared resource deduplication saves {AiSessionsView.FormatSize(audit.SharedBytesSaved)}.", $"资源去重已节省 {AiSessionsView.FormatSize(audit.SharedBytesSaved)}。"), "",
            T($"Missing local manifests / objects: {audit.MissingManifests:N0} / {audit.MissingObjects:N0}", $"缺失的本地清单／资源：{audit.MissingManifests:N0}／{audit.MissingObjects:N0}"),
            T($"Missing image references in latest backups: {audit.MissingAttachmentReferences:N0}", $"最新备份中缺失的图片引用：{audit.MissingAttachmentReferences:N0}")
        };
        if (audit.Cloud is { } cloud)
        {
            lines.Add(""); lines.Add(T("Google Drive · read-only metadata check", "Google Drive · 只读元信息检查"));
            lines.Add(T($"Managed folders: {cloud.Roots:N0}; objects: {cloud.Objects:N0}; manifests: {cloud.Manifests:N0}", $"专用目录：{cloud.Roots:N0}；资源：{cloud.Objects:N0}；版本清单：{cloud.Manifests:N0}"));
            lines.Add(T($"Verified-metadata duplicate groups / extra files: {cloud.DuplicateGroups:N0} / {cloud.ExtraFiles:N0}", $"哈希、大小及校验字段相同的重复组／多余文件：{cloud.DuplicateGroups:N0}／{cloud.ExtraFiles:N0}"));
            lines.Add(T($"Conflicting hashes / invalid metadata: {cloud.ConflictingHashes:N0} / {cloud.InvalidMetadata:N0}", $"哈希冲突／元信息异常：{cloud.ConflictingHashes:N0}／{cloud.InvalidMetadata:N0}"));
            lines.Add(T("This is not a full download-and-restore verification.", "此检查不等同于逐文件下载恢复验证。"));
        }
        else { lines.Add(""); lines.Add(T("Cloud duplicates were not checked (not connected or unavailable).", "未检查云端重复项（未连接或当前不可用）。")); }
        lines.Add(""); lines.Add(T("Nothing was deleted, merged or deselected. Different historical versions are kept; shared chunks are normal.", "没有删除、合并或取消勾选。不同的历史版本会保留；共享资源块是正常的去重机制。"));
        return string.Join(Environment.NewLine, lines);
    }
}
