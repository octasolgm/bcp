Weekly Summary - 19 Oct 2026 to 25 Oct 2026

19 Oct 2026

Tasks
- Phone layout for the app frame (all ND pages): below 720px wide the left navigation is hidden and opens as a slide-out menu from a new menu button in the top bar (closes on tapping outside, on navigating, and on Escape); pages use the full screen width; the top bar fits a 375px phone (logo icon only, New analysis as a + button, shorter workspace switcher and role badge). Tablet and desktop unchanged, including the saved collapsed state and the drag-to-resize width; which menu items each account sees is unchanged (demo accounts included)

- Phone layout for the two side panels on the New analysis and evidence re-check flows (analysis step tracker on the left, admin pipeline progress panel on the right): below 720px each starts as a slim tab at the screen edge and opens as a panel over the page with a dimmed backdrop (closes on tapping outside or Escape), so the page keeps most of the width with no sideways scrolling. Tablet and desktop unchanged, including each panel's collapsed state and dragged width

- Deployed bcp-api-dev (2026.10.19.04) and bcp-web-dev (2026.10.19.05) to Azure dev from release/2026.10.16.01 checkout; API settings synced; startup health ready
- Deployed bcp-api-dev (2026.10.19.06) and bcp-web-dev (2026.10.19.07) from release/2026.10.19.02 (phone ND shell); API settings synced; startup health ready

Bug fixes
- On a phone the navigation stayed fully open beside every page and left the page about 38px wide
- On a phone the step tracker and pipeline panel kept their full widths beside the page and squeezed it

Investigated, follow up pending
- At 768px (tablet) with the navigation, step tracker and pipeline panel all open, the three fixed widths add up to more than the screen and the New analysis page has no room left; collapsing a panel fixes it. Not changed, tablet behaviour was kept as it was
- On a 375px phone the New analysis page clips a few of its own fields by about 20px (section heading, search boxes) with both panel tabs showing; that comes from the page's own layout, not the panels
- In-house (on-prem) deployment for banks: wrote a design doc for licensing and billing - signed license per install, credit/clause allowance and paid-through date from the license, online daily check-in (usage counters only) or offline license files and top-up vouchers, grace period then read-only on non-payment, a separate license/billing server on our side. Builds on the existing AI credit ledger, credit limit and clause limit. No code yet, waiting on pricing and grace-period decisions
- In-house pricing for banks: wrote a second design doc on AI keys, costs and profit - Managed AI (our OpenRouter/Azure, one capped OpenRouter key and one Azure resource group per bank, resold as credits at cost x markup) vs Own AI (bank's own keys, subscription only); subscription and clause allowance in both; per-bank cost tracking and reconciliation; worked profit example. Waiting on pricing decisions
- In-house licensing explainer doc: what a license signature is (ECDSA P-256 signed in Azure Key Vault, public key in the app), full example license, plans as editable templates (not hardcoded, license carries the actual numbers), how each limit is enforced (clauses, documents, users, workspaces, features, monthly reset), how we issue licenses (command-line tool first, license server later), and how in-house updates are delivered and installed (signed release packages, bank change window, UAT first, add-only database changes, updatesUntil). No code yet
- In-house licensing overview doc: trials (limits, extend, convert on the same install, end), subscriptions (example plans, monthly/annual, renewal, upgrade pro-rata, downgrade at renewal, cancel to 90-day archive), add-ons (recurring add-ons vs one-time packs delivered as vouchers, allowance used before packs), worked license example, every lifecycle event, monthly invoice lines, who does what. Waiting on plan, pack and overage decisions

20 Oct 2026

Tasks
- None

Bug fixes
- None

Investigated, follow up pending
- Full in-house deployment plan doc pulling together all options: deployment packages (hosted, in-house Managed AI, in-house Own AI, fully contained), hosting options (single server, VMs, private cloud, Kubernetes, GPU), components and sizing, AI and document-reading choices, network modes (connected, restricted, air-gapped), security controls, licensing summary, install runbook, updates/backups/DR/monitoring, responsibilities, 6-phase bank rollout (about 6 to 9 weeks), built vs to build (about 6 to 8 weeks of engineering before the first bank, containers and license module first), risks. Waiting on package, pricing and engineering start decisions
- Saved the whole in-house plan into the repo as docs/in-house-deployment/: full-plan.md, licensing-overview.md, licensing-design.md, pricing-ai-keys-profit.md, azure-deployment.md (one-script install into a bank's Azure subscription with Bicep + PowerShell, VM route first, Container Apps later, Marketplace later) and implementation-guide.md (work packages and exact code locations for whoever builds it); README now has an index. Documentation only, nothing implemented

21 Oct 2026

Tasks
- Committed the in-house deployment docs (47e68d3, local only, not pushed)

Bug fixes
- None

Investigated, follow up pending
- Pull of release/2026.10.19.02 blocked on purpose: the GitHub remote has forged, force-pushed copies of real commits (same message and author, different committer timezone) on main, release/2026.10.16.01, 2026.10.19.01, 2026.10.19.02, feature/workspace-clause-limit and merged/oct15-all. They add hidden obfuscated JavaScript (end of bcp-web/karma.conf.js, new bcp-web/api.js and public/api.js wired into npm start, a fake font file run by an auto-run VS Code task). Local branches on this machine are clean and never checked out the bad commits. Nothing merged, pushed or released. Waiting on the owner to secure the GitHub account and decide the remote clean-up
