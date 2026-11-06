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

05 Nov 2026

Tasks
- Departments: Add sample bank departments button now also shows when the workspace already has departments; it adds only what is missing, so adding again creates no duplicates
- Departments: every level (department, unit, team) has Team members and Documents tabs; tabs show added of total (for example 0 of 1); type to filter, tick several, add them together; the list sits in the page, not over the rows, and its height can be dragged; Esc closes the list, then the panel
- Users: a user can belong to several departments, chosen from the three level tree with search; demo accounts keep their dropdown
- Internal documents: non-admins see documents filed on their departments and everything under them, documents with no department, and their own uploads; admins see all
- Analysis: Visible to option on New analysis, Analysis runs, Gap analysis and Results; default is all departments, admins can limit it to specific ones; lists, dashboard numbers, queues, inbox and direct links all follow it
- Action inbox now counts all of a user's departments
- Business workspaces: the Users panel now manages that business's users: search, filter by role, add a user, change role, activate or deactivate. Your own account, platform super admins and demo accounts cannot be edited there
- Modules can now only be changed by the platform super admin, also enforced on the server
- Business settings page is view only for everyone: modules show On or Off; changes are made on Business workspaces
- Created and pushed release/2026.11.05.01 and deployed it to Azure dev (API and web)
- One user can now belong to several businesses with a separate role in each: Add existing user on Business workspaces, with a role dropdown for each person that starts on their current role; creating a user with an email that already has an account adds that person instead
- Users in several businesses get a business switcher at the top of the page
- User management shows each user's businesses, for the platform super admin only
- Business Users panel redesigned to match the page; Esc closes what is open on Business workspaces, Departments and User management


Bug fixes
- Reset password email link opened localhost:3000 on the deployed site; Supabase Site URL is now app.comply-solutions.com, and app.comply-solutions.com, bcp-web-dev and localhost:3002 are accepted return addresses. Return addresses confirmed with a test call, not yet tested with a real email

Investigated, follow up pending
- Report that deleted users still showed in the business Users panel could not be reproduced: every listed user has a valid account. Panel now refreshes when you return to it; demo accounts removed from it. Delete could leave a user listed if it failed part way; order fixed
- Query dictionary and Query synonyms per workspace: written and compiled, then set aside at the user's request; nothing applied to the database
- Reset password email comes from Supabase's default sender because the app has no mail sending of its own and no custom mail server is set up in the code or the in-house package
- App has no email notifications today; adding them needs a mail service in the API
- In-house package and deployment docs have no mail settings yet
- Decision: reset emails stay on the Supabase sender for now, custom SMTP stays off
- Not decided yet: whether to build email notifications
- New reset email not received after the redirect fix although the page reported success. Cause not found; the Supabase sender's low limits are one possibility. Full reset with a real email still not tested
- Tested in the running app as an admin in workspace ABC: sample button (no duplicates), adding and removing team members and documents, counts, Esc, Users tree picker, Visible to choice on New analysis. Test data removed
- Business Users panel tested in ABC: list, search, Add form fields and role change (changed and put back). Adding a real user and Deactivate not tested
- Not yet tested: signing in as a user who belongs to several businesses, the switcher and their role in each; deleting a user with the new delete order
- Not yet tested: what a non-admin account sees, because ABC has no non-admin user; saving Visible to on a real analysis, because ABC has no analyses

06 Nov 2026

Tasks
- New branch feature/demo1-plan-limits from release/demo1
- Created and pushed release/2026.11.06.01 from feature/demo1-plan-limits: release/demo1 plus plan limits, coloured progress title and deactivation fixes; the support center is not included
- Deployed release/2026.11.06.01 to Azure dev (API and web), shown as version 2026.11.06.02; API reported ready, new features confirmed in the live site files; not yet tested signed in on the live site
- New per-analysis clause limit for each business: the platform super admin sets "Clauses per analysis" on Business workspaces; Business settings shows it read only
- New businesses now start with 100 clauses in total (was 50) and 10 clauses per analysis; existing businesses and the demo keep their current values
- New analysis shows a Demo plan notice for any business with a clause limit: clauses per analysis, clauses left, and selected out of the per-analysis limit
- New analysis blocks Run analysis, with a reason, when the selection is over the per-analysis limit or over the clauses left; the server refuses the same on start
- Progress title in the blue top bar on New analysis now has colour: amber with a pulsing dot while an analysis starts or runs, green when complete, red when it failed, stopped or did not start
- Demo accounts see no notice and no limits; demo runs skip the limit checks as before

Bug fixes
- A user still signed in when their business was deactivated saw every page fail; they are now signed out once and the sign-in page says the workspace has been deactivated
- A user whose home business was deactivated but who also belongs to another active business was locked out; they now continue in the other business

Investigated, follow up pending
- Business deactivation checked in code: sign-in is refused, every page is refused within about 30 seconds (members' cached access is cleared at once), the platform super admin keeps access, the default workspace cannot be deactivated
- Tested signed in as platform super admin: a new business "Limit test" was created with 100 total and 10 per analysis; Clauses per analysis changed to 3 on Business workspaces and saved; New analysis in Limit test showed the Demo plan notice (3 per analysis, 100 of 100 left)
- Tested on New analysis in the Default workspace with 30 clauses selected and a limit set in the page only (no data changed): over 3 per analysis, over clauses left and all clauses used each disabled Run analysis with the right message; within limits it was enabled; Default workspace itself shows no notice
- Tested the amber progress title with pulsing dot; green and red use the same mechanism, not seen on a real run
- Limit test was then deactivated (left deactivated, with 3 per analysis); platform super admin kept access
- Not tested: a real user of a deactivated business being signed out, or moving to their other business, because there is no test user; the server refusing an over-limit start, because Limit test has no documents
- Unit tests for the per-analysis limit added; 31 limit and workspace tests pass
- This week's report on feature/support-center has its own 06 Nov section; combine the two when both branches are merged
