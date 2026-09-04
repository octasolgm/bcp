Product Update - Week of 31 August to 6 September 2026

For posting to the client portal.

This week at a glance

Work this week focused on making report recall more practical for teams, keeping gap and action counts accurate as a report is reviewed, and publishing a fresh build to the development environment.

Any maker or checker can now recall a report

Previously, only the person who submitted a report could pull it back from the next stage. Recall now follows role, so any maker can pull a report back from the checker, and any checker can pull it back from the reviewer, as long as the next person has not yet acted on it.

Clause status stays in sync as you work

When a gap is resolved or an action is updated, the clause verdicts on the report now refresh immediately, without needing to reload the page. Badges that count gaps and actions also stay accurate after a clause is automatically marked compliant: resolved items on that clause are still counted instead of disappearing from the totals.

The MIS panel now lists items in clause number order, rather than grouping resolved items at the top, so it is easier to scan a report in document order.

Today's latest build of the API and the web app has been published to the development environment.

New local document processing, built without any external AI service

A major piece of work this week: documents can now be read and processed entirely on our own servers, without sending anything to a third-party AI service. This was driven by two goals - reducing cost, and keeping document content fully private, since nothing about a scanned document now needs to leave the server it is processed on.

New versions of the Internal Documents and Regulation Documents pages were built alongside the existing ones (the original pages are untouched and still work exactly as before). On the new pages, converting a document to text and splitting it into individual clauses or points are now two separate, clearly labelled steps, so it is always clear whether a document has been read yet, or has also had its points extracted. A new panel lets you view the full text of a document as soon as it has been converted, even before extraction runs.

Processing speed was a major focus once this was working: a 23-page scanned document that previously took several minutes to process now finishes in under a minute and a half, by reading multiple pages at the same time instead of one at a time. Several smaller reliability issues were also found and fixed along the way - a status display that could show contradictory information, a row that could get permanently stuck if the server restarted mid-process, and a page-count display that could show numbers belonging to an unrelated, similarly-named document.

A simpler third document library, called Text Documents, was also added for cases that do not need the full internal/regulation document workflow, showing extracted content as a nested list of points and sub-points.

Comparing different document-reading technologies

To make sure the new local processing is as accurate as possible, several different underlying reading technologies are being tested side by side on the same real documents, each available on its own page for direct comparison. This is ongoing evaluation work, not yet a final decision - full findings and a recommendation will follow once testing is complete. One early finding: a more advanced, AI-assisted reading mode is showing noticeably better accuracy than the simpler options, but is currently too slow to use on full documents without dedicated hardware, which is a separate decision still under discussion.

Planning for future private/on-premise deployment

With document processing now able to run without any external AI service, a written plan was put together for how the whole platform could eventually run entirely within a client's own private network, for clients with the strictest data-privacy requirements. This is a planning document only - nothing has changed about how the platform is deployed today.
