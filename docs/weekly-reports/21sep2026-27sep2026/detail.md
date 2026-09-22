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

Being worked on

- Analysis with Kimi K3 currently fails because the Kimi account has run out of balance. It needs a top up, or another model can be selected in Admin settings.
- Clauses whose new AI call failed can still display an older verdict as completed. We plan to show them as failed.
- The regulation document list is still slow to open, because it makes many round trips to the remote database.

- Removing the old "Re-run forward" button from the result panel now that each clause has its own rerun.
- Adding resolved, unresolved and total counts, and collapsible acronym and synonym sections, to the Query Expansion Dictionary admin page.
- Agreeing with the client which documents to use for the next round of testing.
