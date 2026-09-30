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

On New Analysis, the hybrid pipeline tooling (right-side engine panel and left progress rail with retrieval/judging phases) uses the same access rule as Business workspaces and AI usage: only the platform owner, not a client workspace's admin. Client users still get the main New Analysis screen, upload progress, workspace columns, and report without those panels.
