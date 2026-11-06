# Product update (02 Nov 2026)

## Light theme by default

The platform now opens in the light theme by default, starting from the sign-in page. If you have already chosen Light, Dark or System, your choice is kept. You can change the theme at any time using the Theme buttons on the sign-in page.

## Access through the main web address

The platform now works from app.comply-solutions.com. The page was loading there, but the system was refusing its requests, so the application could not be used from that address.

## Clearer selection on the New analysis page

You can now tick a document or a chapter to select every clause under it. If only some clauses are selected, the box shows a dash. Parent clauses that can be analysed now appear as their own row, so the totals match what you see on screen. The "All" box now shows correctly when a points library contains repeated clauses.

## Consistent clause numbers

If you select 30 clauses, the summary, the points list and the progress display are now designed to show the same number throughout the analysis. Clauses with headings such as "Introduction" no longer drop out of the list. Chapter totals now match the points shown. Every section found during extraction can now be selected, so the number shown under Regulation Documents and on the New analysis page is the same. Where a regulation repeats a number, for example when it restarts its sections in each chapter, the repeat is shown as "Section 2 (2)", so no clause is left out.

## Deleting documents

Gap evidence documents shown under Internal Documents can now be deleted and restored like any other document. Evidence already attached to an analysis stays with that analysis, and it cannot be permanently removed until it is taken off that analysis. A deleted document also no longer reappears when the list refreshes at the same moment. Bulk delete now tells you when manual custom points were left out.

## Gap analysis Excel export

The first column now shows the name of the regulatory document, including the correct document for each clause when an analysis covers several regulations. The file no longer includes the confidence percentage or the AI model, and the New analysis page now exports the complete results.

## Default upload limits for new businesses

A new business workspace now starts with upload limits of 2 MB per document, 20 MB per upload and 20 MB of total storage. The platform administrator can change or remove these limits for any business. Existing businesses keep their current limits.

A new business workspace also starts with a limit of 50 analysed clauses in total. The limit now appears on the Business settings page, where the business can see how many clauses it has used and how many are left. Only the platform administrator can change it.

## Upload limits checked everywhere

Upload limits now apply to every way of adding a document, including evidence files and imports from cloud storage, so a business cannot go over its total storage. Restoring a deleted document is also refused if it would go over the limit. The settings form explains straight away if a value does not make sense, such as a per-upload limit larger than total storage.

## Password reset emails

The link in a password reset email now takes you to the page for setting a new password, on the same web address you requested it from.

## Departments, teams and who sees what

The Departments page can now add a standard set of bank departments at any time, not only when the list is empty. It adds only what is missing, so using it again creates no duplicates.

Each department, unit and team now has two tabs, Team members and Documents. Type part of a name to filter the list, tick several items and add them together.

On the User management page, a user can now belong to several departments, chosen from the same three level tree. Their action inbox follows all of them.

Users other than admins now see the internal documents filed on their departments and everything under them, along with documents that have no department and documents they uploaded themselves. Existing documents stay visible until you file them.

Every analysis is visible to all departments by default. Admins can limit it to specific departments from the New analysis, Analysis runs, Gap analysis and Results pages. Choosing a department includes its units and teams and the departments above them. Admins and the person who created the analysis always keep access.

## Managing business users and settings

The Users option on the Business workspaces page is now a full management panel. The platform administrator can search a business's users, filter them by role, add new users, change roles and activate or deactivate accounts without having to enter that business first.

One person can now work in several businesses using the same login, with a separate role in each. An existing user can be added to another business and either keep their current role or be given a different one. They switch between their businesses from the top of the page, and the platform administrator can see each user's businesses on the User management page.

Business settings are now clearly separated. Modules, the clause limit and upload limits are changed on the Business workspaces page, and the Business settings page shows them as view only.

## Contact support

Each business can now reach our support team from inside the platform. A Support button sits at the bottom right of every page. It opens a panel where you can report an issue, ask a question, request a guide or suggest a feature, choose a priority and describe what you need. Every request gets its own number, and our replies appear in a chat on that request, so the whole conversation stays in one place. You can mark a request as resolved, or reopen it if you need more help. A badge on the Support button tells you when we have replied, and the Support item in the left menu shows all of your requests on a full page.

Business administrators see every request from their business. Other users see the requests they sent.

## Being worked on

We are confirming the New analysis selection and clause number changes on a full analysis run before releasing them.

We are also extending the date format chosen for each business so that it applies on every page and in exported files. Today it applies to the date column on the analysis runs and review queue pages.

We are also looking at how password reset emails will work when the platform is installed on a client's own servers.

We corrected a setting that stopped the link in password reset emails from returning to the platform. We are now confirming the full reset with a real email.

We are completing final testing of the department access rules with non-admin accounts before releasing them.

We have also prepared separate query dictionaries and synonyms for each business, and will schedule this change separately.

We are testing the new Contact support feature before releasing it, and we are setting up email alerts so that our team is notified of new requests even when they are not signed in.
