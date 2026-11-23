12 Nov 2026

Tasks
- Started the cleaned copy of the app in the new Comply-Solutions repo (source only, no secrets or build output)
- New repo web app: served at the root instead of /nd, New analysis at /new-analysis, Internal and Regulation Documents use Azure Document Intelligence only
- New repo web app: removed the old app pages, analysis versions V2-V9, other OCR engine pages, Landing AI pages, Analysis Version, Evals and Text documents
- New repo web app: removed demo accounts completely (demo detection, demo menu, demo runs and seeds, Demo group admin page)
- New repo web app builds with no errors
- New repo API: removed demo accounts, the old app endpoints, Kafka and dual verify, Landing AI analysis engine, Tesseract, RapidOCR and Docling; Azure Document Intelligence is the only document engine
- New repo API: all AI calls now go through one Azure AI Foundry client (Kimi K3 first), the admin model list comes from configuration
- New repo web app: removed the old session engine and old API client, In progress button now counts analysis runs
- New repo API and web app both build with no errors (not yet run against the database)

Bug fixes
- None

13 Nov 2026

Tasks
- Ran a read-only penetration test (static source review) of the backend and web app; no code, config or data changed
- Produced an 11-page PDF security report with findings ranked by severity, evidence, impact and fixes (bcp-security-pentest-report.pdf)
- Confirmed strong existing controls: no SQL injection, correct JWT signature checks, fail-closed tenant isolation, path-safe file handling
- Ran the app locally (API on 5100, web on 3002) and did live black-box testing on top of the code review; produced a combined v2 report (bcp-security-pentest-report-v2.pdf) with a full category coverage matrix
- Live-confirmed three hardening gaps: no rate limiting (40 rapid requests, no throttling), CORS reflecting any azurewebsites.net origin, and no security headers; auth correctly returned 401 on all protected endpoints with no info leak
- Did authenticated testing logged in as platform super-admin: tampered-payload and alg=none tokens both rejected (401), out-of-scope id gave clean 404, rate limiting still absent under auth; produced v3 report
- Ran the non-admin cross-tenant test: created two throwaway maker accounts in two workspaces (SEC_TEST, bank2), confirmed a user in one workspace cannot read, enumerate or switch into another (404/403 across the board, maker also blocked from runs they did not create), then deleted both test accounts and restored the workspace
- Published an HTML version of the report as a private artifact for reading on tablet; added a data-in-transit/encryption section (app does not enforce HTTPS/HSTS itself, relies on Azure edge TLS; real TLS config needs checking against the live HTTPS site)
- Checked Supabase Row-Level Security table by table; found and confirmed a critical live exposure and remediated it (see Bug fixes); updated the report (artifact + PDF) with the critical finding and a remediation status, now 15 pages

Bug fixes
- Fixed a critical database exposure: 19 tables had Row-Level Security off, so the public anon key shipped in the website could read client data directly over the internet (including the client-banks list), bypassing the backend and tenant isolation. Enabled RLS on all 19 tables; verified the public key is now blocked (0 rows) and the app still works (backend bypasses RLS, 9 workspaces still returned to a logged-in request)
- Added API rate limiting: global 600/min per client backstop plus a strict 10/min window on the password-reset endpoint
- Added a default-deny auth gate so every /nd endpoint requires a valid login unless explicitly marked public
- Restricted CORS to an explicit allow-list; removed the any-azurewebsites.net wildcard, localhost allowed only in Development
- Added security response headers (nosniff, frame-deny, referrer-policy, CSP frame-ancestors) plus HSTS in production
- Made the unsigned-JWT development path opt-in and fail-closed so a forged token can never be accepted in a deployed environment
- Added a no-secrets warning header to the committed production config, and a global exception handler that returns a generic error with a correlation id instead of raw technical detail
- Ensured Swagger is never served on a deployed Azure host even if it is misconfigured to Development
- Added an SSRF guard that blocks server fetches to internal/private addresses, and set uploaded file content-type server-side instead of trusting the sender
- All code fixes committed one-per-finding on branch security/pentest-hardening (10 commits); each built and tested, app verified working after each

15 Nov 2026

Tasks
- Created release branch release/2026.11.15.01 from the security work, and a feature branch for the regions and rulebooks work
- Added the Middle East rulebooks (ADGM, SAMA, QFCRA, CBO, CBB) as rulebook categories with their official portal links, imported so that a business which already has rulebooks only gets the missing ones
- Built per-business regions: each business now records the country it operates in, grouped under a region (Middle East: UAE, Saudi Arabia, Qatar, Oman, Bahrain), with the regulators that come with each country
- Business settings: new Region section where an admin picks the country; choosing one also sets that business's date format when it has none of its own, so the single choice covers both
- Rulebook categories: replaced the hardcoded Middle East button with one "Import rulebook" button that imports the business's own country's rulebooks, filed as Region then Country then Regulator, still adding only what is missing
- Business workspaces admin list: new Region column showing region and country, and the column headings are now sticky while the list scrolls
- Backend and web app both build with no errors; the new endpoints were verified wired and the new database column was confirmed applied at startup

Bug fixes
- Finished the last pen test code fix (L-05): removed the response header that advertised the server technology, and stopped logging any part of the JWT secret

Investigated, follow up pending
- Pen test: the stale "-old" config files (L-04) are being kept for now, a deliberate decision rather than an open item. They are low risk because they are already excluded from the repository, but they do hold old credentials, so they should be deleted and those credentials rotated whenever that suits. Still to plan separately: live TLS verification against the HTTPS site, enabling Application Insights so correlation-id error logs are stored and searchable, moving production config to environment variables / Key Vault, and an accredited third-party test for regulatory sign-off. The security fixes are on branch security/pentest-hardening and not yet merged or deployed
- Regions and rulebooks: the work is on branch feature/regions-and-middle-east-rulebooks and has not been click-tested in the browser yet. The supplied regulator links are portal pages rather than single documents, so they import as reference categories; downloading an actual rulebook still needs a document link per rulebook. More country links are expected, and the region structure may be adjusted after the call about it
- New repo still to do: remove the dual verify model setting endpoints, Landing AI regulation parse/extract code, the local embedding model (needs the pipeline version decision), old tests; then run both apps and test login, upload, analysis and review
- Deleted internal and regulation document views only existed on the old Landing AI pages, so they are not in the new app yet
- Supabase stays for now; moving to Azure database, storage and login comes after the clean copy is tested
