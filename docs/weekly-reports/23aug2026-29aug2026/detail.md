Product Update - Week of 23 to 29 August 2026

For posting to the client portal.

This week at a glance

This week's work covered three areas: making sure notifications and deep links take reviewers straight to the right place, a clearer and more consistent way to read status and compliance colors, and a simpler workflow for routing analysis reports between team members. We also improved page load times in a few places.

Notifications and review links now go to the right place

Clicking an item in your inbox (a pending action or a review request) now reliably opens the exact clause and gap it refers to, and scrolls straight to it, for every role: maker, checker, reviewer and admin. Previously this worked for some roles but not others, and on larger reports the page sometimes failed to scroll to the right spot at all.

We also fixed the inbox notification badge in the sidebar, which could show zero even when there was a genuine pending review waiting for you.

Review and action cards in the inbox now look consistent

Review requests and corrective actions used to look noticeably different in your inbox, even though they are two views of the same list. Review cards now carry the same visual treatment as action cards: a clear label, a colored status indicator, and the same layout for dates and context.

Target dates on pending items now show a plain language countdown underneath the date itself, for example "2 months 3 days left" or "3 days overdue", instead of just a calendar date you had to work out yourself. Review requests show the same treatment for when they were sent, for example "Today" or "3 days ago".

The inbox is now split into All, Actions and Reviews tabs with live counts, so a long list of one type no longer buries the other.

Clearer status colors, everywhere

Document and gap statuses across the platform now follow one consistent color language.

Documents: red means a document still needs to be analyzed, green means it has been analyzed.

Compliance findings: green for compliant, yellow for partially compliant, red for non compliant, applied consistently across every report and overview screen.

Gap tracking: red for pending, green for resolved.

A simpler way to route reports for review

Sending an analysis report through your review chain, maker to checker to reviewer, is now a single Submit action with a dropdown to choose who receives it next, replacing several separate buttons that did similar things in slightly different ways. The dropdown only ever shows the people who can legitimately receive the report next.

We also added a safeguard: before a report is submitted, the platform checks that every identified gap has an assigned owner, a target date and a corrective action written up. If something is missing, submission is blocked with a clear note on what to complete first.

Reports pending your review, or sent back to you for correction, now show up in your inbox alongside your action items, with unread items visually highlighted.

Faster loading

Two loading issues were tracked down and fixed this week. Report review pages were bundling a large export library on every visit even when it was never used, so it now loads only when you actually export a PDF. Separately, the regulation document list was redoing an expensive calculation from scratch on every visit, so it is now cached and loads much faster on repeat visits.

Also this week

The Excel export tool now lets you customize column headers per sheet, and this customization is now available from more places in the app, not just the main gap analysis screen.

An Export to Excel button is now available directly on the report review screen.

Fixed a display issue where a completed report's finding count could show fewer findings than it actually had, right after the run finished.

One consistent view of a report's status, for every role

The status shown for a report as it moves from maker to checker to reviewer could disagree depending on who was looking at it. A checker would correctly see "With checker," but the same report could still show "Finalizing" in the maker's own list. This is now fixed so every role sees the same, correct status for a report at all times.

The analysis lists also carried two columns that ended up showing the same information once a report moved on to the next person. That duplicate column has been removed, and in its place every list (maker, checker, and reviewer) now shows a Summary column with a color coded compliant, partial, and non compliant breakdown, plus a Gaps and actions column, so every role sees the exact same information about a report at a glance.

Reports can now be pulled back before the next person acts on them

If a maker submits a report to the checker, or a checker sends it on to the reviewer, and it turns out that was too soon, it can now be recalled back to whoever sent it, as long as the next person in line has not yet acted on it. Once recalled, the report immediately leaves the other person's queue and inbox.

Checkers and reviewers can also now submit a report onward, or send it back, directly from their list, without needing to open the full report first: the checker can send a report to the reviewer or back to the maker, and the reviewer can finalize a report or send it back to the checker or maker.

Today's latest build of the API and the web app has been published to the development environment.

Being worked on

A known issue affecting demo account document parsing in edge cases has been diagnosed and is scheduled for a fix.

Support for adding multiple comments to a single report review, rather than one editable note, is planned as a follow up feature.
