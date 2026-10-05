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

