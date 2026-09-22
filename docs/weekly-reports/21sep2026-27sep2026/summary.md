Weekly Summary - 21 Sep 2026 to 27 Sep 2026

21 Sep 2026

Tasks
- Built the full step-by-step V5 analysis pipeline on the new Regulation analysis page: query expansion, sub-obligation split, keyword search, embedding search, fusion, adaptive selection, context build, LLM judgment, save
- Sub-obligation split: clauses with (a)/(b)/(c) lists or several "shall" duties are searched part by part
- Fusion and adaptive select: keyword and embedding results are merged into one ranked list and the number kept adapts to the clause (no fixed count)
- LLM judgment is now live for V5 and uses the model chosen in Admin settings
- Right pipeline panel now shows the output of every step per clause, sorted by clause number, resizable, with only resolved acronym/synonym matches shown
- Token use and cost are logged per call (model, input, output, cache write, cache read)
- Compare view: regulation and policy extract columns are resizable
- Points list and detail now show compliance status, confidence and the full regulatory clause text
- Prompt caching for V5 is now off by default, with an admin switch (Analysis prompts page, Regul workflow tab, "Prompt caching (Analysis V5)") to turn it back on; V4 is unchanged
- Built an LLM cost estimate workbook (real tokens per clause vs list prices for Anthropic, OpenAI, Google, xAI, Kimi, DeepSeek) with a shortlist of models to test
- Added Moonshot Kimi and DeepSeek as selectable AI providers and refreshed the model lists for Google, OpenAI, Anthropic and xAI
- Platform settings: main analysis model now sits on top (runs all analysis pages), dual verify below (only where supported)
- Wrote pipeline status and pipeline-vs-diagram docs in docs/pipeline

Bug fixes
- Fixed TFS Guidelines (v12) flashing red "not parsed" before turning green
- Fixed false "1 gap" and phantom "non compliant" on clauses that had not been judged yet
- Fixed clause text being cut short in the Regulatory Requirement panel
- Fixed unresolved acronyms/synonyms being listed and counted as matches
- Fixed new analyses staying at 0/N because clauses were never confirmed before start
- Fixed the pipeline panel staying on "Pending" when opening an existing run
- Fixed newer OpenAI models failing because of unsupported request settings
- Fixed long regulation clauses being cut off after 280 characters on the local Regulation Documents page
- Fixed clause extraction splitting a clause early when a wrapped line began with a reference like "Article 4.1);" (created fake clauses and cut long clauses short); added automated tests for it
- Fixed clause extraction treating a printed table of contents as clauses (duplicate clause numbers, points missing from the analysis point picker, wasted analysis calls); the V5 page now also keeps the fullest clause if a number repeats
- Sped up the analysis page: document readiness checks now read statuses only (about 0.5 s instead of 11-19 s per call)
- Fixed the V5 analysis page losing its clauses after Run: it now settles as complete even when clauses failed, and reloads the run the same way a reopened link does
- Renamed "Azure Document Intelligence" / "Azure DI" to just "Azure" in the page labels and navigation
- Hid the semantic extract buttons on the local Regulation and Internal document pages (structural extract is the final method)
- Fixed the Azure DI Internal Documents page labelling its Parse button and description as local
- Fixed the per-clause "Rerun clause" button not appearing for V5 clauses

Investigated, follow up pending
- Kimi K3 account has insufficient balance, so analysis with Kimi fails until it is topped up or another model is selected
- Clauses whose new AI call failed still show an older verdict as completed on the V5 page
- The regulation document list still takes 8 to 14 seconds to load (remote database round trips)
- Remove the "Re-run forward" button from the result panel (per-clause rerun now exists)
- Query Expansion Dictionary admin page: resolved/unresolved/all counts and collapsible acronym/synonym sections
- Confirm with client which documents to use for testing (TFS, CBUAE_EN_3945_VER2, Internal AML Manual 290626)

22 Sep 2026

Tasks
- Added Zhipu AI (GLM) and Alibaba Qwen as selectable AI providers, and added a Chinese LLM sheet and a recommendation sheet to the cost estimate workbook
- Multi-workspace support (branch feature/multi-workspace): one workspace per client bank, each with its own users, departments, documents, libraries and analyses, fully isolated from other workspaces
- Platform super admin gets a new Administration > Workspaces page: create a workspace together with its first admin, rename, deactivate/activate, see user/document/analysis counts, list members, and open any workspace
- Workspace switcher in the top bar for the platform super admin; everyone else sees their workspace name
- New "Admin" role per workspace: manages that workspace's users and departments only. Platform-wide pages (Platform settings, Analysis prompts, Query dictionary/synonyms, Demo group, Workspaces) stay with the platform super admin
- All existing data, users and demo accounts moved into a "Default workspace" automatically, so nothing changes for current users or the demo
- Deactivating a workspace blocks its users from signing in until it is activated again (no data deleted)
- Tested end to end with two test bank workspaces (71 API checks and 16 new unit tests, all passing) and compared old vs new responses for demo and existing accounts (identical); test workspaces, accounts and files were removed afterwards

Bug fixes
- Fixed the gap analysis Excel export filename (comply-solution- to comply-solutions-)
- "Manual custom points" is now one per workspace instead of one shared list

Investigated, follow up pending
- The deployed dev site still runs the pre-workspace version against the same database; deploy this branch before creating real client workspaces, otherwise the old version would show every workspace's data to everyone
- The query expansion dictionary and synonyms stay shared across all workspaces (terminology only, no client documents); confirm this is acceptable
- 21 existing automated tests already fail on the branch this work started from (not related to workspaces); worth a separate clean-up

