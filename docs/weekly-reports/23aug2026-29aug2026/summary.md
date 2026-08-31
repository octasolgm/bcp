Weekly Summary - 23 Aug 2026 to 29 Aug 2026

24 Aug 2026

Tasks
- Fixed inbox deep links so "Open gap" and "Open action" land on the exact clause and gap, for every role (checker, reviewer, maker, admin)
- Added a poll/retry to the scroll-to-gap logic so it works reliably on large runs, instead of one timed attempt that could miss
- Made the "Review summary" panel start collapsed so checker and reviewer land directly on the clause list and detail view instead of a long open-actions table
- Restyled review-request cards in the inbox to match action-item cards: added a "Review" type pill, a colored status pill, a left-border accent, and the missing gap number in the context line
- Added a calendar-aware countdown under target dates on pending actions (for example "2 months 3 days left", "Due today", "3 days overdue")
- Added the same relative-time treatment to review "Sent" dates ("Today", "3 days ago")
- Restructured the inbox into All, Actions and Reviews tabs with live counts, so a long list in one section no longer buries the other
- Lazy loaded the PDF export library so it only loads when Export PDF is actually clicked, instead of on every page visit
- Committed the above work and deployed both the API and the web app to the Azure dev environment

Bug fixes
- Fixed the sidebar inbox badge showing 0 for a role that had a real pending review
- Confirmed a reported "duplicate owner" case was not a bug: two demo accounts share the same display name but different emails, and the UI already tells them apart by email

26 Aug 2026

Tasks
- Standardized status colors across the app: document status (red or green), gap resolved or pending (red or green), and compliance findings (red, yellow or green), all pulled from one shared color source instead of scattered one off colors
- Added custom column header text to the Excel export tool, and made that export picker available from the Results page as well as the Gap Analysis page
- Redesigned the report submit workflow into a single Submit button with a role dropdown, replacing several separate buttons, and removed the old whole report responsibility field
- Added a check that blocks a maker from submitting a report if any gap is missing an action, a responsible role, or a target date
- Added a Reports tab to the inbox for submissions pending your review or sent back to you, with unread rows highlighted (also added to the Reviews tab)
- Added an Export Excel button directly on the report review panel

Bug fixes
- Fixed document status colors showing blue instead of red or green
- Fixed the finding count on a completed report undercounting right after the run finished
- Fixed the submit role dropdown not appearing when there was only one valid choice
- Fixed slow regulation document list loading by caching a calculation that was being redone on every page load

Investigated, follow up pending
- A demo account document parsing issue was traced to a missing template case, fix is pending a decision on the right fallback
- A reported duplicate actions issue and a one off slow load report were investigated with no reproducible bug found yet
- Multiple comments on a single report review needs a new backend feature, not built yet

28 Aug 2026

Tasks
- Fixed the workflow status label so a report's stage reads the same everywhere: it used to show "Finalizing" in the maker's list even after the report had moved on to the checker
- Removed the duplicate "Workflow" column from the analysis lists since it repeated the "With" column once a report leaves the maker
- Added a Summary column (colored compliant, partial, non compliant counts) and a Gaps / actions column to every analysis list, so maker, checker and reviewer all see the same columns and the same data
- Added a Recall action so a maker can pull a report back from the checker, or a checker can pull a report back from the reviewer, before the next person has acted on it
- Added a Submit dropdown to the checker and reviewer list tables (send to reviewer or back to maker; finalize, send to checker, or send to maker) so these actions no longer require opening the full review page
- Built and deployed the API and the web app to the Azure dev environment (version 2026.08.28.091336)

Bug fixes
- None today
