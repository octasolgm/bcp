Weekly Update - 28 Sep 2026 to 04 Oct 2026

Role permissions for maker, checker and reviewer

Roles now work as a hierarchy instead of three separate lanes: a checker can also do everything a maker can, and a reviewer can also do everything a checker and a maker can, in addition to their own tasks. A super admin can still do anything. If someone tries an action their role does not allow, the message now says which role is actually needed instead of a plain "Forbidden".

Super admin status override

A super admin can now change a report's status directly from the reports list, for example moving it straight to "finalized" or back to any earlier stage, without going through the normal submit/approve/finalize chain. This asks for confirmation first, and every change is recorded in the report's history for the audit trail. A new "Finalized" tab was also added next to the existing queue tabs so finalized reports are easy to find.

Cleaner reports list

The action buttons on the reports list no longer stack into a tall column of full-width buttons; they now sit compactly side by side. Sidebar counts, like the internal documents count, now update immediately after approving, finalizing or sending back a report from the Checker or Reviewer queue pages, matching what already happened from the single-report review screen.

Rebrand to Comply Solutions

The platform now shows the "Comply Solutions" name and the new logo everywhere: browser tab, favicon, sidebar and the sign-in page. The build number shown in the sidebar now uses a short counter instead of a long timestamp when no source-control reference is available.

AI cost controls per business

Two new controls are available per business, independent of each other and independent of the platform-wide defaults:

- A credit limit: a hard cap on how many credits a business may ever use, shown in real dollar terms, or left unlimited.
- A margin override: a business can be billed at the platform's standard margin, or at its own specific margin.

The AI usage report now also shows, for every call, which AI model was used and through which provider it was reached (OpenRouter, Anthropic direct, Google direct, Azure OpenAI, xAI, DeepSeek, Moonshot or Qwen), with a new filter and breakdown by provider. Previously only the model was shown clearly; now both are, since the same model can be reached through more than one provider at different cost.

Azure Document Intelligence (OCR) costs are now included

Document text extraction through Azure Document Intelligence was not being counted anywhere before. It is now billed into the same cost and credit system as AI model calls, so it appears in every existing cost, usage and margin report going forward.

Action plan embedding fix

When a reviewer finalizes a report, resolved corrective actions are embedded into the finalized copy of the internal document. This previously waited until every corrective action on a gap was resolved before embedding any of them. It now embeds each resolved action as soon as it is resolved, even if another action on the same gap is still open.

Corrected document filename fix

Downloading a reviewer's finalized document previously always showed a generic name ("internal-document") instead of the real document's name, because of a browser restriction that blocked the real filename from being read after download. This is fixed, and the downloaded file now also carries a version marker (for example "(v2)") so two versions of the same document no longer look identical once downloaded.

Deployment

Published this week's changes to the hosted API and web app.
