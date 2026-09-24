Product Update - Week of 21 September to 27 September 2026

For posting to the client portal.

This week at a glance

The new hybrid analysis pipeline on the Regulation analysis page now runs end to end, from reading a clause through to a real AI judgment, and every step of it can be inspected on screen. We also added an admin switch to control prompt caching for this pipeline.

A complete, visible pipeline

Each clause now moves through nine steps: expanding acronyms and synonyms, splitting clauses that contain several separate duties, keyword search, meaning-based search, merging the two result lists, choosing how many policy passages to keep, building the context, asking the AI for a judgment, and saving the result. The number of passages kept is no longer fixed - it adapts to how many passages are genuinely relevant to each clause.

Clauses that list several obligations, such as (a), (b), (c) items or several separate "shall" statements, are now searched part by part, so each duty is matched against the policy on its own.

Judgment now uses the model you choose

The judgment step is live and uses the model selected in Admin settings. Each call records which model was used and how many tokens it consumed, so cost per clause and per run can be tracked.

A right-hand panel that shows the work

The panel on the right shows what each step produced for each clause, in clause-number order. It can be resized, and it now lists only acronyms and synonyms that were actually resolved, so it no longer fills up with unmatched entries. In the compare view, the regulation and policy columns can also be resized, and the points list shows compliance status and confidence. The full regulatory clause text is shown rather than a shortened version.

Prompt caching control

In V5, each clause pulls in a different set of policy passages, so caching them costs slightly more and never saves anything. Caching is therefore now switched off by default for V5. It is not removed: a new "Prompt caching (Analysis V5)" card on the Analysis prompts page turns it back on with one click if it is ever useful. The earlier analysis versions are not affected.

More AI providers to choose from

Kimi (Moonshot) and DeepSeek can now be selected as the analysis model alongside Google, OpenAI, Anthropic and xAI, and the model lists for the existing providers were brought up to date. A provider only becomes usable once its access key has been added on the server. On the Platform settings page, the main analysis model now appears first and is described as the model that runs every analysis page, while the dual verify model sits below it and is described as a second pass used only where dual verification is supported. We also fixed an issue where some newer OpenAI models would reject requests because of settings they do not accept.

Cost estimate for different AI models

We measured how many tokens the judgment step really uses for a clause (roughly 11,500 in and 1,300 out on average) and priced that against the published rates of the main providers. On the current model a typical clause costs about 9 cents, so a 30 clause report is under 3 dollars. Lower priced models can bring that to a few cents per report, but their accuracy on our documents has not been tested yet, so a comparison on the already verified documents is the next step. The full table is available as an Excel file that recalculates when prices or token counts are changed.

Fixes

- A regulation document could briefly show as "not parsed" in red before turning green. This is fixed.
- Clauses that had not been judged yet could show a false "1 gap" or "non compliant". They now show no verdict until judged.
- Clause text was cut short in the Regulatory Requirement panel. It now shows in full.
- Long regulation clauses, such as a clause containing a bullet list, were cut off after about 280 characters when reopening a saved document on the local Regulation Documents page, so "Show more" had nothing more to show. The full clause text now displays.
- Clause extraction could end a clause too early when the PDF wrapped a legal reference onto a new line, for example a line starting with "Article 4.1);". The reference was mistaken for a new heading, so the original clause was cut short and a made-up clause appeared in its place. On the CBUAE guidance, clause 3.1 grew from about 280 characters to its full text with all eight obligations, and five made-up clauses were removed. Extraction only reads the text that was already parsed, so re-extracting a document uses no parsing credits. The Azure pages and navigation group are now simply labelled "Azure". The semantic extract buttons were also hidden on the local document pages, since structural extract is the final method.
- When a document had a printed table of contents, each contents line was extracted as if it were a clause, so every clause listed there existed twice. On the analysis page this hid whole ranges of clauses from the point picker (for example 3.1 to 3.11 and 7.1 to 7.10 in the CBUAE guidance), and it would have spent analysis calls on contents lines. Contents pages and page-number-only entries are now ignored. For that document the count went from 143 entries to 94 real clauses, and all 93 distinct clauses now appear in the picker instead of 54. Documents extracted earlier need one free Re-extract to pick this up.
- After clicking Run on the new analysis page, the list of clauses being analysed could stay empty at 0 of 0 and the page could stay on "running" even after the run had finished, until the page was reloaded. The page now settles when the run finishes, including when some clauses failed, and shows the run the same way as a reopened link.
- The analysis page took a long time to show document status because every check downloaded the full text of every document. The status check now reads only the status, which is about 20 times faster.
- On the Azure Document Intelligence page, the Parse button and description wrongly said parsing was local. The button now reads Parse and the description explains that parsing runs on Azure while section extraction stays on our server.
- New analyses could stay stuck at 0 of N. They now start correctly.
- Opening an existing analysis could leave the pipeline panel on "Pending". It now loads each step's output.
- The per-clause "Rerun clause" button was missing for some clauses. It now appears.

Separate workspaces for each client

The platform now supports multiple workspaces, one per client organisation. Each bank gets its own space with its own users, departments, uploaded documents, regulation libraries and analyses. People inside one workspace cannot see anything that belongs to another, including in lists, dashboards, counts and direct links.

The platform super admin has a new Workspaces page under Administration. From there a new workspace can be created together with its first administrator in one step, and existing workspaces can be renamed, deactivated or reactivated. The page shows how many users, documents and analyses each workspace holds. The super admin can open any workspace with one click, or use the workspace selector in the top bar, and from then on every page shows that workspace's data.

Each workspace has its own Admin. A workspace admin creates and manages the users and departments of their own workspace and has the same day to day rights as before inside it, but cannot see other workspaces or change platform-wide settings such as the AI model, analysis prompts or the query dictionary. Those remain with the platform super admin.

Everything that existed before this change, including all current users, documents, analyses and the demo accounts, now sits in a Default workspace, so current users and the demo experience are exactly as they were. Deactivating a workspace blocks its users from signing in until it is turned back on, and no data is deleted.

We tested this with two separate test banks, checking that users, documents, departments, libraries and analyses created in one bank never appear in the other, that workspace admins cannot reach platform settings or other banks' users, and that demo and existing accounts see the same results as before. The test banks were removed afterwards.

More AI providers

Zhipu AI (GLM) and Alibaba Qwen can now be selected as the analysis model, alongside the providers added earlier this week. The cost estimate workbook now includes a sheet for these providers and a recommendation sheet.

Fixes

- The gap analysis Excel export now uses the correct file name prefix.
- The list of manually added regulation points is now kept separately for each workspace.

Faster document lists, and much lower database traffic

Sign-in stopped working because the hosting plan's monthly data allowance ran out. Investigating it showed the cause was in our own screens rather than in the amount of documents you hold: the database is only 72 MB, but the same content was being read out of it thousands of times.

Three screens were sending far more data than they displayed. The regulation document list read the entire parsed text and extracted clause data of every document each time it refreshed, to show cards that only display a name, a status and a count. The local document pages did the same on every status refresh while parsing or extracting was running. The analysis result screen repeated the whole analysis record once for every clause it loaded.

All three now load only what they show. A regulation list refresh dropped from about 409 KB to 4 KB, a document status refresh from about 852 KB to 2 KB, and the analysis screen no longer repeats the same record hundreds of times. The parsed text and clause lists are still there, they are simply loaded for the single document you open, and kept for the rest of your session.

Parsing and extracting documents themselves were never the problem. Reading a file to parse it accounted for about 1 percent of the month's usage, and saving results costs nothing against the allowance, so you can keep parsing and extracting as many files as you need.

The sidebar counters also stop refreshing while a browser tab sits in the background, so a window left open overnight no longer keeps working.

Service will come back once the hosting plan is adjusted. These changes mean the next period starts with a far smaller footprint, and the screens themselves are quicker to open.

AI credits per client

Each workspace now has its own prepaid AI credit balance, so AI spend can be controlled per client instead of across the whole platform.

Every AI call made during an analysis is recorded against the workspace that made it, together with the model used and the number of tokens. When calls go through OpenRouter the exact cost of the call is returned by the provider and used directly, so the figures are real rather than estimated. Calls made with a direct provider key are priced from a model price list instead.

From the Workspaces page the super admin can see each client's remaining balance at a glance, open a panel showing spend by model and the full history, add credits with an optional note, and choose the level at which the client is warned (80 percent used by default). Removing credits is possible too and is recorded as an adjustment, so the history always explains the balance.

A separate AI usage page covers every business at once. It can be filtered by business, by period (last 7 days, last 30 days, this month, all time, or a custom date range), by model, by the activity that spent the credits, and by entry type, and it shows the totals for the chosen filters along with a breakdown per business and per model. Clicking a business narrows the whole view to that client, and the Credits panel on the Workspaces page links straight into it.

Each business has an AI credits page of its own showing what is left, how much has been used, a progress bar and recent activity. They see credits only; our own provider costs are never shown to them.

When a workspace runs out of credits, starting a new analysis or a rerun is refused with a message asking them to contact their administrator. Analyses already running finish normally so no work is lost, and demo accounts are untouched since they never call AI at all.

Workspaces that have never been given credits stay unlimited, so nothing changes until credits are handed out for the first time.

Being worked on

- Analysis with Kimi K3 currently fails because the Kimi account has run out of balance. It needs a top up, or another model can be selected in Admin settings.
- Clauses whose new AI call failed can still display an older verdict as completed. We plan to show them as failed.
- The regulation document list is still slow to open, because it makes many round trips to the remote database.

- Removing the old "Re-run forward" button from the result panel now that each clause has its own rerun.
- Adding resolved, unresolved and total counts, and collapsible acronym and synonym sections, to the Query Expansion Dictionary admin page.
- Agreeing with the client which documents to use for the next round of testing.

- Rolling out the workspace update to the hosted environment. Real client workspaces should be created only after that, because the currently hosted version does not yet separate workspaces.
- Confirming that the shared acronym and synonym dictionary used to improve search can stay common to all workspaces, as it holds terminology only and no client documents.

- Restoring service requires a change to the hosting plan; the reductions above apply from now on but cannot recover the allowance already used this period.

- Embedding calls used by document indexing are not yet counted against AI credits; only analysis and judgment calls are.

Choosing and seeing the AI model

You can now pick from many more AI models in Admin, including a group of free models for low cost testing. The analysis results page shows which model judged the analysis, and the exported Excel gap analysis includes an "AI model" column so the source of each result is clear. Exported files are now named "comply-solutions-...". Free models are suitable for trials only, as their providers may keep prompts.

Fixed

Analyses of the CBUAE regulation against the AML manual could show the fixed sample answers used in demonstrations instead of the AI model's own result. Live analyses now always show the model's real output, and demonstration accounts are unaffected.

Being worked on

Free models give different verdicts from the paid ones on the same clause, so we are comparing their quality before recommending them for real use.


Evidence quotes on the new analysis page

On the new analysis page, every AI model now returns the exact passages from the internal policy that support its verdict, together with the document section and page. Previously one of the models could return a verdict with only page references. Other analysis pages are unchanged. While checking this we found that one internal document had been uploaded under the wrong name; after re-uploading the correct manual, clause 3.1 is judged against the right text.

Consistent gaps and action plans

On the new analysis page, a clause judged Compliant is now shown with no gap and no action plan, because a fully compliant clause has nothing to fix. A clause judged Partial or Non-compliant always shows both the gap and an action plan for closing it. Previously one model could list minor observations and a corrective action under a Compliant verdict.

Costing and margin

Platform administrators can now set the price of an AI credit and the markup on the real AI cost in one place, with an example that updates as the numbers change. Each business shows its balance in dollars, what the AI cost us, what the business was charged and the margin we keep, and the AI usage page shows the same by business, by model and for every call, highlighting when the margin falls below a chosen level. Businesses see their credits and what those credits are worth in dollars, but never our provider cost. Because each call is charged from its real cost, a price increase at an AI provider is passed through automatically and the margin percentage stays the same. We also fixed two problems: AI calls made through OpenRouter were not being counted, and a saved credit price was not being applied. A written costing guide explains the rules and day-to-day management.
