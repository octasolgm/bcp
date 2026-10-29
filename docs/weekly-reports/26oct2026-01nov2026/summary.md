26 Oct 2026

Tasks
- Built and deployed release/2026.10.26.01 to Azure dev: bcp-api-dev and bcp-web-dev both on 2026.10.26.01; API settings synced (18); /health/version reports ready

Bug fixes
- None

Investigated, follow up pending
- None

28 Oct 2026

Tasks
- Scanned every local and GitHub branch for the injected malware (auto-run VS Code task, fake font script, api.js in npm start, karma.conf.js tail); all local branches clean
- Cleaned 9 infected GitHub branches (main, release/2026.10.16.01, 10.19.01, 10.19.02, 10.23.01, 10.26.01, merged/oct15-all, feature/workspace-clause-limit, security/critical-fixes-2026-10-21) with no-force merges that keep the real local files; all 29 GitHub branches rescan clean

Bug fixes
- None

Investigated, follow up pending
- Force pushes cannot be blocked on the free GitHub plan; relying on revoked tokens, 2FA and a pre-pull scan instead
- Old history (Aug to 26 Oct) still holds the forged commits; do not check out or build those commits

29 Oct 2026

Tasks
- Finalized report now lists each finalized document with the resolved actions embedded in it: clause, page, action and embedded text, plus a download link
- View embedding button (next to Submit / Finalize and in the report's top bar) opens a right-side panel: before finalize it previews which resolved action goes into which document and page; after finalize it lists the embedded documents with the written text and a Download button. Nothing about embedding is shown inline in the report
- Embedded note heading no longer shows the gap number ("POLICY UPDATE - Regulatory Clause 4.1.1")
- Regulation and internal documents: several files can be uploaded at once
- New Business settings page (Admin settings) with Actions and Embeddings module switches; the platform admin has the same switches per workspace on Business workspaces
- Actions off hides corrective actions in reports (gaps and stored actions unchanged); Embeddings off makes finalize skip creating finalized document copies
- Upload limits per business in MB: per document, per upload (several files together) and total storage; set by the platform admin on Business workspaces, shown read-only to the business on Business settings with storage used
- Upload pages check the chosen files against the limits before uploading; the server also refuses a file over the per-document or storage limit

Bug fixes
- None

Investigated, follow up pending
- New pages not yet clicked through while signed in; API and web builds pass, 54 embed/workspace/finalize tests pass
