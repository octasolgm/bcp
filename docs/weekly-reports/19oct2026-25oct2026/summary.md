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
- System guide for the external security testing team (docs/security-testing/system-guide.md + PDF): quick start for the main flow, then workspaces and limits, users and roles, departments, documents, libraries, running analysis, gaps/actions/reviews/evidence/export, maker-checker-reviewer flow, finalize to corrected v2 document, other admin and billing pages, ground rules on not touching existing data

Bug fixes
- Security: legacy API routes (/documents, /landing-ai, /dual-verify-kafka, /bcpweb) accepted calls with no login and ignored workspaces; now need a signed-in active user, run in the caller's workspace, legacy-table routes Default workspace only, seed routes platform super admin only; portal sends the login token to these routes
- Security: a new account could make itself super_admin through POST /nd/auth/profile; role is no longer read from that request, new profiles are always maker
- Security: tokens were accepted without signature check on non-Azure hosts when the JWT secret was missing; now only in local Development
- Security: removed the hardcoded Supabase service key and DB passwords from scripts/migrate-old-to-new-supabase.ps1 and the default 123456 password from scripts/check-api.ps1 (environment variables now); keys not rotated, by decision
- Branch security/critical-fixes-2026-10-21 and release branch release/2026.10.21.01 created locally (not pushed, remote compromised). API build, web production build and API tests run; anonymous and forged-token calls to the old routes return 401 locally. Signed-in use of the old routes still to be checked after deploy

Investigated, follow up pending
- Pull of release/2026.10.19.02 blocked on purpose: the GitHub remote has forged, force-pushed copies of real commits (same message and author, different committer timezone) on main, release/2026.10.16.01, 2026.10.19.01, 2026.10.19.02, feature/workspace-clause-limit and merged/oct15-all. They add hidden obfuscated JavaScript (end of bcp-web/karma.conf.js, new bcp-web/api.js and public/api.js wired into npm start, a fake font file run by an auto-run VS Code task). Local branches on this machine are clean and never checked out the bad commits. Nothing merged, pushed or released. Waiting on the owner to secure the GitHub account and decide the remote clean-up
- Internal security assessment before handing over to the external cyber security team: docs/cybersecurity/security-assessment-2026-10-21.md. 5 critical, 7 high, 9 medium, 5 low. Criticals: live Supabase service_role key and DB passwords committed in scripts/migrate-old-to-new-supabase.ps1 (plus AI keys in old commit 17886c8), compromised GitHub remote, legacy /documents, /landing-ai, /dual-verify-kafka, /bcpweb endpoints with no auth and no workspace filter, self-assigned super_admin via POST /nd/auth/profile on first login, JWT signature skipped on non-Azure hosts when the secret is missing. Also npm audit (jspdf critical, Angular high) and NuGet transitive CVEs. C-3, C-4 and C-5 fixed (see Bug fixes); secret rotation, GitHub clean-up and the high/medium items still open

22 Oct 2026

Tasks
- Client delivery package for installing on a client's own Azure (deploy/client-package/): build-client-package.ps1 builds one versioned zip that is the same for every client (backend.zip, frontend.zip, install.ps1, deploy-patch.ps1, settings-template.json, README.pdf, CHANGELOG.txt, version.json, checksums.txt) plus a separate SHA-256 for the download. install.ps1 creates the plan and two Windows App Services in the client's subscription, saves their Supabase/AI settings as App Service settings and deploys; deploy-patch.ps1 deploys files only and shows version before/after; rollback = run the previous package. Build removes our appsettings secret files from the API output, empties app-config.json and fails if any of our keys are found. Client guide written as README.html, printed to README.pdf at build

Bug fixes
- None

Investigated, follow up pending
- Our normal API publish puts appsettings.Secrets.json (all our keys) inside the zip; fine for our own dev App Service but must never go to a client. The client package build strips it. Not changed for the dev deploy
- Every API publish (dev deploy included) also copies leftover local folders _build_verify, _publishcheck, _publishcheck2, _publishcheck3, _testbuild, _testout from bcp-api, each with copies of appsettings.Secrets/Development files; Bcp.Api.csproj DefaultItemExcludes misses them. Client package build strips them and its key scan passed (sample v1.0.0, backend.zip 350 MB, README.pdf 8 pages). csproj not changed yet, waiting on decision
- Client install not yet run end to end on a clean Azure subscription and empty Supabase project; first-admin step is a manual SQL statement (no bootstrap-admin setting yet)

23 Oct 2026

Tasks
- Client package trimmed to the live product: New analysis (analyse-regul-full-v2) and Azure Document Intelligence document pages. Client installs get "edition": "client" in app-config.json; Analysis Version menu item hidden and older analysis versions (v8, v9, regul, regul-full), Landing AI/Tesseract/RapidOCR/Docling pages and /old pages redirect to New analysis or the Azure documents page (ndDenyClientEditionGuard). Hosted web unchanged (no edition)
- Client keys reduced to OpenRouter, Azure Document Intelligence and Azure OpenAI embeddings (text-embedding-3-small, same as live); Kafka off, no Landing AI/Gemini keys; README.pdf updated with how to create the two Azure AI resources
- Client installs now get our live analysis setup: build exports current prompt versions (regul_judgment v11, full v2) and analysis settings (openrouter kimi-k3, pipeline v6, dual-verify kimi-k2.6, gap check, prompt cache) read-only into client-baseline.json in backend.zip; API applies it at startup only when ClientBaseline__Enabled=true (ClientBaselineSeeder), replacing system-set values on each patch and keeping anything the client's admin changed. 3 new unit tests pass
- Started local API (5100) and web (3002) for the session
- Deployed bcp-web-dev 2026.10.23.01 (web only) with the status batching fix; API unchanged (2026.10.21.06). Build also carries the client-edition code, inactive on dev (no edition in app-config)

Bug fixes
- New analysis on dev showed "Could not check yet (Cannot reach the API...)" and CORS errors: the document status poll put every listed document id in one URL, and once there were more than about 55 documents the query string passed the 2048-character IIS limit on Azure, which returns a bare 404 without CORS headers. Status calls are now split into batches of 40 ids and merged
- Login check: our Supabase project signs user tokens with ES256 (new JWT signing keys) but the API only verified HS256, so every request logged "Unsupported algorithm" and called the Supabase Auth API to confirm the user (slow, some calls timed out). The API now loads the project's public signing keys (JWKS, cached 10 min, refetched once for a new key id) and verifies ES256/RS256 itself; HS256 tokens work as before and the Auth API stays as fallback. 4 new tests; checked locally (signed-in calls 200 with no Auth API call, forged ES256 token 401). Not deployed yet
- Regulation documents list ran the department-name and document queries at the same time on one DbContext; it regularly failed with "A second operation was started on this context instance" and then silently showed the list without regulation documents. Now runs them one after the other. Not deployed yet
- A fresh client database would have analysed with the Gemini default model (no key) and the Base v1 prompts instead of our live v11 prompts and kimi-k3; fixed by the baseline above

Investigated, follow up pending
- Full API test suite: 21 tests fail, the same 21 as on the last commit (checked in a temporary worktree), none new; not investigated
- Regulation list fix not exercised signed in locally (browser pane not signed in)
- Client-edition hiding checked by type-check and build only, not yet clicked through signed in with edition=client

