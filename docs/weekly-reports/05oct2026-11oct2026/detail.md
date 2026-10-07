Weekly Update - 05 Oct 2026 to 11 Oct 2026

Reliable multi-clause analysis

When an analysis covered several regulatory clauses, some clauses could fail even though the AI had produced a valid judgment. This happened when the AI cited supporting text from more than one internal document and listed those references in a slightly different format than expected. The platform now accepts both formats, and the AI instructions were updated to always return references in one consistent form. Multi-clause analyses now complete for every clause.

Action plans that match the actual gap

The first-draft action plan created for each gap now uses the AI's own corrective action, written specifically for that clause and that gap. Previously, a generic template was picked by keyword, which could add requirements the regulation never mentioned (such as reporting deadlines or escalation paths) and could cut the gap description short. Each gap also now receives its draft action only once, where before it could occasionally appear twice.

Clearer pipeline progress

The pipeline panel on the New Analysis page now shows a progress bar for each step, with the number of clauses completed out of the total. The AI judgment step also shows how long it has been running and an estimate of the time remaining. Clauses now show as completed one by one as soon as each judgment finishes, rather than all at once at the end.

Full text of matched policy sections

When reviewing which internal policy sections were matched to a clause, the panel now shows the complete text of each matched section instead of a short preview, so analysts can verify exactly what the AI was given.

Faster analysis without lower accuracy

More clauses are now judged at the same time, which shortens the total time for larger analyses. The depth of the AI's reasoning and the checks that require both a gap and an action are unchanged, so accuracy is not affected.

More accurate clause judgments

Each clause is now judged against the policy sections the search actually found for that clause, rather than against the full text of every attached document. This keeps the AI focused on relevant evidence and makes each clause much faster to judge.

A clause can no longer appear as "partially compliant" with no gap listed. Previously, if the platform could not match one of the AI's quotes word for word, it lowered the verdict without explaining why. Quotes are now checked against the full documents, unmatched quotes are simply left out, and a verdict is only lowered when none of the evidence can be confirmed, in which case the report says so clearly and gives an action.

Gaps and actions per requirement

When a clause has several separate missing requirements, each one now appears as its own gap with its own action plan, and a gap can carry more than one action when it needs changes in more than one place. The report also now lists which parts of the clause are already covered by your policies, with the supporting document reference, instead of showing "None".

Updated AI instructions

The AI instructions were tightened so that every part of a clause is accounted for as either covered or missing, the verdict always matches the gaps listed, and legal definitions that staff must apply (such as what counts as "funds" or a "predicate offence") are treated as requirements, while purely background statements are not.

Full audit trail of every AI judgment

For every clause, the platform now records exactly what was sent to the AI (the selected policy sections and the instructions), every response the AI returned, how long each call took, and what was finally saved. Administrators can review this in the pipeline panel and in the browser's developer console, alongside the result of each search step, so any judgment can be traced back to the evidence it was based on.

One consistent analysis workflow everywhere

A new analysis, re-running all clauses, re-running a single clause, and re-checking gaps against newly uploaded evidence now all follow exactly the same steps: every policy document is checked to be fully processed (and processed automatically if it is not), the relevant policy sections are searched again for each clause, and only those sections are given to the AI. Previously some re-runs reused older search results or skipped documents that had not been processed yet. The same policy text can also no longer appear twice in what the AI reviews when a document was processed more than once. Single-clause re-runs are now included in AI usage reporting.

Faster analysis

Several steps around the AI judgment were made much faster without changing how clauses are analysed: settings and document text are no longer re-loaded for every clause, and the search step no longer re-downloads policy text it already has. In a test on two clauses against five policy documents, the time before the AI starts judging dropped from about 1.5 minutes to under 30 seconds, and the Internal Documents list now loads in about 3 seconds instead of 10.

AI model shown on every report

Each analysis report now shows, right under its title, which AI model produced the findings, so reviewers always know how a result was generated.

More reliable AI judgments

If the AI service returns an incomplete or unreadable answer, or is briefly unavailable, the platform now automatically asks again (up to three times) instead of marking the clause as failed. Every attempt is recorded in the audit trail.

Being worked on

We compared two AI models on the same clauses and documents. One gives more precise, better-evidenced results but takes longer; the other is several times faster but tends to flag background statements in the regulation as gaps. The choice of model for clause judgment is being finalised.

Re-running a single clause no longer triggers an unnecessary document re-processing step in an external service, so clause re-runs are faster and avoid extra cost.

Safer handling of search errors

If the step that finds relevant policy sections fails, the analysis now stops with a clear error instead of continuing without any policy text, which could otherwise have reported gaps that do not exist.

Internal document versions after final review

When a reviewer finalizes an analysis, the corrected internal document appears in the library with a clear version label (for example v2) next to the document title, so you can tell the finalized copy apart from the original upload at a glance.

Resolved actions in finalized files

Finalized PDF and Word copies now embed resolved action plans as policy notes more reliably. If the run used only one internal document, or the AI cited a passage that appears in a specific file, those actions are written into the downloaded corrected document (PDFs add note pages after the cited page). For a run you already finalized, use Regenerate corrected documents on the analysis run row, then download the new version.

Each finalized document now shows what was added to it. In the Internal Documents list, a finalized copy shows how many resolved actions were embedded; expanding it lists the clause, gap and page for each one, together with the policy wording that was inserted. Notes are now placed only in the documents the analysis cited as evidence for that clause, and when an action names a specific document, only in that document. The inserted text is written as finished policy wording rather than a copy of the action plan, and it is never cut short.

Document tables are easier to work with. In Internal Documents and Regulation Documents the document name stays visible on the left and the actions stay visible on the right while you scroll through the other columns. You can tick several documents (or all of them) and parse, extract, download or delete them in one go. Each group of documents generated from an analysis now has a History button that shows, for every document, exactly which resolved actions were added to it and where.

Regulation points visible again

Opening a regulation in Regulation Documents showed its chapters as thin empty bars instead of the extracted points. The chapters now display at full size, with their points readable and expandable as before. The extraction coverage note and point counts at the top of the panel now scroll away with the list, leaving only the search bar pinned so more points fit on screen.

Wider regulatory context for each clause

When the AI judges a clause, it now also sees where that clause sits in its regulation: the chapter heading above it, the titles of the other clauses in the same chapter, and the titles of any sub-clauses under it. For example, when judging "Money Laundering" it can see that "Predicate Offences" has its own clause right next to it. This helps it keep each finding inside the clause's real scope instead of asking for content that belongs to a neighbouring clause. The headings are only used for orientation; the clause text is still the only thing judged. Administrators can see exactly what context was given for each clause in the pipeline panel and the browser developer console.

Evals: saved reference results per clause

Administrators can now save individual clauses from any analysis as reference results, called evals. Each saved clause keeps its verdict, gaps and actions, together with the exact AI instructions, the version of the analysis pipeline and the AI model that produced it. Saving the same clause again creates a new version, and the newest one becomes the reference by default; any earlier version can be made the reference again. On every analysis report, "Compare with evals" shows the analysis's clauses next to the reference clauses, lets you choose which ones to compare, and shows how many reached the same verdict and how similar their gaps are. The comparison is done by the platform itself, with no AI involved and no cost. Saved clauses are listed on the Evals page.

Analysis setup at a glance

Administrators can now see, for every analysis, which AI model, analysis pipeline version and AI instruction versions were used, both in a new column on the analysis list and at the top of each analysis report.

Better matching of abbreviations and synonyms

When a regulation uses an abbreviation such as "CDD" and your policy writes it out in full as "customer due diligence" (or the other way round, or uses a recognised synonym), the search for relevant policy sections now runs a second time with the wording switched. Previously the alternative term was only added at the end of the search, which often was not enough to surface a policy section that used only the other form. This means fewer clauses are judged without the policy section that actually covers them.

Versioned analysis pipeline

The steps that select which policy sections each clause is judged against are now versioned. The previous behaviour is kept as version 1 and the improved abbreviation and synonym search is version 2, which is now in use. Administrators can switch between versions in the analysis settings, every analysis records which version it used, and saved evals include it, so any change that makes results worse can be undone.

Being worked on

The full path from starting an analysis to the saved compliance result (status, confidence, gaps, and actions) was mapped in detail for the current hybrid analysis. No product behavior changed. The map is the baseline for any later change to how clauses are judged.

