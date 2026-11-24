23 Nov 2026

Tasks
- Comply-Solutions staging: deployed the clean edition to two new App Services on the existing B1 plan (bcp-api-staging, bcp-web-staging), so staging adds no monthly cost. The live bcp-api-dev and bcp-web-dev apps were not touched
- Staging API set to .NET 8, Always On, 64-bit, HTTP/2 and TLS 1.2, with its managed identity turned on. All 20 keys and service addresses are Key Vault references, so the app holds no keys of its own; CORS allows only the staging web address
- Staging web app built with its own address file pointing at the staging API (the Windows host serves it as a static file)
- Added the six non-secret values (AI service addresses, embedding deployment, Google client ID, sign-in key) to the Key Vault
- Security documents: an internal security control register (61 controls with evidence, SOC 2 and ISO 27001 mapping and status: 34 done, 10 partial, 17 open) and a two-page Security and Trust Overview for bank IT teams that lists only controls in place on the live service
- Re-checked the 13 Nov penetration test findings against the current code and the live API: both High findings fixed, 9 of 13 fixed in total; HTTPS only, TLS 1.2, HSTS and the security headers confirmed live
- API logs now reach Azure: added the App Service logging provider to the API (bcp and Comply-Solutions) and turned on Application and Web server logging on bcp-api-dev and bcp-api-staging. Error details the API keeps out of its responses are now in Log stream, matched by the correlation id shown with the error
- Released 2026.11.23 on branch release/2026.11.23.01 and deployed it: API 2026.11.23.01 and web 2026.11.23.02 are live. Checked after deploy: startup ready, a wrong sign-in answered by our API in our own wording, the four security headers present, the web config names only our API, and log lines arriving in Azure
- Security phase 1 released as 2026.11.23 (API 2026.11.23.03, web 2026.11.23.04, branch release/2026.11.23.02), also in Comply-Solutions and on staging:
  - Error text: messages our own code writes still reach users; anything a framework or database driver throws is replaced by a generic message with a reference id and logged in full (pentest M-05)
  - The startup log no longer prints part of the JWT secret, and the public root address returns only a status (pentest L-05)
  - bcp sign-in now uses the publishable sign-in key instead of the admin key (key checked against Supabase first)
  - Fonts are served from our own site, so the browser no longer contacts Google
  - Checked live: protected endpoints refuse requests without a token, a wrong sign-in is answered in our own wording, the root shows only a status, the font comes from our own site
- Security register updated: 42 done, 8 partial, 17 open; 11 of 13 penetration test findings fixed
- Comply-Solutions deploy now reads keys from Key Vault only: removed the secrets file the deploy used to ship and the script that built it; deploy-api and deploy-web target staging by default and refuse the live bcp apps; new set-keyvault-settings script turns every vault secret into a Key Vault reference on an App Service (names only, no values). Redeployed staging with it: all 20 references resolved, startup ready, sign-in answered by our API
- Security register rebuilt to show what is done and what is not: 43 done, 8 partial, 17 open. Key Vault and managed identity marked done on staging, not yet on live; new control for deploy packages carrying no keys

Bug fixes
- Staging sign-in failed with 'An unexpected error occurred'. The new logs showed why: the staging API cannot read Key Vault yet, so its settings are the unresolved reference text and the sign-in service address is empty. Needs the Key Vault Secrets User role, no code change. Role granted; after a restart all 20 Key Vault references resolved, the staging API connected to the database and sign-in answers correctly

Investigated, follow up pending
- Staging uses the same Supabase database as live, so anything done on staging happens in real data until the Azure PostgreSQL server exists
- Google Drive import on staging needs the staging API address added to the Google app's redirect list before it can connect
- Security gaps for bank-grade assurance: monitoring and an incident plan, written policies, an accredited third-party penetration test, then SOC 2 and ISO 27001
- The live API keeps its keys in App Service settings (bcp is live, small changes only); it moves to Key Vault when Comply-Solutions replaces it. Staging already reads everything from the vault
- The bcp test project does not build (a test still calls the renamed ImportSample); fixed in Comply-Solutions only

24 Nov 2026

Tasks
- Security phase 2 in Comply-Solutions, deployed to staging (commit a830e4e, not on live):
  - Password rules checked by our API wherever a password is set: 12+ characters, three character types, no common or email-based passwords
  - Audit log: sign-ins, failed sign-ins, two-step, PIN and password changes, every change and every page or document opened; admin page with filters and CSV export; kept 3 months
  - Two-step sign-in with an authenticator app (Google, Microsoft or other), set up by QR code or typed key, enforced by the API; admins can reset it; a business can require it for everyone
  - Optional 6-digit sign-in PIN offered as a two-step choice: stored hashed, locks for 15 minutes after 5 wrong tries
  - Reset and invite pages ask for the authenticator code when the account uses two-step sign-in
  - Dependency scan script; System.Text.Json, System.IO.Packaging and brace-expansion updated
- Security register updated: 43 done, 10 partial, 15 open
- Release branch release/2026.11.24.01 in both repositories (pushed; Comply deployed to staging, bcp not deployed):
  - Angular 19 to 22, jsPDF 2 to 4 and patched packages: 0 known web vulnerabilities in both apps (the critical jsPDF one is gone)
  - AI input guard on every AI call, both apps: emails, phones, IBANs, card numbers and Emirates IDs masked before text leaves the server; a fixed instruction that document text is data, not instructions; instruction-like text logged. Demo accounts never call AI, so the demo is unchanged
  - Comply only: two-step backup codes (10 one-time codes, stored hashed, "Use a backup code" at sign-in, new codes from My profile)
  - Comply API tests: 392 pass, the same 18 known failures as before
- Security register: 43 done, 12 partial, 13 open

Bug fixes
- Two-step set-up showed no QR code: the sign-in service now sends raw SVG, which the API turns into an image
- Set-up panel stayed on "Preparing set-up" and sent the set-up request several times; fixed

Investigated, follow up pending
- Staging and live share one Supabase sign-in service: an account with two-step sign-in cannot change or reset its password in live bcp. Plan: a separate staging Supabase project (about $10 a month on Pro), waiting on the project and a second Key Vault
- The bcp release branch needs a full click-through (sign-in, documents, analysis, results, exports, admin, demo) before it goes live; one known analysis should be re-run and compared because of the AI input guard
