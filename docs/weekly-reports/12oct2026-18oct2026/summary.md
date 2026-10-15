Weekly Summary - 12 Oct 2026 to 18 Oct 2026

12 Oct 2026

Tasks
- Client-facing process overview of the compliance analysis (docs/pipeline/REGUL-V5-PROCESS-OVERVIEW.md): document preparation (upload, parse, sections, search passages, dictionary), the analysis steps 1-9, review, export and finalize, versions and limits
- Demo plan for this week's product demo (flow on clause 3.5, preparation checklist, limits to state)
- Feed update listing every fix and feature of the V5 work with problem, cause and fix
- Sound when an analysis finishes: admin setting Off / Chime only / Chime and summary; the browser plays a chime and can read out the status and gap counts (no AI, no credits); demo accounts unaffected
- Production build and ZIP deploy to Azure: bcp-api-dev (deploy version 2026.10.12.02) and bcp-web-dev (2026.10.12.04); API app settings synced from local Development config; API health startup reports ready

Bug fixes
- None

13 Oct 2026

Tasks
- Temporary "Test finish sound" button on the analysis report, to test the finish notification on an existing analysis
- Run guide from upload to export, and client version of the improvements update
- Billing > AI usage page for each workspace admin: credits used, billed amount, balance, breakdown by activity and model, and the activity list, filtered by period, model, activity and type; always limited to the admin's own workspace and never shows provider cost or margin (platform super admin page unchanged, demo accounts hidden)
Bug fixes
- New analysis page: a regulation's point count showed the older stored count first (1, then 96); every clause was ticked when the points loaded; the run counter showed 0/85 for one ticked clause. The page now loads the real clause list straight away, starts with nothing ticked, and counts only the ticked clauses (demo accounts unchanged)

Investigated, follow up pending
- Clause 3.5 came back compliant in the new demonstration workspace but had 3 gaps in Default: the new workspace's admin email contains "demo" (demonstration.com), so the account counts as a demo account and its runs replayed the fixed demo answer instead of running the real analysis. Fix is to use an admin email and name without "demo". The points showing "done" early and missing clause headings in that workspace are likely the same cause, not yet confirmed

14 Oct 2026

Tasks
- Gap evidence re-check panel shows a loader while it runs: spinner on the active stage, the phase line and each running clause, plus a progress bar (moving until the first clause finishes, then filling as clauses complete)
- Exports include the gap evidence re-check results: Excel gets two new sheets, "Evidence re-check" (per gap: result, what the evidence covered, what is still missing, the quotes found with document/section/page, evidence documents, clause status before and after, date, who) and "Re-check actions" (per action: result, part done, part still to do, resolved / split / unchanged, reason); the PDF lists the same under each clause. Export dialog has an "Include sheets" option for them. Checked by running both exports on sample data
- Export Policy Extract (new analysis pipeline, real accounts): each document reference is followed by its quote, as on the gap analysis page, instead of numbered points; the newest evidence re-check's quotes follow under "New evidence" with document, section and page. PDF no longer repeats the references separately
- Production build and ZIP deploy to Azure: bcp-api-dev (2026.10.14.01) and bcp-web-dev (2026.10.14.02); API settings synced; startup health ready
- Production build and ZIP deploy to Azure of the AI usage, re-check loader and export changes: bcp-api-dev and bcp-web-dev both on 2026.10.14.05; API settings synced (18); startup health ready
- Another dev deploy: bcp-api-dev ZIP retry (2026.10.14.05) after upload network errors; bcp-web-dev (2026.10.14.06)

Bug fixes
- AI usage: Azure Document Intelligence parsing showed as model "114 page(s)" with activity "Other". New parses are recorded as "Azure Document Intelligence (layout)" with activity "Document parsing" and a note with the page count and file name, shown on both usage pages
- Gap evidence re-check: "localhost refused to connect" during "Rerun all gaps" was the local API having stopped (it had been started from the in-app browser panel, which stops it when the panel closes); API and web now run from the Terminal panel instead. The interrupted re-check is closed as "interrupted, run it again" after 3 minutes

15 Oct 2026

Tasks
- Deployed the OpenRouter data protection (zero retention and no training on every request) to the dev API: bcp-api-dev 2026.10.15.01, startup ready; web portal unchanged. Account privacy toggles for training set off by the owner; zero-retention account toggles still to be switched on
- Data-protection changes for AI and document services: every OpenRouter request now carries zero-retention and no-training rules (on by default, configurable; tested on the whole model list - Qwen and the free models cannot be served under strict rules and now fail with a clear message); setting to disable the full-text qualitative review; outbound allow-list and https-only rule for every HTTP call the API makes (Privacy section); platform-admin screen endpoint /nd/admin/settings/privacy-posture showing what is in effect; in-house settings profile (appsettings.InHouse.example.json); log audit (no document text, prompts or AI answers logged); 9 new tests
- In-house deployment plan and client data-protection brief (docs/in-house-deployment): deployment models, data flow, controls, phases, install runbook, responsibilities, acceptance checklist, and a list of engineering gaps found (no container packaging yet, OpenRouter requests carry no zero-retention or no-training flags, embeddings default to Azure OpenAI, qualitative review sends full policy text); Google app decision: demo only, no verification
- Built cloud storage import, Google Drive first: Admin settings > Integrations page (Connect pop-up, Disconnect, recent imports), "Import from cloud" button beside Upload on the Internal and Regulation document pages (browse folders, search, pick up to 25 PDF/Word/Google Docs, imported as Pending parse), encrypted sign-in storage per workspace, read-only access, audit log, demo accounts excluded; OneDrive/SharePoint, Dropbox, Box and pCloud shown as coming soon. 11 new tests; full sign-in with a real Google test user still to be checked
- Google side set up for Drive import (Google Cloud project, consent screen, Web OAuth client with local and dev callback addresses); guide written up in docs/storage-provider-integration/google-drive.md
- Design doc for connecting cloud storage (OneDrive, Google Drive, Dropbox, Box, pCloud) and importing documents: docs/storage-provider-integration/README.md (not built yet; open decisions listed in it)
- Step-by-step guide for the team chat (upload, parse, extract, run analysis, report, rerun all gaps, export, finalize, profile), checked against the app; parse and extract are manual buttons, and Run analysis starts straight away with no confirmation box

Bug fixes
- New analysis page: the regulation list showed "0 pts" and a red "NOT PARSED / NOT EXTRACTED" with no loader while the parse/extract status check was still waiting (more than 2 minutes once). A failed or slow check was treated as an answer. Each row now shows "Checking..." with a spinner row and "... pts" until its own status has really come back, with a "taking longer than usual" or error line while waiting (real accounts only; demo accounts unchanged). Checked by holding the status request for 20 s

Investigated, follow up pending
- Why the status check took over 2 minutes once: it normally answers in about 0.4 s here and nothing slow or failing was in the API log that was still available. The page waits up to 60 s per attempt and retries every 4 s, so two slow attempts give about 2 minutes; likely a slow or just-restarted API or database at that moment, not confirmed
- The "type start to confirm" box in the new analysis page markup is never shown (the code runs the action directly), so Run analysis has no confirmation

