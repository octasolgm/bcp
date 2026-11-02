02 Nov 2026

Tasks
- Default theme is now light for anyone with no saved theme choice, including the login page; a saved Light, Dark or System choice is still respected
- Deployed release/2026.11.02.01 to Azure dev (API and web)
- Deployed release/2026.11.02.02 and release/2026.11.02.03 to Azure dev (API and web)
- Checked 2026.11.02.03 on app.comply-solutions.com: 30 points in the list and the New analysis panel, and document, chapter, row and All checkboxes all correct
- New analysis: checkboxes on document and chapter headings, ticking a parent selects everything under it, partly selected shows a dash
- New analysis: parent clauses that can be selected now have their own row, so section totals match the rows shown
- Excel export: AI model column added on the New analysis and Results pages

Bug fixes
- app.comply-solutions.com did not work because the API blocked requests from that domain; now allowed
- New analysis: selected count, POINTS list and progress could show different numbers (for example 30, 29, 21); all now use the run's own points
- New analysis: clauses with headings starting "Int", such as Introduction, were dropped from the run's list
- New analysis: "All" could stay unticked for points libraries with duplicates, and ticks could disappear during a run
- New analysis: chapter totals such as 8/6 now match the points shown, and the separate "compared in gap analysis" figure is gone
- New analysis dropped sections that repeat a clause number (30 extracted, 27 offered; 25 extracted, 16 offered on another regulation); every extracted section is now offered, so the count matches everywhere and repeats show as "Section 2 (2)"
- New analysis hid a chapter's own clause (1, 2, 3) on screen while still counting it; it now has its own row
- Internal documents: gap evidence documents could not be deleted, restored or permanently removed from the list
- Regulation and internal documents: a list refresh that started before a delete could bring the document back
- Regulation documents: bulk delete now says when manual custom points were left out
- Excel export: document name was blank on the Gap analysis and Results pages, and is now per clause for runs with several regulations
- Excel export: confidence showed "-" on the New analysis page because it exported shortened results; it now exports the full results

Investigated, follow up pending
- TFS Guidelines (v13) has 3 extracted Annex lines of about 45 characters that repeat a heading; they are now offered and analysed like any clause. Removing them at extraction is a possible follow up
- A TFS Guidelines (v13) analysis started at 23:31 was failed by the API restart during the deploy (0 of 30 done); it needs a rerun
- The New analysis page shows "Processing" for that failed run while the runs list shows Failed; not investigated yet
- Not yet confirmed on a complete run: counts during the run, Excel download and document delete
- Unit tests cannot run at the moment because some test files need a test package that is not installed
