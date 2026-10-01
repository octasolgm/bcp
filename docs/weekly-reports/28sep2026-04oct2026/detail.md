Weekly Update - 28 Sep 2026 to 04 Oct 2026

Role permissions for maker, checker and reviewer

Roles now work as a hierarchy instead of three separate lanes: a checker can also do everything a maker can, and a reviewer can also do everything a checker and a maker can, in addition to their own tasks. A super admin can still do anything. If someone tries an action their role does not allow, the message now says which role is actually needed instead of a plain "Forbidden".

Super admin status override

A super admin can now change a report's status directly from the reports list, for example moving it straight to "finalized" or back to any earlier stage, without going through the normal submit/approve/finalize chain. This asks for confirmation first, and every change is recorded in the report's history for the audit trail. A new "Finalized" tab was also added next to the existing queue tabs so finalized reports are easy to find.

Cleaner reports list

The action buttons on the reports list no longer stack into a tall column of full-width buttons; they now sit compactly side by side. Sidebar counts, like the internal documents count, now update immediately after approving, finalizing or sending back a report from the Checker or Reviewer queue pages, matching what already happened from the single-report review screen.

Rebrand to Comply Solutions

The platform now shows the "Comply Solutions" name and the new logo everywhere: browser tab, favicon, sidebar and the sign-in page. The build number shown in the sidebar now uses a short counter instead of a long timestamp when no source-control reference is available.

AI cost controls per business

Two new controls are available per business, independent of each other and independent of the platform-wide defaults:

- A credit limit: a hard cap on how many credits a business may ever use, shown in real dollar terms, or left unlimited.
- A margin override: a business can be billed at the platform's standard margin, or at its own specific margin.

The AI usage report now also shows, for every call, which AI model was used and through which provider it was reached (OpenRouter, Anthropic direct, Google direct, Azure OpenAI, xAI, DeepSeek, Moonshot or Qwen), with a new filter and breakdown by provider. Previously only the model was shown clearly; now both are, since the same model can be reached through more than one provider at different cost.

Azure Document Intelligence (OCR) costs are now included

Document text extraction through Azure Document Intelligence was not being counted anywhere before. It is now billed into the same cost and credit system as AI model calls, so it appears in every existing cost, usage and margin report going forward.

Action plan embedding fix

When a reviewer finalizes a report, resolved corrective actions are embedded into the finalized copy of the internal document. This previously waited until every corrective action on a gap was resolved before embedding any of them. It now embeds each resolved action as soon as it is resolved, even if another action on the same gap is still open.

Corrected document filename fix

Downloading a reviewer's finalized document previously always showed a generic name ("internal-document") instead of the real document's name, because of a browser restriction that blocked the real filename from being read after download. This is fixed, and the downloaded file now also carries a version marker (for example "(v2)") so two versions of the same document no longer look identical once downloaded.

Notifications and navigation

Inbox has moved out of the left sidebar and into a notifications bell icon in the top bar, so it no longer takes up space in the main navigation. The sidebar itself was reordered for a clearer flow: Overview, Analysis, and Pending reviews at the top, then a divider, then Documents and Admin settings below.

New Analysis page improvements

Uploading a document on the New Analysis page no longer reloads the entire page; only the item you uploaded updates, with clear progress states (uploading, parsing, extracting, done) shown along the way. Internal document uploads now go through the same document processing pipeline used on the Documents pages, including Azure, so uploads from either place are handled consistently. The page also gained select-all and clear-selection controls, document count badges, and clearer step-by-step labeling.

All Analysis table improvements

The status of each report now shows as the very first column for faster scanning, and the separate Source column was removed since that information is already clear from context. Clicking a row now opens the report in a new tab instead of navigating away from the list, and the search box got a search icon for clarity.

Per-business date format

Each business can now choose how dates are displayed across their own workspace. The platform defaults to the UAE date format, and a business admin can switch their workspace to Pakistan, USA, UK, India, or ISO format instead, independent of every other business.

Visual consistency pass

This week included a broad visual cleanup pass across the app: status badges and table styling were standardized to the same colors and layout everywhere; document list pages no longer flood an entire row with red or green when flagged, using a left accent bar instead so text stays readable; search boxes and filter dropdowns got consistent focus and hover styling; document titles and metadata (file size, page count, points) are now shown more clearly with small pill-style tags; the Overview page's summary cards, compliance breakdown chart, and corrective actions list were restyled for a cleaner look; and spacing, heading sizes, and the sizing of buttons, dropdowns, and search boxes were standardized across the whole app.

Text display fix

Fixed a display issue where a section-reference symbol in extracted document text could render small enough to look like a dollar sign.

Deployment

Published this week's changes to the hosted API and web app.

Per-clause analysis cost

On the New Analysis page, almost all of the work that compares one regulation clause to the bank's internal policy is done locally and costs nothing. The only paid step is the judgment call that decides whether the policy covers that clause. Using current OpenRouter prices, a typical clause is about 4 to 9 cents with Claude Sonnet 5, and about 6 to 13 cents with Kimi K3. Kimi costs more because it reasons more heavily by default and its token rates are higher. A clause that is only partly covered can be judged a second time, which roughly doubles that clause's cost.

New Analysis document upload fix

On the newest version of the New Analysis page, uploading a regulation or internal document runs it through a parse-and-extract pipeline in the background so it becomes ready to analyse automatically. That pipeline was, in some cases, running through an older process while the page's own readiness check was looking for results from the newer one, so a document could finish uploading and processing but still show as not ready until it was processed again from the Documents page. Uploads on this page now run through the same pipeline the page actually checks, so a document shows as ready as soon as it is genuinely done.

Faster clause-by-clause analysis

When a New Analysis run judges each regulation clause against the bank's policy, it previously did this one clause at a time, so the total wait grew directly with the number of clauses - a run with a dozen clauses took roughly twelve times as long as a single one. Clauses are now judged several at a time in the background, cutting the total wait time significantly for runs with more than a few clauses, with no change to the judgments themselves, the prompts used, or the accuracy of the results. Separately, one of the AI models available for this step (Kimi K3, reached through OpenRouter) was found to be running at its highest, slowest reasoning setting by default on every clause; that has been capped to match the faster setting already used when reaching the same model directly, further reducing time and cost per clause.

Finalized report document fixes

A round of fixes to how a finalized report generates corrected copies of the internal documents it references.

When a report's evidence came from more than one internal document, only one of them was actually receiving the embedded corrective notes; the others were silently left as plain, unmarked copies. This was traced to the tool that rewrites a PDF being unable to open a small number of real-world PDF files, due to a structural quirk some scan or export tools produce in the file. It now falls back to rendering those specific documents as images instead of giving up, so every attached document gets its notes embedded correctly.

A related issue meant a report's corrective actions could end up embedded into every attached internal document instead of just the one each finding's evidence actually came from. This is now fixed so each document only carries the actions genuinely relevant to it.

Several smaller issues around the same feature were also fixed: the Finalize button on the main reports list was not working for reports awaiting final reviewer sign-off; re-finalizing a report was creating an unnecessary duplicate document version instead of recognizing it already had one; the Download button could report "no documents found" even when finalized documents existed, or hand back old superseded versions alongside the current one; and Finalize, Regenerate, and Download now all work directly from the main reports list, on any tab, rather than only from the dedicated Finalized tab. A new "Regenerate documents" action also lets the corrected-document step be re-run on an already-finalized report for testing, without re-running the underlying AI analysis. Finally, the document-generation step itself now processes several internal documents at the same time instead of one after another, so it does not get proportionally slower on reports with many attached documents.

Tighter page layouts under the title bar

Every page had a large empty strip between the blue page title bar and the content below it. That spacing has been tightened across the whole app, so lists, forms and analysis screens all start closer to the header instead of sitting under a blank band.

New Analysis page layout

List and report tables across the app now share the same header styling: a light accent-tinted background with darker accent-toned column titles, so the header row is easy to spot on every screen (analysis runs, document catalogs, admin settings, and similar).

The New Analysis screen is now broken into clearer stages with larger headings and more breathing room between them: regulation setup (Step 1 and Step 2), the summary and run controls, the three live columns (points, progress, and result), and the full analysis report below. The result column stays reachable when the step tracker or pipeline panel is open: you can scroll horizontally across the column row instead of the result pane disappearing off the edge. Each column can be collapsed to a narrow strip from its header, reordered by dragging the header, and resized using the dividers between columns, the same way as before.

My profile and billing admin pages

The My profile screen has a clearer layout: your name and workspace at a glance, editable display name, read-only role and department, and a Security section where you can set a new password without signing out. Administrators also get shortcut links from profile to AI credits, and platform owners additionally to business workspaces and the cross-tenant AI usage report.

The AI usage, business workspaces, and AI credits pages share updated presentation: clearer page labels, a compact metric strip on usage totals, filters grouped in a light panel, and create-workspace plus credit-pricing forms shown side by side on wide screens.

The Text documents library (the experimental local parse library under Documents) is no longer shown to client workspace users. Only the platform super admin sees it in navigation, and direct links or API calls from a business admin are blocked the same way as other platform-only tools.

On New Analysis, the detailed hybrid engine panel on the right (document parse status, retrieval steps, and phase breakdown) is platform-owner only, matching Business workspaces and AI usage. Every user still gets the left Progress rail on New Analysis; client workspace admins just do not see the right-side engineering panel.

Structural extract coverage

Internal and regulation document pages (local/Azure parse and extract) now measure how much of the parsed document text appears in structural sections after extract. The side panel shows a coverage percentage; if it falls below 98%, a warning explains that tables, table-of-contents pages, or mis-split clauses may be the cause, with a short sample of text that did not land in any section. Re-run Extract on a document to refresh the metric after parser changes; older extracts compute the ratio on the fly when you open the panel.

Structural extract quality (Phase 2 and 3)

Re-extracting a parsed regulation or internal document now uses an improved structural splitter: table content from Azure markdown is kept as readable lines instead of being skipped; table-of-contents pages no longer discard an entire page of text, only classic "clause title + page number" pointer rows; and numbered sub-lists inside a clause (such as rules listed under 3.4) stay with that clause instead of appearing as a false clause "1". After deploying or restarting the API, use Extract again on a document to refresh points and the coverage percentage in the side panel.

Regulation points list

Section and point cards

Finalize embed into corrected documents

When a reviewer finalizes an analysis, resolved gaps still produce new versions of your internal documents with the fixes written in. Instead of pasting the full action-plan text (including project steps like re-run the assessment or present ratings for approval), the platform now uses a dedicated AI model you choose under Platform settings as Finalize embed model. That model receives the regulatory clause, what was missing, what the bank decided to do, and relevant policy excerpts, and returns polished policy language (definitions and requirements) suitable for the next compliance review. If the model call fails, a short fallback is used so finalize never blocks.

Internal documents list

When several internal documents were produced from the same finalized analysis, the catalog now groups them under one heading: the analysis name (linked to open that run), plus how many documents came from it. Documents you uploaded directly appear under a separate "Direct uploads" block at the top. A colored top border and left accent on each group make the batches easy to scan.

On internal and regulation document side panels, each extracted section or point now shows its full heading in the card header row (for example "1. Introduction" or "2. Purpose") instead of only a bare number with the title repeated below. The body starts with the prose only. While you scroll the panel, the header for the section you are reading stays pinned at the top until the next section pushes it away. Each header also has previous and next controls to jump to the neighboring clause in document order; on regulations, collapsed chapters and section groups open automatically when you land on a point inside them.

Document side panel headers

On internal and regulation document pages (local pipeline and Azure), the action buttons for parse, extract, parsed text, downloads, and similar tools now share one row with the document title. The name and status stay on the left; controls align to the right so the panel uses less vertical space before the section or point list.

On Azure regulation document pages, the side panel could show the correct point count (for example 94 stored) but only empty gray clause rows with no readable text. The page was reusing lightweight local pipeline status data that listed how many sections existed without loading the actual clause bodies. Opening a document now loads full text from the saved regulation points when local cache rows are empty, so clause content appears as expected.

On local and Azure regulation document pages, the side panel could show the correct point count in the summary line but only thin empty rows in the list. That happened when a lightweight background status refresh replaced the full extraction payload in memory (counts stayed, clause text did not). Status updates now merge into the cache without dropping section text, and opening a document reloads full clause bodies or loads the saved points from the database when needed. Single-line clauses also show their text reliably when title and body are the same.

Gap evidence on the analysis report

When you upload gap evidence on an analysis report, progress now matches the hybrid New Analysis flow: Azure Document Intelligence parse, structural chunking, and search indexing show in the attachment row, the blue header marquee, and (for platform admins on hybrid runs) the same right-side pipeline panel. Rerun all gaps keeps polling until each open clause finishes re-judging (run status, pipeline phase, and per-clause pending state), with live marquee text for retrieval and LLM judgment instead of an instant "complete" toast with no visible work.

The Gap analysis documents block (upload, file list, and Rerun all gaps) now appears on the main gap analysis report when you open a run from All analysis, as well as on pending correction, pending review, and pending final review workspaces. You no longer need the dedicated reviewer URL to add report-level evidence. When the run is not in an active review phase, you still get upload and rerun; the overall sign-off form stays hidden until the report is with checker, reviewer, or maker for correction.

Platform super admins opening the same gap analysis report see one shared layout with makers, checkers, and reviewers. On that page they can use every workflow action that fits the run's current status (send to checker, approve to reviewer, pull back, finalize when the report is in final review), add gap-level reviews at any active stage, and upload or rerun gap evidence like any other review role. After they act, the page refreshes in place instead of sending them back to a queue.

After an analysis is complete, you can attach new internal policy documents at report level (or per clause as before). Those uploads now go through the same prepare path as the hybrid New Analysis flow: Azure Document Intelligence parse, local section extract, and search indexing on hybrid runs. When you click Rerun all gaps, only clauses that still show a gap are re-judged. The engine searches your original internal library plus the new uploads, runs the forward judgment again, and leaves open gaps unchanged if the new material does not address them. When a gap is closed by the new document, the clause moves to resolved or compliant and the policy extract on that row reflects text from the supporting file. The report attachment list shows while each file is still preparing and refreshes automatically until indexing is done. Per-clause evidence upload and rerun are unchanged.

When new evidence closes or partly closes a gap, the original gap wording is not deleted. The clause keeps an on-record section for what was found initially, plus a dated evidence review block that states whether the upload fully or partly fulfilled the gap, what is still pending, and quotes from the new document in policy extract (prior extracts are kept as "Prior analysis" where helpful). Corrective actions follow the outcome: all resolved when the gap is fully covered; only matching actions resolved when part of the gap is addressed; an action that is only partly satisfied is split into a resolved portion and a new pending follow-up, each with status history. Makers can see CAP history entries labeled "Gap evidence re-run" alongside earlier AI and manual edits.

Review and summary counts on gap analysis

The clause list, review summary cards, and the submit panel now use the same definition of a corrective action. If two clauses each carry two open action plans, you see four actions in the header chips and four slots in "Action reviews: 0/4", not three because the old logic counted gaps only. The review summary "Pending actions" card shows how many individual action plans are still open, not how many clauses have at least one open plan.

When you resolve or reopen an action, edit a gap, or save a review on the gap analysis report, the UI updates right away: gap status chips, action pending counts, review progress, and the clause compliance badge on the left rail all move together. When every action on every gap on a clause is resolved, the clause can flip to compliant automatically, matching what the server already did but without waiting for a full page refresh.

Clause cards in the left rail now show the clause number, status, heading, and regulatory text, then gap and action chips and confidence. The short policy excerpt line is no longer shown on the card so the list stays easier to scan; open a clause to read the full policy extract in the detail panel. Excel and PDF export for the run are only in the blue page header (format dropdown plus Export); the extra Export Excel control on the overall report review block was removed to avoid duplication.

Clicking Export in the page header opens a refreshed export dialog. You choose Excel or PDF at the top of that dialog. For Excel, each worksheet is grouped under clear headings: Gap analysis, Actions, and Reviews, with the same names on the tabs in the downloaded file (the actions tab was renamed from Action Plans). You can still pick columns, rename headers, and include or skip the actions and reviews sheets. PDF export from the same dialog downloads the full narrative report without the column picker.

Gap documents that were uploaded but never prepared

On the analysis report, each gap document row now states clearly when parse and extract have not run yet. In that case you get a Parse and extract action on the row so you can start Azure parse, structural extract, and indexing for that file without re-uploading. While prepare is running, the row shows the same step progress as before; when it finishes, the file is ready for Rerun all gaps or per-clause evidence rerun.

Review summary drawer

Instead of a large inline accordion on the report page, Review summary is now a small button (with open and overdue counts when relevant). Clicking it opens a panel from the right with a clear header, open actions listed as cards sorted by clause number (3.9 before 3.10), target date and responsible party on each card, and review records below with the same date filter as before. Press Escape or click outside to close.

Re-checking gaps against newly uploaded evidence

After an analysis is finished, you can upload a new or updated policy document and re-check the open gaps against it. This now works the same way as running a new analysis, and you can watch it happen:

- The uploaded document is prepared first: Azure Document Intelligence parsing, structural chunking, and indexing for search. Each stage is named on the document row while it runs, and the row shows page and section counts once the file is ready.
- Clicking Rerun all gaps starts a re-check of only the clauses that still have gaps. A progress panel on the report shows the stages (prepare documents, retrieve evidence, judge gaps, complete), how many clauses are done, running, queued or failed, and the result for each clause as it finishes. The blue header and the progress rail follow the same steps. If you refresh or leave and come back, the page picks the progress back up.
- For each clause, the most relevant passages are found in the new document, then the configured AI model checks every open gap and every open corrective action against them. Each conclusion must be backed by a quote taken word for word from the document; a "fulfilled" answer with no supporting quote is never allowed to close a gap.

What happens to your gaps and actions:

- The original gaps are never deleted or reworded. They stay on the clause exactly as found, with a clear marker on each gap: fulfilled, partly covered, or not covered by the new evidence.
- Each gap shows what the new document now covers, what is still missing, and the supporting quotes with a reference to the document, section and page. These quotes also appear under Policy extract, and clicking a reference opens the document.
- Corrective actions follow the evidence: actions the document fully satisfies are resolved; an action that is only partly satisfied is split in two, with the done part resolved and the remaining part kept open as a new action with the same owners, target date and priority. If every action on a gap is done but part of the gap is still missing, a new action is added for the remainder so it stays owned.
- When every gap on a clause is resolved, the clause turns compliant automatically; a clause that was non-compliant and is now partly covered moves to partially compliant. A status someone set by hand is never changed.
- Everything is kept on record: each re-check is saved in an Evidence review history on the clause (date, who ran it, documents used, AI model, outcome per gap), every action status change is in the action history, and split actions keep the original wording in their notes.

The same re-check runs when you upload a document for a single clause or a single gap and use the rerun button there.

Fixes in this area: Rerun all gaps previously showed "complete" straight away while nothing visible happened, because the page checked for progress before the work had started. A re-check could also change a report's workflow status (for example a finalized report moving back to completed), parsed the same uploaded file several times in parallel, and could replace a clause's original findings so that its gap list disappeared. All of these are fixed. Resolved gaps also keep their original numbering when a clause turns compliant.

Re-checks now use the full New Analysis pipeline

Re-checking open gaps against an uploaded document now runs every clause through exactly the same steps as a New Analysis: the same search over your documents and the same analysis prompts and checks, this time over your original policies plus the new document. That fresh verdict is then matched against each open gap and corrective action, and only text found in the new document is credited as newly covered. Short documents are read in full so no passage is skipped.

Each evidence document on the report now shows how many times it has been used in a re-check, and each clause card shows how many times it has been re-checked. Clicking either opens a history panel on the right listing every re-check (collapsed to a one-line summary until opened) with the clauses checked, the fresh verdict, what is now covered, what is still missing, the supporting quotes, and which actions were resolved or split, or that nothing changed.

Page references for Word documents are now correct. Word files used to cite page 1 for everything, because the document reading service does not paginate Word files; pages now come from the page layout Word saved in the file, for every section and every quote. Clicking a quote reference opens a PDF at that page, or opens a Word file in the document viewer filtered to the quoted passage. Upload progress now reads simply Parsing, Extracting, Indexing and Ready.

Re-check fixes

A re-check could fail on a clause with a technical error when the AI answered part of its verdict as a list of points instead of a single paragraph. Those answers are now read as normal text. During a re-check, the pipeline panel on the right now fills in each clause's search details (expanded terms, keyword and meaning-based matches, and the combined result) the same way it does on New Analysis, and the page scrolls to the re-check progress panel when you start a re-check and again when it finishes.
