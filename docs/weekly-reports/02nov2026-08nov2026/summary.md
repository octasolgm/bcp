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
- Created and pushed release/demo1 from release/2026.11.05.01
- Started feature/support-center: Contact support for workspace users
- Support button at the bottom right of every page opens a side panel: send a new request (Report an issue, Ask a question, Knowledge request, Feature request) with subject, priority and message; list of requests; chat on each request; mark resolved or reopen
- Support item in the left menu opens the same requests as a full page
- Workspace admins see all of their workspace's requests; other roles see their own
- Unread badge on the Support button when support has replied
- Platform super admin gets a Support center menu group with Support inbox: requests from every workspace, filter by status, workspace, type and priority, search by subject, sender or SUP number; reply, add internal notes the client never sees, change status and priority
- Each request gets a number like SUP-00001; open chats check for new messages every few seconds
- Demo accounts see no Support button or menu and the server refuses them; the platform super admin uses the inbox instead of the button
- New tables nd_support_tickets and nd_support_messages are scoped to each workspace

Bug fixes
- None

Investigated, follow up pending
- Email alerts for new requests and replies: the Zoho free plan does not allow sending from other apps (SMTP); options are Azure Communication Services Email (recommended), a paid Zoho plan, or Brevo/SendGrid. Email is not built yet, waiting on the choice
- Support center compiled and the new endpoints answer, but it is not yet tested signed in (sending a request, replying from the inbox, demo account hidden)
- Attachments on requests are not included yet
