02 Nov 2026

Tasks
- Default theme is now light for anyone with no saved theme choice, including the login page; a saved Light, Dark or System choice is still respected
- Deployed release/2026.11.02.01 to Azure dev (API and web)
- Created release/2026.11.02.02 with the fixes below (not deployed yet)
- New analysis: checkboxes on document and chapter headings, ticking a parent selects everything under it, partly selected shows a dash
- New analysis: parent clauses that can be selected now have their own row, so section totals match the rows shown
- Excel export: AI model column added on the New analysis and Results pages

Bug fixes
- app.comply-solutions.com did not work because the API blocked requests from that domain; now allowed
- New analysis: selected count, POINTS list and progress could show different numbers (for example 30, 29, 21); all now use the run's own points
- New analysis: clauses with headings starting "Int", such as Introduction, were dropped from the run's list
- New analysis: "All" could stay unticked for points libraries with duplicates, and ticks could disappear during a run
- New analysis: chapter totals such as 8/6 now match the points shown, and the separate "compared in gap analysis" figure is gone
- Regulation lists showed 30 points for a document with 27 different clauses, because repeated Annex headings were counted; they now show 27
- Internal documents: gap evidence documents could not be deleted, restored or permanently removed from the list
- Regulation and internal documents: a list refresh that started before a delete could bring the document back
- Regulation documents: bulk delete now says when manual custom points were left out
- Excel export: document name was blank on the Gap analysis and Results pages, and is now per clause for runs with several regulations
- Excel export: confidence showed "-" on the New analysis page because it exported shortened results; it now exports the full results

Investigated, follow up pending
- One regulation (amlcft cb uae decision) restarts Section numbers in each chapter, so the picker shows 16 of its 25 sections; 9 real clauses never reach an analysis. Needs a decision on how to number them
- New analysis count and checkbox fixes were compiled but not tried on a real signed-in run yet
- Unit tests cannot run at the moment because some test files need a test package that is not installed
