# AI Sessions / AI 对话 — 0.8.3

## 中文使用说明

左侧 **AI 对话** 浏览本机 Codex、Claude Code、Antigravity 的对话标题。右上方搜索只检索标题，来源下拉框可筛选平台。点击标题查看实际路径、大小、更新时间和备份版本；右键修改的只是 Pinboard 内的显示名称。

每条对话前的勾选框控制持续备份。新对话默认不勾选，选择主对话不会自动选择子代理；点击箭头展开子对话后可独立勾选。勾选后即进入后台队列，以后每五分钟检查变化，也可点击 **备份所选** 立即刷新并排队。取消勾选后停止后续备份，已有版本保留。

列表上方提供 **全选 / 取消全选** 和勾选数量。没有筛选时操作当前全部对话；搜索标题或选择来源后，只操作匹配项，包含匹配但已折叠的子对话，不会选中仅作为上下文显示的父对话。全选前会确认数量、子对话范围和原始记录上传风险；取消确认不会更改任何勾选。取消全选只停止后续备份，不删除已有版本。以后新发现的对话仍默认不勾选。

资料库默认位于画板保存目录的同级 `PinboardArchives/AI Sessions`；具体完整路径可在 **AI 对话 → ⋯ → Google Drive 设置** 中查看和打开。首次创建后路径保存在本机设置中。`catalog.sqlite` 保存目录和任务状态，`Objects` 保存去重数据，`Versions` 保存版本清单，`Imports` 保存手动导入的原文件。它们独立于 `.pinboard` 画板。

状态含义：**已勾选**表示等待处理；**本地备份完成**表示本机快照已保存；**云端备份完成**表示数据和清单已经上传并通过服务端大小与校验值确认。它不等于已在目标应用中验证续聊。**等待重试**会在详情显示原因，本地已保存版本不会因上传失败被删除。

### 查看备份进度

勾选对话后，列表上方显示独立进度卡片：**整体百分比、已完成/总数、排队、需重试、待检查**。当前对话另有处理阶段和已确认传输字节的小进度条。整体按对话数量计算，不是把对话文件大小相加估算时间；列表搜索或来源筛选不会改变整体范围。

- **云端备份已完成 / 100%**：本轮全部所选对话都已获得当前 Google 账号的版本确认，且没有排队、失败或附件缺失。上传完文件但尚未确认版本时不会提前显示 100%。
- **本地备份已完成 · 尚未连接云端**：只代表本机快照已保存，不代表 Google Drive 上传完成。
- **查看问题**：只筛选需要处理的所选条目，包含折叠子对话，不修改勾选或文件。点“查看全部”返回；更多菜单的 **重试失败或不完整的备份** 只重新排队问题项。
- 可在更多菜单暂停/恢复。退出、重启后保留队列和上传续传位置；恢复后无须重新全选。已完成且未变化的对话不会在每次自动扫描时把进度归零。隐藏界面时停止进度轮询，后台备份继续。

程序不会为缺失的源文件伪造备份。选中线程的数据库导出只保留关联行，不夹带其他项目表；这是恢复资料，不是可直接替代源应用数据库的全量副本。

### 未完成的备份怎么处理

点击 **查看问题**，可进一步筛选 **原始记录缺失 / 图片缺失 / 传输与访问问题**。右侧显示具体原因和建议；**重试此条**只处理当前对话，**重试当前问题**只处理当前筛选范围，不会重置完整备份或改变勾选。

- **传输中断**：保留已确认的上传位置，按 30 秒、1 分钟、2 分钟逐步延长等待，最长约 30 分钟。右侧可看下次重试时间，也可立即重试。限流和临时服务故障按可重试错误处理。
- **需要重新连接 / 空间不足 / 权限问题**：先处理对应问题，再重试；不进行无休止的上传尝试，也不自动清理云端文件。
- **原始记录缺失**：刷新会重新查找受管理目录中的记录。若 Codex 仍有该线程的历史正文，只备份所选线程的剩余历史并明确标记为“不完整”；只有标题或空历史时不会伪装成完整备份。找回原文件后刷新，或将官方导出作为独立条目导入。缺失文件恢复前暂停该条的自动重试，不影响其他对话。
- **图片缺失**：对话记录可能已上传，但原始临时截图文件已经被清理。点击 **缺失清单** 查看对应路径；将找回的图片放回原位置后刷新，可自动补齐。反复重试不能生成已删除的图片。未变化的部分备份不会每五分钟重新制作快照。

### 重复检查

打开 **⋯ → 检查重复与备份状态**。检查同一对话的相同内容版本、跨对话相同快照、同名标题、共享资源去重，以及缺失的本地资源。连接 Drive 后只读取本功能专用目录的元信息，按哈希、大小和校验字段统计重复文件；完整读取所有分页，不扫描其他 Drive 文件。

**同名不等于重复；不同历史版本和共享资源块也不是多余对话。** 检查结果不会删除、合并、取消勾选或创建分享链接。云端元信息检查不等同于逐文件下载恢复校验；完整校验仍使用右侧的恢复功能。

### 连接 Google Drive

1. 打开 [Google Cloud](https://console.cloud.google.com/apis/dashboard)，创建项目并启用 Google Drive API。
2. 配置 OAuth 同意页面；若保持 Testing，添加你的账号为测试用户。创建 **Desktop app / 桌面应用** OAuth 客户端，下载 JSON 配置。
3. 在 **Google Drive 设置** 点击 **导入应用配置**，选择下载的 JSON，然后点击 **连接**。Pinboard 打开系统浏览器；登录、授权后回到程序，确认接收备份的账号。
4. 点击 **测试上传与恢复**。程序只使用一条明确无私人内容的示例，上传、下载并检查每个文件的哈希；通过后再勾选正式资料。测试示例会保留在导入目录，测试结束后不持续收录。

授权仅请求 `drive.file`；备份文件夹名为 `Pinboard AI Sessions Backups`，保持私有，不创建分享链接。原始记录及其中的敏感内容不额外加密。Windows 用户级保护仅用于保存 OAuth 配置与令牌；它不加密对话备份。当前聊天里的 Google Drive 插件授权不能转移给 Pinboard。

OAuth Testing 状态下的刷新令牌可能七天后失效。请按 Google Cloud 的发布要求配置应用状态；失效时在 Pinboard 重新连接。换账号需要再次确认。未连接或断网时仍可建立本地备份，上传任务在连接可用时继续。

### 恢复与导入

点击对话，在右侧选择版本，点击 **恢复**。本地恢复读取本机快照；**下载并校验云端备份**从对应账号下载清单和数据，逐文件验证哈希。选择一个恢复位置，程序创建新的子文件夹，保留原文件；第一版不自动写回源应用。

本机目录索引丢失时，连接原账号，再使用 **⋯ → 找回云端备份目录**。程序读取本功能的云端版本清单，重建索引，默认全部不勾选；随后可执行云端恢复。

Gemini 等云端聊天需要先取得官方导出文件，再通过 **⋯ → 导入官方导出文件** 添加。第一版将整个导出文件保存为一个由你命名的归档条目，不读取浏览器缓存，不抓取账号历史，也不解析对话正文。

### 边界与验证

- 标题目录不代表所有平台云端历史已被获取。来源格式变化、源文件缺失或无法取得可靠快照时显示失败，不确认完整版本。
- 只导出所选线程的共享数据库记录；添加明确引用的受管理图片和 Codex 剪贴板 PNG，内嵌图片保留在原记录中。缺失图片列入附件报告，版本显示“附件缺失”，其余记录仍可恢复。项目源码、凭据目录、Cookie 与缓存不在备份范围内。
- JSONL 只复制完整记录边界。最后一条记录超过 4 MiB 校验窗口时会提示需要格式检查并等待重试；不会假定其完整。
- 活跃数据库通过一致性快照保存，快照使用 DELETE 日志；大文件按 4 MiB 分块，原有 ZST 不重复编码。
- 关闭窗口后仍在托盘运行；完全退出才停止后台处理。暂停只影响 AI 资料库，不改变截图或文字收集。
- 本机删除不删除云端，取消勾选不清理版本。请自行管理容量；满额时暂停失败任务并提示。
- 云端验证必须在真实授权后执行。离线模拟测试通过不代表你的账号已连接。

## English guide

Open **AI Sessions** in the sidebar to browse local Codex, Claude Code, and Antigravity titles. Search filters titles; the source filter narrows the list. Details show the actual source path, size, update time, backup versions, and recovery actions. Renaming changes only the local display name.

Check individual conversations to back up future changes. New conversations stay unchecked; children are folded under their parent and selected independently. **Back up selected** refreshes discovery and queues work. The background worker checks every five minutes. Unchecking stops future work while retaining existing versions.

**Select all / Deselect all** above the list apply to all current conversations, or only title/source matches when filtered. Matching folded children are included; parents shown only for context are not. A confirmation explains the additional selection count, child sessions, and unencrypted upload before enabling bulk backup. Cancelling makes no changes; deselecting retains existing versions. Newly discovered sessions remain unchecked.

The progress card shows overall percentage, confirmed conversations, queue, retries, review items, and the active conversation's stage and acknowledged transfer bytes. Overall progress counts conversations, not bytes, and ignores list filters. **Cloud backup complete / 100%** requires the latest versions to be confirmed for the current account, with no outstanding work or incomplete attachments. Local-only completion is labelled separately. **View issues** reveals problematic folded children without changing selection; the More menu can retry only problem entries. Queues and resumable upload positions survive restart. Automatic checks preserve completed progress for unchanged conversations; hidden views stop UI polling while backups continue. Selected-row database exports exclude unrelated foreign tables and are not full source-application database replacements.

The independent archive defaults to `PinboardArchives/AI Sessions` beside the board library. The full path is shown in **AI Sessions → ⋯ → Google Drive settings**. The catalogue, content-addressed objects, version manifests, and imported exports are kept outside `.pinboard` files.

**View issues** now separates missing recordings, missing images, and transfer/access failures. Details explain the cause and next automatic retry; retry one entry or only the current issue filter without requeuing completed backups. Temporary failures use bounded exponential backoff. Authorization, storage, permissions and missing sources wait for user action or a recovered source. Missing Codex recordings can preserve selected database history only when actual history rows remain; title-only metadata is never presented as a recovered conversation. Partial versions remain visibly incomplete. Unchanged partial snapshots are not rebuilt every five minutes; restored referenced images are detected on refresh.

Use **More → Check duplicates and backup health** for a non-destructive report. It separates identical version content, cross-session identical snapshots, same-title matches, and normal shared-chunk deduplication. Optional Drive checks read all metadata pages inside this feature's managed folders only, grouping by hash, size and checksum. They never delete, merge, deselect, share or modify remote files. A metadata inventory is not a full download-and-restore verification.

For Google Drive, create your own Cloud project, enable Drive API, configure OAuth consent, and download a **Desktop app** client configuration. Import it in the connection window, sign in through the system browser, and confirm the destination account. Run **Test backup & restore** with the supplied non-sensitive sample before selecting private records. Testing-mode refresh tokens can expire after seven days. The connection uses PKCE and `drive.file`; credentials are protected for the current Windows user outside the archive.

Backups go to the private `Pinboard AI Sessions Backups` folder without additional encryption or sharing links. No source deletion is propagated. Local snapshots survive upload failures; resumable uploads and queues survive restart. The browser canvas remains restricted to local app resources.

Select a version and restore into a new directory. Cloud recovery downloads both the manifest and objects and verifies SHA-256. **Recover cloud backup index** rebuilds a lost catalogue from this feature's cloud manifests, with selection disabled by default. Recovery does not automatically replace source-app files or promise that the original application can resume a restored session.

Official Gemini or other cloud exports can be imported as a single named raw archive entry. There is no transcript reader, body search, browser-cache scraping, or AI summarization in this version. Source schema changes, incomplete JSONL boundaries, or unavailable files are reported. JSONL final records larger than the 4 MiB validation window require review. SQLite snapshots are consistent; only selected thread rows and explicitly referenced managed images or Codex clipboard PNGs are included. Missing images are recorded in an attachment report and the version is labelled incomplete while retaining the remaining records.

## Implementation verification

Automated coverage includes selection persistence, independent child selection, source title discovery, live WAL snapshots, selected-row privacy boundaries, chunk reuse, hash-checked restoration, cloud-index recovery, quota/authentication errors, interrupted upload resume, and protected token storage. The desktop harness uses synthetic content and checks a virtualized 10,000-title list, title filtering, both languages, compact layout, canvas navigation, and tray behavior.

Live Google Drive account authorization and a real upload/download test remain a separate user-authorized step. Local deployment does not publish a GitHub release.

Official references: [desktop authorization](https://developers.google.com/identity/protocols/oauth2/native-app), [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads), [OAuth lifecycle](https://developers.google.com/identity/protocols/oauth2/).
