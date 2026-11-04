02 Nov 2026

Tasks
- Default theme is now light for anyone with no saved theme choice, including the login page; a saved Light, Dark or System choice is still respected
- Deployed release/2026.11.02.01 to Azure dev (API and web)
- Deployed release/2026.11.02.02 and release/2026.11.02.03 to Azure dev (API and web)
- Deployed release/2026.11.02.04 (21 points, failed run label, In progress badge) and created release/2026.11.02.06 (run start fixes); both web only, so no API restart
- Tested a real 30 point run on 2026.11.02.04, started and stopped with permission: 30 held after the first 12 seconds and Stop responded in 0.3 seconds
- Deployed the API as 2026.11.02.06 too, so web and API show the same version (no code change for the API)
- Deployed release/2026.11.02.06 (web only; API stays on 2026.11.02.03) and repeated the test run, stopped after about 30 seconds: Run responds in 0.1 seconds, and Regulation points and Points selected stay at 30
- Measured about 100 ms page freezes several times per second during a run; not fixed yet
- Checked 2026.11.02.03 on app.comply-solutions.com: 30 points in the list and the New analysis panel, and document, chapter, row and All checkboxes all correct
- New analysis: checkboxes on document and chapter headings, ticking a parent selects everything under it, partly selected shows a dash
- New analysis: parent clauses that can be selected now have their own row, so section totals match the rows shown

Bug fixes
- app.comply-solutions.com did not work because the API blocked requests from that domain; now allowed
- New analysis: selected count, POINTS list and progress could show different numbers (for example 30, 29, 21); all now use the run's own points
- New analysis: clauses with headings starting "Int", such as Introduction, were dropped from the run's list
- New analysis: "All" could stay unticked for points libraries with duplicates, and ticks could disappear during a run
- New analysis: chapter totals such as 8/6 now match the points shown, and the separate "compared in gap analysis" figure is gone
- New analysis dropped sections that repeat a clause number (30 extracted, 27 offered; 25 extracted, 16 offered on another regulation); every extracted section is now offered, so the count matches everywhere and repeats show as "Section 2 (2)"
- New analysis hid a chapter's own clause (1, 2, 3) on screen while still counting it; it now has its own row
- New analysis: opening or running a run showed 21 points selected for a 30 point run, because the selection was rebuilt from comparable points only; it now uses every point in the run
- New analysis: a failed run showed "Processing your analysis"; it now says it did not finish
- In progress: the badge could say 1 while the page was empty; the page now corrects the badge
- New analysis: for about 12 seconds after clicking Run the numbers jumped (0, 20, 0/59, 21) and the page said "did not start", with no response for 4 seconds; Run now shows Starting and the numbers hold until the run's points have loaded
- New analysis: a stopped run said "Analysis complete"; it now says stopped
- In progress badge counted a new run twice (2 for one run)
- Internal documents: gap evidence documents could not be deleted, restored or permanently removed from the list
- Regulation and internal documents: a list refresh that started before a delete could bring the document back
- Regulation documents: bulk delete now says when manual custom points were left out
- Excel export: document name was blank on the Gap analysis and Results pages, and is now per clause for runs with several regulations
- Excel export: the New analysis page exported shortened results; it now exports the full results
- Excel export: confidence and AI model columns removed from the gap analysis file for real accounts

Investigated, follow up pending
- First 12 seconds of a run still flicker: Points list title (0, 20, 0), a brief "did not start" message, and row ticks clearing for about 2 seconds
- In progress badge still showed 1 seven seconds after a stop; not confirmed whether it clears by itself
- TFS Guidelines (v13) has 3 extracted Annex lines of about 45 characters that repeat a heading; they are now offered and analysed like any clause. Removing them at extraction is a possible follow up
- A TFS Guidelines (v13) analysis started at 23:31 was failed by the API restart during the deploy (0 of 30 done); it needs a rerun
- Not yet confirmed on a complete run: counts during the run, Excel download and document delete
- Unit tests cannot run at the moment because some test files need a test package that is not installed

04 Nov 2026

Tasks
- Deployed release/2026.11.04.01 to Azure dev (API and web); live versions confirmed, not yet tried while signed in
- New business workspaces now start with upload limits of 2 MB per document, 20 MB per upload and 20 MB total storage; the platform admin can change or clear them. Existing workspaces are unchanged
- New business workspaces now start with a clause limit of 50; the platform admin can change or clear it
- Business settings now shows the clause limit: read-only for the bank admin, editable for the platform admin
- Upload limits form checks values as you type: per upload cannot be more than total storage, per document cannot be more than per upload or total storage
- Every upload route now checks the limits on the server: evidence attachments, run evidence, text documents, cloud imports and the older upload routes, which had no check before
- A single file above the per-upload limit is now refused, and a request with several files is checked as a whole
- Restoring a deleted document is refused if it would go over total storage
- Upload pages now check the limits before sending the file

Bug fixes
- Reset password email: the link could open the main site instead of the new password page, and always pointed to the dev web address; it now returns to the new password page on the site the user is on. Not yet tested with a real email

Investigated, follow up pending
- Workspace date format setting is not followed across /nd: only the date column on Analysis runs, Checker queue and Reviewer queue uses it
- Other pages and Excel and PDF exports use the browser's language or fixed formats, so the same workspace shows mixed date formats
- Evidence re-check notes on the server use two other fixed formats, always in UTC
- Awaiting decision on the fix; it would also change dates shown to demo accounts unless they are kept as they are
- Forgot password and new password not yet tested end to end with a real account and a real email
