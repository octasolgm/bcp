Weekly Summary - 28 Sep 2026 to 04 Oct 2026

28 Sep 2026

Tasks
- Maker/checker/reviewer role hierarchy: a checker can now also do a maker's actions, and a reviewer can also do a checker's and a maker's actions, on top of their own
- Forbidden-action errors now explain what role is actually needed instead of a plain "Forbidden"
- Super admin can now force-change a report's status directly (draft/maker/checker/reviewer/finalized), bypassing the normal chain, with an audit trail entry and a confirm prompt before it applies
- Added a "Finalized" tab next to All analysis / Pending correction / Pending review / Pending final review
- Report actions column made compact and responsive (dropdown + button side by side instead of stacked full-width buttons)
- Sidebar counts (e.g. Internal documents) now refresh right after any status change made from the Checker or Reviewer queue pages, not only from the single-report review screen
- Rebranded to "Comply Solutions" with the new logo/favicon everywhere (browser tab, sidebar, sign-in page)
- Build number shown in the sidebar now uses a short 2-digit counter instead of a 6-digit timestamp when no git commit is available
- Per-business AI credit limit: set a hard cap on total credits a business may use, or leave it unlimited, shown in real dollars
- Per-business margin override: each business can use the platform's default margin or its own, set independently
- AI usage reporting now shows which LLM was used through which provider (not just OpenRouter — Anthropic, Google, Azure OpenAI, xAI, DeepSeek, Moonshot, Zhipu and Qwen are all tracked separately), with new "by provider" filtering and breakdown
- Azure Document Intelligence (OCR) usage is now billed into the same AI cost/credit ledger as LLM calls, so it shows up in every cost and margin report — previously it was not tracked at all
- Deployed the API and web app to bcp-api-dev / bcp-web-dev

Bug fixes
- Fixed the reviewer's document-embedding feature: a gap with two resolved-and-pending action plans now gets its one resolved action embedded into the corrected document right away, instead of waiting for every action plan on that gap to be resolved
- Fixed the downloaded corrected document always being named generically "internal-document" instead of matching the real document's name and new version — caused by the browser being blocked from reading the real filename cross-origin
- Corrected document downloads are now named with a version marker (e.g. "... (v2).pdf") so v1 and v2 don't look identical once downloaded
