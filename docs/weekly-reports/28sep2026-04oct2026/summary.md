Weekly Summary - 28 Sep 2026 to 04 Oct 2026

28 Sep 2026

Tasks
- Maker/checker/reviewer role hierarchy: a checker can now also do a maker's actions, and a reviewer can also do a checker's and a maker's actions, on top of their own
- Forbidden-action errors now explain what role is actually needed instead of a plain "Forbidden"
- Super admin can now force-change a report's status directly (draft/maker/checker/reviewer/finalized), bypassing the normal chain, with an audit trail entry and a confirm prompt before it applies
- Added a "Finalized" tab next to All analysis / Pending correction / Pending review / Pending final review
- Report actions column made compact and responsive (dropdown + button side by side instead of stacked full-width buttons)
- Sidebar counts (e.g. Internal documents) now refresh right after any status change made from the Checker or Reviewer queue pages, not only from the single-report review screen
- Rebranded to "Comply Solutions" with the new logo/favicon everywhere (browser tab, sidebar, sign-in page)
- Build number shown in the sidebar now uses a short 2-digit counter instead of a 6-digit timestamp when no git commit is available
- Per-business AI credit limit: set a hard cap on total credits a business may use, or leave it unlimited, shown in real dollars
- Per-business margin override: each business can use the platform's default margin or its own, set independently
- AI usage reporting now shows which LLM was used through which provider (not just OpenRouter — Anthropic, Google, Azure OpenAI, xAI, DeepSeek, Moonshot, Zhipu and Qwen are all tracked separately), with new "by provider" filtering and breakdown
- Azure Document Intelligence (OCR) usage is now billed into the same AI cost/credit ledger as LLM calls, so it shows up in every cost and margin report — previously it was not tracked at all
- Deployed the API and web app to bcp-api-dev / bcp-web-dev
- Moved Inbox out of the left sidebar into a Notifications bell icon in the top bar
- Reordered the left sidebar: Overview, Analysis, Pending reviews, then a divider, then Documents and Admin settings; the local OCR engine pages stay hidden from view as before
- New Analysis page: uploading a document no longer reloads the whole page, only the affected item updates; internal document uploads now go through the same parse-and-extract pipeline as the Documents page (including Azure), with visible progress states (uploading, parsing, extracting, done); added Step 1/Step 2 labeling, select-all/clear-selection controls, and document count badges
- All Analysis table: status moved to the first column, Source column removed, rows open the report in a new tab, search box got an icon, multi-line rows with clearer column names
- Added a per-business date format setting: defaults to UAE format, with each business able to switch their own workspace to Pakistan, USA, UK, India or ISO format
- Standardized status badge colors and table styling to the same look across every data table in the app
- Fixed document list pages so a row no longer floods with a full red/green background — now uses a left accent bar so text stays easy to read
- Standardized search box and filter dropdown focus/hover styling everywhere
- Cleaned up document title and metadata display (file size, page count, points shown as small pill tags)
- Restyled the Overview page's KPI cards, compliance breakdown chart legend, and corrective actions list for a cleaner, more modern look
- Standardized global page spacing, heading sizes, and control sizing (dropdowns, search boxes, buttons) app-wide; increased main content padding

Bug fixes
- Fixed the reviewer's document-embedding feature: a gap with two resolved-and-pending action plans now gets its one resolved action embedded into the corrected document right away, instead of waiting for every action plan on that gap to be resolved
- Fixed the downloaded corrected document always being named generically "internal-document" instead of matching the real document's name and new version — caused by the browser being blocked from reading the real filename cross-origin
- Corrected document downloads are now named with a version marker (e.g. "... (v2).pdf") so v1 and v2 don't look identical once downloaded
- Fixed a section-reference symbol ("§") in extracted document text rendering small enough to be misread as a dollar sign ("$")

29 Sep 2026

Tasks
- Estimated per-clause AI cost on the New Analysis (hybrid) path for Claude Sonnet 5 and Kimi K3 through OpenRouter
- New Analysis page (V2): regulation and internal document uploads now run parse-and-extract through the same Azure Document Intelligence pipeline as the dedicated Documents pages, instead of the older Landing AI-based flow
- Sped up clause-by-clause judgment on the New Analysis page: clauses are now judged several at a time instead of strictly one at a time, cutting total run time roughly in proportion (same prompts, same accuracy, same results - only the scheduling changed)
- A finalized report's corrected-document generation now processes several internal documents at once instead of one after another, so it does not get proportionally slower on reports with many attached documents
- Added a "Regenerate documents" action for an already-finalized report, so the corrected-document step can be re-run for testing without re-running the AI analysis
- The Finalize and Regenerate actions, and the Download button for a finalized report's corrected documents, now work from the main reports list on any tab, not only the dedicated Finalized tab

Bug fixes
- Fixed a document-readiness mismatch on the New Analysis page (V2): a freshly uploaded regulation or internal document was parsed through the old pipeline but the page's own "ready to analyse" check only looked at the newer pipeline's status, so it never showed as ready until parsed again elsewhere
- Fixed Kimi K3 (via OpenRouter) running at full, uncapped reasoning effort on every clause judgment call, adding unnecessary time and cost per clause - now capped the same way the direct Moonshot connection already was
- Fixed the Finalize button on the main reports list doing nothing when clicked, for reports awaiting final reviewer sign-off
- Fixed a finalized report's corrected internal documents only getting the review notes embedded into one of several attached documents - the tool used to rewrite a document rejected some real-world PDFs outright (a structural defect some scan/export tools produce), silently leaving an unmarked copy for those; it now falls back to rendering the document as images when that happens, so every attached document gets its own corrected copy
- Fixed corrective actions being embedded into every attached internal document instead of just the one each finding's evidence actually came from, when a report had more than a couple of internal documents attached
- Fixed re-finalizing a report creating a needless duplicate document version instead of recognizing it already had one, and fixed the Download button sometimes reporting "no documents found" even when finalized documents existed - both traced to a text-matching mismatch with how the database stores the document-to-report link
- Fixed the Download button returning every past version of a document from repeated regeneration, instead of only the current one

30 Sep 2026

Bug fixes
- Fixed a large empty gap under the blue page title bar on every page - a layout rule was stretching the title bar's container to half the column height; content now sits directly under the bar
- Removed the engine name badge (e.g. Azure) from local document catalog headers; initial load now shows a centered accent spinner instead of plain left-aligned text
- Document catalog pages: Refresh (icon only) and Upload moved into the blue page title bar on the right; department picker stays in the page toolbar for regulations
- New Analysis page: live progress (including "Run analysis to see live progress here") now scrolls in a center marquee on the blue page header while a run is idle or in flight
- ND blue page header: "In progress" control (with count) on the right on every page, styled for the title bar; removed duplicate from the New Analysis page body

Tasks
- New Analysis (V2) layout: larger section headings and much more vertical space between Step 1, Step 2, summary/run strip, three-column workspace, and analysis report
- New Analysis workspace: Result column no longer clipped when side panels are open (horizontal scroll on the column row); columns can collapse via header control, still draggable to reorder and resizable between dividers
- Data tables app-wide: header rows use a consistent accent-tinted background and label color (All analysis, document catalogs, admin lists, dashboard tables)
- My profile page: refreshed layout (hero, account and security cards), change-password while signed in, quick links to AI credits and (for platform admin) business workspaces and AI usage
- AI usage, business workspaces, and AI credits admin pages: shared modern styling (section labels, metric strip, side-by-side create workspace and pricing forms)
- Text documents library hidden from client business workspaces; only the platform super admin sees it in the menu and can open the page or API
- New Analysis: right-side hybrid pipeline panel (engine steps, retrieval debug) visible only to platform super admin; client workspaces keep the main page and progress rail
