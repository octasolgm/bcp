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
- Sales guide for Comply Solutions in Jumppl (docs/jumppl/comply-solutions-sales-guide.md): one task per bank, pipeline statuses, bank custom fields, CSV import of the bank list, client portal use, file storage folders, sales to development handover, step-by-step tasks per role, daily/weekly/monthly routines, rules and Q&A, setup status
- Jumppl Ultar Tech setup for Comply Solutions sales: pipeline statuses in Sales Comply Solutions (Lead to Won, Lost, On hold), folders 01-07 in Sales Folder Comply Solutions; sub-projects by country (UAE, Saudi Arabia, Pakistan) with bank types under each; 12 bank detail custom fields (city, contacts, lead source, deal value, dates) on the project and every sub-project

Bug fixes
- None

Investigated, follow up pending
- New pages not yet clicked through while signed in; API and web builds pass, 54 embed/workspace/finalize tests pass

31 Oct 2026

Tasks
- Platform setting "Analysis pipeline panel" (Platform settings, platform admin only): on shows the right-hand pipeline panel while an analysis runs, as before; off hides it and shows a plain "Processing your analysis - X of Y clauses checked" status at the top of the page, with no pipeline step names
- Users who never saw the pipeline panel now also get that plain status while an analysis runs
- New release branch release/2026.10.31.01 built and deployed to Azure dev

Bug fixes
- None
