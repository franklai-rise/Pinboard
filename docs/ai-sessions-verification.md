# AI Sessions 0.8.3 verification

This update was developed and verified locally. No conversations were selected or deselected by diagnostics; cloud inspection is metadata-only and does not create, delete, share or merge remote files. No GitHub commit, push or release is part of this update.

| Repair update check | Result |
| --- | --- |
| .NET tests | 155 passed, including 58 AI archive/source/selection/progress/repair cases |
| Desktop harness | 87 checks passed with isolated synthetic content; recovery guidance, scoped retry, issue categories, multiline diagnostics, compact layout and both languages verified |
| Frontend and website | 20 frontend and 3 website tests passed; static-site checks passed |
| Regression coverage | New tests cover schema-1 queue migration, persisted blocked jobs, bounded retries, unchanged partial snapshots, restored sources/images, scoped database salvage and non-destructive duplicate reporting |
| Missing source | Only selected database history with actual remaining rows can be saved as an explicitly incomplete version; title-only metadata cannot create a confirmed conversation backup |
| Selected-row isolation | Partial source recovery exports no unselected thread rows, preserves the original source databases and passes restore/integrity checks |
| Retry behavior | Transient failures retry with a bounded exponential delay; missing source, authorization, permission, storage and format issues wait for repair or explicit action; other tasks continue |
| Avoid repeated work | Unchanged partial snapshots remain visible but are not rebuilt every scan; ordinary Back up selected does not reset successful progress |
| Source recovery | A recovered original file unblocks its task; restored referenced images trigger a new snapshot; unrelated retry deadlines are kept |
| Duplicate definitions | Full file-set fingerprints distinguish identical versions and cross-session snapshots from same-title matches and shared chunks; diagnostics do not delete anything |
| Cloud inventory | GET-only Drive requests confined to this feature's managed folders; pagination and repeated-page protection, same-file deduplication, conflicting hashes and incomplete inventory errors covered |
| Real metadata inventory | Read-only inspection of the already-authorized destination completed. No duplicate content or conflicting metadata was found at inspection time. This is not a full download/restore verification |
| Real source diagnosis | Already-selected missing-source cases were checked in a separate local catalogue with no cloud client. They were classified as blocked missing sources with zero fabricated versions; the production catalogue and selection were not modified |
| UI | Focused source/image/transfer filters, filtered and per-entry retry, next retry time, recovery guidance and a readable multiline report are verified with isolated synthetic content |
| Deployment | Installed 0.8.3 after the user's normal tray exit, with full program/profile/board/archive backup. Restarted hidden in the background; executable hash matches the verified portable build |
| Data preservation | All 52 existing board hashes, settings and protected authorization files retained. No selected conversations or prior versions lost; sign-in startup remains enabled |
| Live restart check | Catalogue integrity passed and schema 2 migrated without selection loss. Existing queue resumed and additional authorized versions were saved/uploaded. Unavailable sources correctly became blocked tasks; unchanged partial versions do not remain in an automatic retry loop. Missing or incomplete sources remain explicitly incomplete; this is not a claim that all backups or full restore verification have completed |
| Local rollback backup | Content-addressed resource hashes matched their expected SHA-256 values; all 52 backup board integrity checks passed using the Unicode-safe managed SQLite API |

## Earlier 0.8.2 verification

The progress update was verified locally without changing the user's selection or uploading test conversations. The application continues the user's existing opt-in queue after restart. No GitHub commit, push or release is part of this update.

| Progress update check | Result |
| --- | --- |
| .NET tests | 143 passed, including 46 AI archive/source/selection/progress cases |
| Frontend tests | 20 passed |
| Desktop harness | 77 checks against isolated synthetic content: overall and current transfer bars, counts, issue filtering, completion, pause, compact layout, Chinese/English and hidden-view polling |
| Correct completion | Latest version, confirmed destination account and settled queue required; failures, missing attachments and manifest-in-flight never display 100% |
| Queue continuity | Automatic scans do not requeue unchanged successes or reset retry deadlines; pause/resume preserves completed work; explicit retry targets only problems |
| Upload bytes | Progress reports server-acknowledged offsets, including resume and checksum-confirmed completion |
| Source regression fixes | Extended-length Windows paths normalized without broadening managed roots; selected-row SQLite exports retain row values while excluding unrelated foreign tables; relocated archives can resolve stale source paths |
| Real source verification | Two already-selected local cases exported successfully; snapshots passed database integrity and selected-thread isolation checks. No network calls or uploads were made by the diagnostic |
| Build | Windows x64 portable build succeeded; npm audit 0 vulnerabilities, NuGet audit and public-tree privacy checks passed |
| Installation | Installed 0.8.2 after normal exit, backed up the executable/settings/protected authorization/boards/AI archive, and restarted in the background. Executable hash matched; all 52 board hashes, settings, authorization and sign-in startup retained |
| Live backup state | Existing user selections and versions retained; installed worker advanced an already-authorized backup and continued another upload. Catalogue integrity passed. This is not a claim that all selected conversations or a full download/restore test have completed |

## Earlier 0.8.1 verification

The bulk-selection patch was verified locally on Windows x64. No production conversations were selected, read for backup, or uploaded by these tests. Existing working-tree changes were retained; nothing was committed, pushed, or released on GitHub.

| Patch check | Result |
| --- | --- |
| .NET regression suite | 131 passed, including 34 AI archive/source/selection cases |
| Frontend regression suite | 20 passed |
| Isolated desktop harness | 66 checks passed against synthetic content |
| Bulk storage | 10,000 selections persisted in one transaction; queue changes roll back with selection changes |
| Selection scope | Title and source filters respected; matching folded children included; context-only parents and future discoveries stay unchecked |
| Confirmation | Cancel is the default; cancelling makes no changes; additional counts and sensitive-upload warning shown before selection |
| Deselect all | Clears only the matching selection/queue scope; existing versions and outside selections retained |
| Existing completed backups | Re-selecting an already selected conversation does not reset its status or create an unnecessary job |
| UI | Visible Select all / Deselect all controls, selection count, empty-scope disabling, compact layout and Chinese/English labels checked |
| Build and privacy | Portable Windows x64 build passed; public-tree privacy check passed; npm audit found 0 vulnerabilities; NuGet restore audit passed |
| Deployment | Installed 0.8.1 after normal app exit and restarted in the background; executable hash matched the portable build |
| Local data and preferences | All 52 board hashes, settings and protected Google authorization files unchanged; sign-in startup retained; catalogue integrity check passed and user selections unchanged |
| Live Drive verification | Not performed by this patch; use the built-in non-sensitive upload/download test after account authorization |

## Earlier 0.8.0 verification

Local verification completed on Windows x64. This source change has not been committed, pushed, or published as a GitHub release.

| Check | Result |
| --- | --- |
| .NET regression tests | 123 passed, including 26 AI archive/source tests |
| Frontend tests | 20 passed |
| Isolated desktop checks | 50 passed |
| Website regression checks | 3 passed; static site checks passed |
| Dependency audit | 0 known npm vulnerabilities; NuGet restore audit passed |
| Public-tree privacy check | Passed |
| Native AI title list | 10,000 synthetic entries; virtualization, child folding, independent selection, title filtering, compact layout and both languages checked |
| Capture integration | Screenshots save while the AI list remains visible; returning to a board restores the canvas; tray collection remains available |
| Local persistence | Versioning, resource deduplication, verified restore, missing-source handling, selected-row database exports and live WAL snapshots passed |
| Network simulation | Interrupted upload resumes at the server-acknowledged offset; checksum mismatch, expired authorization and insufficient capacity are handled |
| Recovery simulation | Cloud manifests rebuild a lost catalogue and restore objects without the original local object directory |
| Credentials | Windows user-level protected storage verified with synthetic tokens |
| Local deployment | Portable executable installed and restarted in the background; prior program/settings/library backed up |
| Existing data | All 52 board hashes matched at deployment; after background collection one current-day text board gained new index rows, with existing indexed text preserved |
| Preferences | Existing settings preserved, including language, capture preferences and sign-in startup |
| Real source discovery | 636 local titles found, all unchecked; no private conversation version created or uploaded |
| Live Google Drive | Pending separate account authorization and the built-in non-sensitive upload/download test |

The source adapters target the local formats inspected for this implementation. Future provider schema changes are reported. Cloud-only histories need official manual exports. Imported exports remain one named raw entry. JSONL final records larger than the 4 MiB boundary-validation window require review. Referenced missing images produce an attachment report and an incomplete-version label. Restores do not automatically replace source-app data or guarantee continued chat in the original application.

Updated indirect frontend dependencies remove the audited vulnerable chain: Sass 1.105.2, KaTeX 0.18.2, and source-map-js 1.2.2. Existing Excalidraw remains 0.18.1. This uses the current dependency audit, not a guarantee against future vulnerabilities.
