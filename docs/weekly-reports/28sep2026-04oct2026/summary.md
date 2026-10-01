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
- Regulation documents (local/Azure): points list no longer shows empty gray rows while the header still says dozens of points - background status refresh was wiping clause text from memory; opening a doc now reloads full text or falls back to stored points
- Fixed a large empty gap under the blue page title bar on every page - a layout rule was stretching the title bar's container to half the column height; content now sits directly under the bar
- Removed the engine name badge (e.g. Azure) from local document catalog headers; initial load now shows a centered accent spinner instead of plain left-aligned text
- Document catalog pages: Refresh (icon only) and Upload moved into the blue page title bar on the right; department picker stays in the page toolbar for regulations
- New Analysis page: live progress (including "Run analysis to see live progress here") now scrolls in a center marquee on the blue page header while a run is idle or in flight
- ND blue page header: "In progress" control (with count) on the right on every page, styled for the title bar; removed duplicate from the New Analysis page body
- Internal documents (local/Azure): opening an extracted document no longer flashes "No sections extracted yet" while section text loads from the API

Tasks
- Platform settings: separate "Finalize embed model" for reviewer finalize; LLM drafts policy definitions/requirements for corrected internal docs instead of embedding raw action-plan checklists

Bug fixes
- Regulation documents (local/Azure): points panel loads full clause text again (no empty rows); shows loading while fetching; clause titles use same header/sticky pattern as internal sections
- Internal documents: analysis groups keyed by run id with date sort preserved; analysis-generated copies can use Parse and Extract like uploads
- Structural extract coverage audit (Phase 1): after local/Azure extract, API stores parsed-vs-sections coverage ratio; internal and regulation side panels show percentage and warn below 98% with a missing-text sample
- Structural extract Phase 2/3: HTML tables flattened into text; TOC pages drop pointer pairs only (not whole pages); nested "1. Sub-rule" lines stay inside parent clauses (e.g. 3.4 Name Screening)
- New Analysis (V2) layout: larger section headings and much more vertical space between Step 1, Step 2, summary/run strip, three-column workspace, and analysis report
- New Analysis workspace: Result column no longer clipped when side panels are open (horizontal scroll on the column row); columns can collapse via header control, still draggable to reorder and resizable between dividers
- Data tables app-wide: header rows use a consistent accent-tinted background and label color (All analysis, document catalogs, admin lists, dashboard tables)
- My profile page: refreshed layout (hero, account and security cards), change-password while signed in, quick links to AI credits and (for platform admin) business workspaces and AI usage
- AI usage, business workspaces, and AI credits admin pages: shared modern styling (section labels, metric strip, side-by-side create workspace and pricing forms)
- Text documents library hidden from client business workspaces; only the platform super admin sees it in the menu and can open the page or API
- New Analysis: right-side hybrid pipeline panel (engine steps, retrieval debug) visible only to platform super admin; client workspaces keep the main page and progress rail
- Internal and regulation section/point cards: clause title (e.g. "1. Introduction") moved into the card header; body no longer repeats the heading; headers stick while scrolling the side panel list
- Section and regulation point headers: previous/next controls scroll to the adjacent clause and expand collapsed chapters/sections when needed
- Internal documents catalog: rows grouped by originating analysis run with a header showing run name and document count; direct uploads in a separate block; borders separate each group

30 Sep 2026

Tasks
- Internal and regulation side panels: Re-parse, Re-extract, Parsed text, Source, and related actions sit on the same row as the document name, right-aligned (local and Azure document pages)

Bug fixes
- Regulation documents (Azure DI): opening a document no longer shows empty clause rows when local status cache had section counts but no clause text — panel loads stored points from the library API instead

Tasks
- Internal documents catalog: analysis group labels use the same column layout as data rows; groups separated by a light box instead of a full-width banner and accent bars

01 Oct 2026

Tasks
- Gap analysis report: uploaded gap-evidence documents auto-parse and extract (Azure DI + index on hybrid runs); Rerun all gaps re-judges only open clauses using that evidence and updates policy extracts when gaps close
- Gap analysis UI: report attachment rows show prepare status and poll until indexing finishes
- Gap evidence re-run: original gap text kept on record with evidence review appendix; policy extract cites new upload; action plans resolve, split, or stay open with status history; CAP history entry type "Gap evidence re-run"

Bug fixes
- Gap analysis review counts: "Action reviews" total and review summary pending-action cards now match the same corrective-action totals shown on clause cards (one slot per action plan, not per gap only)
- Gap analysis report: resolving or editing actions, gaps, and reviews updates clause badges, rollups, and compliance status on the left rail immediately (then re-syncs with the server)
- Compliant status color on analysis report badges and summary cards aligned to ss1 green (was using blue compliant tokens)
- Checker/reviewer/maker review workspace: removed workflow bar (Running badge, Refresh run, All analysis runs); fixed stale Running when point status was still pending
- Gap analysis: removed duplicate Export Excel from the report review panel (export stays in the page header with Excel/PDF)
- Clause list cards: policy excerpt line hidden on the rail; full policy extract remains in the clause detail panel
- Export popup: Excel/PDF format dropdown moved into the dialog; sheet sections labeled Gap analysis, Actions, Reviews (matches Excel tabs); Actions worksheet renamed from Action Plans
- Clause rail cards: one clause number in the header row, then the clause title as heading (no duplicate number on reviewer or analyse-regul pages)
- Clause rail cards: shared buildClauseRailCardFields for gap analysis, analyse-regul/full-v2, and admin demo preview; body excerpt drops repeated number/title
- Clause rail cards: one layout everywhere (number + status, heading, excerpt, work chips, confidence) on gap report and analyse-regul/full-v2 rails
- Internal documents catalog now lists gap-evidence uploads from analysis reports, grouped under the related run (open/download; parse stays on the report upload path)
- Gap analysis documents (upload, list, Rerun all gaps) now show on the main gap analysis report URL and every workflow view, not only the dedicated reviewer workspace; evidence-only panel when the run is not in an active review phase
- Gap evidence upload and rerun on the analysis report now show Azure parse, structural extract, and index progress (header marquee, step rail, hybrid pipeline panel) and keep polling until clauses actually finish re-judging instead of stopping on the first refresh
- Super admin on the unified gap analysis report: all workflow actions for the run status (send, approve, pull back, finalize), gap reviews on any active stage, same report layout as other roles
- Gap analysis report: compliant / partial / non-compliant summary cards now sit above the gaps-and-actions tally row on every view (including checker and reviewer)
- Shared report summary stack (compliance row then gaps/actions row) on gap analysis, New Analysis / analyse-regul / full-v2 / v8 / v9 inline reports, legacy gap report, and embedded ND results
- Opening a run from All analysis uses the same report layout as Pending review / Pending final review (shell title, summary stack, review panel, no workflow bar); dedicated review URLs still work the same
- All analysis report view: reviewer sequence (compliance cards, AI draft, review summary with gap/action stats, gap documents) for every saved run including cancelled/finalized; no Re-run bar or duplicate gap stat row

01 Oct 2026

Tasks
- Gap analysis documents: when a file was never parsed or extracted, the row shows that status and a Parse and extract button to start the Azure pipeline for that file only
- Gap evidence uploads: report rows now show parse/extract status from the stored file; each re-upload on a run gets its own v2/v3 record; server starts Azure prepare after upload; gap evidence files list under Internal documents (source Gap evidence)
- New analysis (hybrid) internal document picker now lists gap-evidence uploads from analysis reports, not only library uploads
- Report summary layout: action stat row, then gap stat row, then AI draft banner
- Review summary on gap analysis reports: compact pill button opens a right-side panel; open actions sorted by clause number; card layout for actions and review records
- Deployed bcp-api-dev and bcp-web-dev (build labels 2026.10.01.05 / 2026.10.01.08); production web budget bumped so deploy build completes
- Gap evidence re-check rebuilt as a tracked job with the same stages as New Analysis: prepare documents (Azure DI parse, structural chunking, index), retrieve evidence sections (hybrid BM25 + embeddings over the uploaded docs only), judge each open gap and action, complete
- Re-check judges gap by gap and action by action against the new document, with verbatim quotes checked against the source sections; unsupported "fulfilled" verdicts never close a gap
- Original gaps are never rewritten: each verdict is stored as a separate evidence review per clause (covered, still missing, quotes with document/section/page, what happened to each action)
- Action plans: fulfilled ones resolved, partly fulfilled ones split into a resolved part and a new pending part (same owners, date, priority), every change in status history and action comments
- Gap analysis report: live re-check panel (stages, done/running/queued/failed counts, per-clause outcome), header marquee and step rail follow the job; page resumes the panel after refresh
- Clause detail: per-gap evidence verdict chip and evidence box, new-evidence quotes with clickable references under Policy extract, evidence review history
- Upload progress labels now read "Azure Document Intelligence parsing", "Structural chunking", "Indexing for hybrid retrieval", ready with page and section counts
- Per-clause and per-gap evidence uploads are prepared on the server too, same as report-level uploads
- Unit tests for the evidence verdict rules
- Gap evidence re-check now runs the same New Analysis pipeline per clause (query expansion, sub-obligation split, BM25, embeddings, fusion, adaptive select, context, LLM judgment with the admin prompts) over the original policies plus the new evidence, then maps that verdict onto the existing gaps and actions; short evidence files are read in full
- Re-check history: "Analysed N x" on each evidence document and "Re-checked N x" on each clause card, both opening a right-side history panel (collapsible per re-check) with clauses checked, re-analysis verdict, covered / still missing, quotes and action changes
- Evidence quote references open the source: PDFs at the page, Word files in the document viewer filtered to the quoted passage
- Upload step labels simplified to Parsing, Extracting, Indexing, Ready

Bug fixes
- Rerun all gaps showed "complete" immediately and appeared to do nothing: the page checked progress before the server had started, and the server kept no run-level progress to follow
- Evidence rerun overwrote the report's workflow status (a finalized or in-review report could flip back to "completed")
- Evidence rerun re-parsed the same uploaded document once per clause at the same time; each upload was also parsed twice (server and browser)
- Evidence rerun replaced the clause's original judgment, so a fully covered clause lost its numbered gap list
- Clause detail re-numbered gaps from a different source once all gaps were resolved and the clause auto-flipped to compliant
- Word evidence files showed every section and quote as page 1 (Azure does not paginate .docx); pages now come from the layout Word saved in the file
- Evidence re-check failed a clause with "Cannot get the value of a token type 'StartArray' as a string" when the AI answered a text field as a list; list and object answers are now read as text
- Pipeline panel stayed empty during a re-check; it now shows each clause's step outputs (expansion, sub-obligations, BM25, embeddings, fusion) like New Analysis, and keeps them until the re-check panel is dismissed
- Page now scrolls to the re-check panel when a re-check starts and again when it finishes
- Kimi K3 answers were cut off mid-JSON on long clauses ("Expected end of string") because its reasoning shares the 16k output limit; limit raised to 32k for Kimi K3, and a failed re-analysis step no longer fails the clause's evidence check
- Re-check history: clauses inside a re-check can be collapsed (open by default)
- Evidence re-check failed to save a clause when an action was split (new action's history row was written before the action itself); re-check errors now show the real cause
