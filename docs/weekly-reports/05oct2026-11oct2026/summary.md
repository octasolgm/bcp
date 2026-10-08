Weekly Summary - 05 Oct 2026 to 11 Oct 2026

05 Oct 2026

Tasks
- Regul forward judgment prompt v7: document_reference must be one string (several labels separated by "; "), gap_description and suggested_action must be single strings; startup seeder sets v7 current on next API restart
- New Analysis (hybrid V5) pipeline panel: each step shows a progress bar (N / M clauses, %), Step 8 also shows elapsed time and estimated time left
- Pipeline panel shows the full text of every retrieved chunk (loaded when a clause is expanded) instead of a 400 character preview
- Forward judgment saves each clause as soon as its AI call finishes, so clauses complete one by one in the UI instead of all at once at the end
- Retrieval (Steps 1-6) saves per clause so the panel can show retrieval progress
- Forward judgment runs 8 clauses in parallel by default (was 4); reasoning effort and gap/action retries unchanged
- Real accounts: first-draft action plans now use the AI's own numbered corrective action for each gap; keyword templates kept for demo accounts only

Bug fixes
- Multi-clause runs: clauses failed with "JSON value could not be converted to System.String. Path: $.document_reference" when the model cited several documents as a list; parser now accepts a list for document_reference, gap_description, suggested_action and interpretation
- Action plans showed canned text unrelated to the clause ("Define the escalation path and reporting deadline...") with the gap cut off at "(e."; replaced by the AI's own action on real accounts
- Action plans were saved twice when the analysis page and the gap panel seeded the same clause at the same moment; seeding is now serialized per run
- Retrieval failure no longer silently continues with an empty context (which would report false gaps); the run now fails with a clear error
- Hybrid V5 judgment was sent the full parsed text of every internal document (about 534k chars, ~133k tokens per clause) instead of the clause's retrieved chunks; context is the retrieved chunks again (main cause of ~3 min per clause)
- Clauses judged compliant were silently downgraded to "partial" with gap "N/A" when a quote could not be matched against the chunk text; V5 now drops unmatched quotes, checks quotes against the full parsed documents too, and only downgrades when no evidence verifies, with an explicit verification gap and action
- V5 gaps written as "[1] ... [2] ..." were treated as one gap, so only the first action plan was created; each numbered gap is now its own gap with its own action plan(s)
- "Fulfilled clauses" always showed "None" on partial clauses; now lists the covered requirements with their evidence

- Re-running a single V5 clause started the legacy Landing AI section parse of the internal documents (a separate paid service V5 never uses); clause reruns on V5 now skip it, matching full runs
- AI call log: each clause's Step 7 context, every Step 8 AI call (request and raw response, timing, errors) and the saved result are stored per run (new workspace-scoped table) and shown in the pipeline panel and the browser DevTools console; Steps 1-6 per clause are also printed to the console and the API log

- Rerun all (V5) also started the legacy Landing AI parse and reused stale retrieval; it now follows the new-analysis flow (index check, Steps 1-6 again, judgment) and records errors on the run instead of leaving it stuck "running"
- Clause rerun (V5) reused the clause's old retrieval and did not record AI usage or check the credit limit; it now re-runs Steps 1-6 for that clause, is billed as "Clause rerun" and checks credits first
- Retrieval loaded a document once per OCR engine it was indexed under, which could repeat the same policy text in the AI context; now one index per document (Azure DI first)
- Internal documents that were never indexed were silently left out of V5 retrieval; new analysis, rerun all and clause rerun now parse, extract and index them first (same Azure DI path as gap evidence)

- Performance: each prompt read re-ran the full prompt-version seeding check (~30 database queries, 3 times per clause); now seeded once per API process, at startup
- Performance: Step 7 re-downloaded the full parsed text of every document for every clause; now loaded once per run
- Performance: Step 4 pulled up to 300 full section texts from the database per sub-obligation although they were already in memory; now ids and distances only
- Performance: Step 1 re-read both dictionary tables per sub-obligation; now once per run
- Performance: Internal Documents list made 2 database calls per document; now only for documents still processing (10 s to about 3 s)
- Measured on clauses 3.5 + 3.6 (5 internal documents): start to AI call 86 s to 29 s; whole run 4m17s to 2m51s with Kimi K3; 42 s with Claude Sonnet 5

- Analysis report now shows which AI model judged the run under the title (the existing chip sat in a hidden bar); rerun all records the model used
- AI judgment calls no longer fail a clause on the first bad delivery: an answer cut off at the token limit (now detected explicitly), unreadable JSON or a provider error re-sends the same request up to 3 times, each try logged; token usage log now includes reasoning tokens

- Kimi K3 output ceiling raised from 32,768 to 131,072 tokens (model limit on OpenRouter is ~943k) so long reasoning cannot cut the answer off; billing is per generated token, so no extra cost on normal clauses

Investigated, follow up pending
- Clause 3.5, v8 vs v9 gap difference (runs e5aff65b vs 0603b412): both AI verdicts were partial (v8 only shows compliant because its actions were resolved). Evidence was the same, including the crypto/property/precious-metal typologies (Annex 1 B.3, B.4, B.18); v8 accepted them for the asset-scope element, v9 required an express scope statement. Found: v9 supporting context never reached the AI on the V5 new analysis page (analysis points have no regulation point link), so that run was effectively v8; pipeline v2 also changed at the same time (context 148k -> 211k chars, 40 -> 55 sections, ~20k chars of duplicate Document.pdf / Implementation Manual text); the prompt has no rule for definitional/interpretive clauses or illustrative "such as" lists. Fixes proposed, awaiting decision
- Model comparison on 3.5 + 3.6: Kimi K3 (high reasoning) more precise and follows the v8 rules better but varies between runs and takes 40-170 s per clause; Claude Sonnet 5 about 23 s per clause with schema-enforced output but flags contextual statements as gaps and cites less evidence. Decision on judgment model (and possibly testing Claude Opus 5) pending

Tasks (continued)
- One V5 workflow at every entry point (new analysis, rerun all, clause rerun, gap evidence re-check all / one clause): index check, Steps 1-6, Step 7 context, Step 8 judgment, with the AI call log tagged by entry point; gap evidence re-checks also log the evidence-check AI call, and the gap report prints them to the DevTools console when a re-check finishes
- Regul forward judgment prompt v8: covered_elements field, every requirement accounted for as covered or missing, one gap line per missing requirement, several action lines per gap when needed, status and gap list must agree, rules for legal definitions vs contextual statements, verbatim quotes from one excerpt only; startup seeder sets v8 current
- Verified run a7e51682 against the 5 parsed internal documents: clause 3.5 gaps (no funds/proceeds definitions; timeframe and asset form not stated as irrelevant) are accurate; clause 3.6 was a post-processing artifact (see bug fixes) and needs a rerun

Tasks
- Internal Documents catalog shows a version pill (v2, v3, etc.) on finalized corrected copies and on any upload with version above 1

Bug fixes
- Finalize corrected documents often had no embedded resolved actions when retrieval or document_reference did not map a clause to an internal doc; single-document runs, policy-quote matching, and clause-level finding fallback now produce embed targets so PDF/DOCX copies include policy notes
- Finalize embed still empty on multi-doc runs: resolver now reads Azure/Landing parse cache markdown (not only local extraction), does not require a gap roster row, falls back to the first selected internal doc, and finalize/regenerate toasts report resolved-action vs embed-target counts
- Finalize embeds: notes now go only into the documents the AI cited as evidence for the clause (then documents containing its quotes, then the single best search match); an action that names a document ("Amend the AML Manual ...") goes only into that document; removed the fallback that dropped notes into the first selected document; the same note was previously copied into 3 documents
- Finalize embed text: the AI rewrite was failing with OpenRouter 402 (balance could not cover the 131k max_tokens reservation), so every note fell back to the raw action text cut at 480 characters; Kimi cap set to 65,536, a 402 now retries once with the affordable amount, and the fallback uses the full draft wording inside the action's quotes instead of a cut-off copy of the action
- Internal Documents: finalized copies list each embedded note (clause, gap, page, action and the embedded policy text) under the title; version history records the same detail
- Internal and Regulation Documents tables: Document column pinned left and Actions pinned right while the middle columns scroll (wide screens), row checkboxes with select all, and a bulk bar (Parse, Extract, Download, Delete) for the selected documents
- Internal Documents analysis group header: History button lists the latest finalized copy of each document with the clause, gap, page, action and embedded text of every note; document count shown as a filled pill
- Fixed a template error from the earlier embed list change that stopped the web build (nullable embed list); embed note headings use plain hyphens so PDF fonts render them; outdated embed tests updated (21/21 pass)
- Verified "new analysis 2": its corrected copies dated 30 Sep / 1 Oct were made before the embed fixes; after regenerating, the AML Manual copy (v15) contains the 3 resolved clause 3.3 actions as policy text on new pages after page 40
- Embed history opens in a right-side drawer instead of an inline row, and each embedded note now shows the gap it closed, the action, the inserted text, the page and who resolved it (gap text recorded for copies generated from now on)
- Catalog tables: Document column fixed at a comfortable width with padding and wrapping (titles no longer spill into the next column); Actions column capped at a fixed width with extra buttons wrapping, instead of every row reserving the widest row's width; spacing between History and Download all
- Finalize on an already finalized run showed "Run is not ready for final review": the page offers super admins a re-finalize but the API only accepted runs approved by the checker; a super admin can now finalize again (run stays finalized, corrected documents regenerated, logged as a finalized review noting the re-finalize), and other cases get a clearer message ("already finalized" / "checker has not approved it yet")
- Finalize embeds repeated content: each note's AI rewrite was given the clause's whole gap list, so every note restated all gaps (clause 3.5 got 3 near-identical notes); the rewrite now receives only its own action's gap, and each note heading names its gap ("Regulatory Clause 3.5, Gap 1")
- Finalize embeds now go after the section the action names ("Section 7.7", "Definitions section"), found in the document's own text with table-of-contents lines skipped, instead of all notes of a clause going after the clause's first evidence page
- Finalizing a run that was sent back to review and approved again created no new corrected files (a duplicate guard skipped documents the run had already produced) while still paying for the AI note writing; every finalize now produces a new version of each corrected copy, and the AI note writing only runs for documents that get a new copy
- Catalog tables: row checkbox sits in its own gutter so long document titles wrap beside it instead of dropping below it
- Finalize embeds: a note could still restate other gaps (clause 3.5 gap 1 also defined "Proceeds" and the timeframe rule) because the gap text was cut from the clause's action list using "(n)" numbering while the AI numbers items "[n]", so the whole list was used; each note now gets its own numbered line from the judgment's gap list

07 Oct 2026

Bug fixes
- Startup database update failed on every restart after the clause evals table was added (raw SQL read the empty-JSON default as a placeholder), so the clause evals table and the earlier-run setup fill-in never ran; fixed, 65 earlier runs now have pipeline and prompt versions recorded
- Analysis prompts page: the version list items on the right shrank and clipped their titles ("v9 · v9 - v8 rules plus..."); items now keep their full height
- Regulation Documents: opening a regulation showed its chapter groups as thin empty bars with no points; the groups were shrinking to a few pixels inside the scrolling panel and their content was clipped. Groups now keep their full height, so chapters and points show again
- Regulation Documents points panel: the coverage note and point counts now scroll with the points instead of staying fixed above them, so only the search and expand/collapse bar stays pinned and more of the list is visible

Tasks
- Regul judgment prompt v9 (system, user 1, user 2) set current: v8 rules plus a supporting regulatory context block ({clause_context}) with the clause's parent heading(s), every sibling clause at the same level (the judged clause marked) and its sub-clause headings, taken from the regulation's extracted points; the system prompt says the headings are for scope only, never requirements or evidence. Same retrieval, same AI calls, same workflow at every entry point (new analysis, rerun all, clause rerun, gap evidence re-check)
- Supporting context is recorded on each clause's Step 7 trace (new columns clause_context, clause_context_sent), shown in the pipeline panel under Step 7, printed in the DevTools console with the clause's AI calls, and written to the API log
- Clause evals (platform super admins, real accounts only): new workspace-scoped table nd_clause_evals replaces the first run-level eval table (dropped only if empty); "Save clauses as evals" on an analysis report saves ticked clauses, each as the next version for that clause (3.5 v1, v2, ...) with verdict, confidence, gaps, actions, covered elements, evidence, and the prompt versions (with text), pipeline version and AI model read from the clause's own AI call log; the newest saved version is current by default, one current version per clause
- Evals page (Analysis > Evals): one row per clause with its current version, expand to see every version, view the result and prompt texts, set current, delete (deleting the current version promotes the newest remaining one)
- "Compare with evals" on every analysis report: two columns (this analysis's clauses on the left, current clause evals on the right) with checkboxes, matching clauses pre-ticked; comparison is local and rule-based (verdict match, gap counts, gap wording overlap), no AI call and no cost
- Analysis list: "AI setup" column (AI model, pipeline version, prompt versions) for platform super admins; analysis report header shows pipeline and prompt versions next to the AI model, read from the clause AI call log (older runs show v1 and the versions found in their log)
- Runs now record the prompt versions of their last full judgment pass (analysis_runs.regul_prompt_versions)
- Earlier analyses filled in at startup (once, demo runs untouched): pipeline v1 for every run that called the AI, and prompt versions from the run's AI call log, or for runs without a log the versions that were current when the run was created
- Unit tests for the context hierarchy, prompt version matching and the comparison (11 new, all pass)
- Retrieval pipeline versions: v1 = the pipeline as it was (expansion terms only appended to the clause text), v2 = v1 plus a second BM25 + embedding search per sub-obligation with every matched acronym/synonym swapped for its counterpart (CDD <-> customer due diligence), so sections written in the other form are found; admin setting on Analysis prompts (default and current v2); each clause's retrieval record and the run store the version used
- Pipeline panel and DevTools console show the pipeline version, the expanded wording that was searched, and which BM25/embedding matches came from it; evals store and compare the pipeline version per clause
- 5 new unit tests for the rewording (both directions, whole-word acronyms, synonyms, no chaining)

Investigated, follow up pending
- Checked whether query expansion reaches Steps 3 and 4: it did, but only as a few words appended to the clause, which barely moves BM25 or the embedding, so a section using only the other form (full form vs acronym) could be missed; fixed as pipeline v2 above
- Traced the full real-account compliance analysis path (create run, confirm clauses, hybrid retrieval, one judgment call per clause, post-processing, saved status/confidence/gaps/actions) so later changes start from a written map of the current workflow


