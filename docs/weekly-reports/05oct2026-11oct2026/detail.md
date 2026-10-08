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

We reviewed why the money laundering definition clause produced a different set of gaps after the latest analysis changes. The overall verdict was the same in both runs; the difference came from how strictly the AI treated definitions and example lists in the regulation, and from a larger amount of policy text being sent for review. Improvements to make these results consistent are being prepared.

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


Why some gaps are reported for content your policies already contain

We reviewed the whole analysis process step by step, from reading your documents to writing the final policy text back into them, using the money laundering definition and the reporting protection clauses as test cases. We found that part of a long clause was not being used when searching your policies, so a requirement such as "the amount, timeframe and form of the funds do not matter for reporting suspicion" was checked without the policy passage that covers it. We also found that the search for matching policy text can be made much more precise, that the AI is given a large amount of loosely related text instead of focused evidence for each requirement, and that some first-draft actions on older analyses still carried generic sample wording, such as a request for a reporting deadline the regulation never asked for. A detailed improvement plan is ready: each clause will first be broken into its exact requirements, each requirement will be searched for separately, and every reported gap will be double-checked against all of your documents before it is shown. The first, smaller fixes are ready to start once approved.

We also prepared a plain overview of every step of the analysis, from reading a document to producing the corrected copy, showing which steps run on our own platform at no charge and which use paid services (document reading, and the AI review of each clause), with the expected cost per clause today and after the planned improvements. The planned changes make the review more accurate and also lower the cost per clause, mainly because the AI will read focused evidence for each requirement instead of a large amount of loosely related text.

A step-by-step improvement plan has been prepared, listing each issue, where it occurs, the change, what it fixes and how it affects the cost of an analysis. As part of this, the analysis will no longer cap how many parts of a clause are searched or how many relevant policy sections are reviewed: relevance alone will decide, so that no requirement is judged without the policy text that addresses it. We also found that policy sections matching a clause's exact wording, rather than its general meaning, were usually left out of the review; this is included in the first set of fixes.

First round of analysis improvements ready for testing

The first set of fixes from the improvement plan is built and can be switched on by an administrator as a new analysis setting, while the previous behaviour stays available for comparison. Every sentence of a regulation clause is now used to search your policies; previously the end of long clauses could be left out, which caused requirements such as "the amount, timeframe and form of the funds do not matter" to be reported as missing. Policy sections that match the clause's exact wording are now kept for the review instead of being set aside, and there is no longer a fixed limit on how many relevant sections are reviewed: relevance alone decides. Repeated page headers are removed from document text so they no longer distort the search. Abbreviations picked up automatically from documents now wait for administrator approval before they are used, and a few overly broad synonym pairs were retired. On analyses created before the AI's own corrective actions were introduced, administrators can replace leftover sample action wording with the AI's actions in one click.

The clause view in the gap report now shows each clause's title next to its number, and the corrective action plan has a single Expand all / Collapse all control for every gap and action.

Accuracy review of the first test

We checked every result of the first test analysis line by line against the original internal documents. The overall verdicts were right for all three clauses, but two of the five reported gaps were not real: the policies already cover virtual assets and other non-cash assets, and they address the duration of suspicious activity, using different wording from the regulation. We traced the causes: long policy sections were only partly visible to the search, different wording for the same idea was not linked, some evidence was credited to the wrong document, and one document had been uploaded twice. A focused improvement plan is ready, starting with a free check that confirms the right policy text is found before any paid AI review is run, so each fix can be verified at minimal cost.

Accuracy improvements ready for testing

Policy documents are now searched in short passages, each labelled with the headings it sits under, instead of whole chapters, so text in the middle of a long chapter or annex (for example a typology on virtual currencies) is found. Different words for the same idea, such as "timeframe" and "duration of the activity", are now linked. An optional second check looks for every reported gap again across all documents and asks the AI whether any passage already covers it, removing gaps the documents do address. Updated review instructions help the AI recognise definition clauses that a policy applies in practice and avoid reporting one missing idea as several gaps. Administrators also have a free retrieval check that shows, before any paid AI review, whether the right policy text is found for each clause.

Stricter review of definition clauses

The first test of the new review settings on the money laundering definition clause returned "compliant", which was too generous. The regulation gives the legal meaning of "funds" and "proceeds", and none of the internal documents state or refer to those definitions; the review accepted examples of laundering through goods and assets as if they were the definitions. It also accepted "the amount, timeframe and form of the funds do not matter" as covered when the quoted policy text only addresses the amount. A corrected set of review instructions is ready for testing: a term the regulation formally defines now counts as covered only when the policies state that definition or adopt the law's definition, and a requirement with several parts counts as covered only when each part is supported by quoted policy text. The second check of reported gaps follows the same rule.

Faster first analysis on existing documents

The improved search reads documents in short passages. For documents uploaded before this change, those passages are now prepared automatically in the background, so an analysis no longer has to wait for them. When an analysis does need to prepare them, the progress panel now says so instead of showing every step at 0%.

Easier troubleshooting of an analysis

Administrators can now copy a complete plain-text report of how a clause was analysed, straight from the browser: which parts of the clause were searched, which policy passages were found and sent to the AI, which review instructions were used, what the AI answered, and how each reported gap was double-checked. This makes it quick to share exactly what happened on a run when a result needs to be questioned. The corrected review instructions were also checked against the expected result of every test clause before the next test run, and one conflict that could have caused a new false gap on the reporting protection clause was removed.

Corrective action plan matches each gap again

On the latest test, the money laundering clause was correctly reported as partially compliant, with the missing definitions of "funds" and "proceeds" as the main gap. Two display problems were found and fixed: when the review numbered its gaps by requirement, the drafted actions could end up under the wrong gap, and gaps the review rated as low risk were shown as medium risk with a 30-day due date. Each gap now carries its own drafted action and the risk level the review assigned, and very long quotations of the regulation inside a gap are shortened so the gap reads as a clear statement.

Choice of search model and a clearer progress panel

Administrators can now choose the model behind the meaning-based search of policy passages: the free model that runs on our platform, or a stronger Azure OpenAI model at a cost of well under one cent per document. Switching takes effect without re-reading or re-processing any document; passages are refreshed in the background. How documents are read and divided into sections is unchanged. On the analysis page, the progress panel no longer shows the search steps as finished for a moment when a new analysis starts, and it now follows the analysis, bringing the step in progress into view.

We also re-checked the remaining money laundering gap about the timeframe of transactions against the policy text. No policy document states that the timeframe does not matter for suspicion, so the gap is correct; the transaction reporting guidance only partly touches on it.

The stronger search model is now the default for the policy passage search. Documents do not need to be read or processed again: only the search index is refreshed in the background, and how documents are divided into sections and passages stays exactly as before.

The latest test of the money laundering clause gave the expected result: partially compliant, with three low-risk gaps (the definitions of "funds" and "proceeds", and a statement that the timing of a transaction does not affect reporting), each with its own drafted action and a low-risk due date. The detailed step-by-step report of an analysis can now be downloaded as a text file from the progress panel.

The step-by-step report of the latest test showed three further improvements, now made: when policy text partly addresses a gap (for example the guidance on reviewing a longer period of transactions for the timeframe point), the gap now shows that text instead of ignoring it; the search is faster with the stronger search model; and two incorrect abbreviation entries no longer distort the search.

