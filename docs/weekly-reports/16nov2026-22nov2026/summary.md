16 Nov 2026

Tasks
- Business workspaces list: the Region shown against each business is now editable in place, so an admin clicks "Not set" (or the current region) in the row and picks the country without opening anything
- Business workspaces list: tidied the row height. The six action buttons had a wide minimum size, so they sat two per row and made every business three rows tall; actions are now compact and cell padding is tighter
- Business workspaces list: Users, Credits and Settings were three separate buttons each opening their own panel. One Settings button now opens the row and those three are tabs inside it, so the actions fit one short row (Open, Settings, Deactivate)
- Business workspaces list: renaming moved onto the title itself, a pencil beside the name with a tick to confirm, which also took a button out of the actions column
- Business workspaces list: added a "Back to top" shortcut once the list has been scrolled, and the open panel now names the business and repeats its region, counts and status
- Rulebook categories: Import rulebook is now disabled until the business has a region, with a tooltip and a line pointing at Business settings, instead of only failing after the click
- Rulebook categories: added multi-select with a bulk delete. Each row has a tick box and a bar appears with the count, Select all shown, Clear and Delete selected. It confirms first, deletes parents before children, and does not report a child the cascade already took as a failure
- Business workspaces: the create form is folded behind a "+ New workspace" button and the "Platform admin" heading and explanatory paragraph were removed, so the page opens on the list and it runs nearly the full height of the screen
- Created release branch release/2026.11.16.01 and pushed it, the regions feature branch and the security fixes branch to GitHub
- Deployed both the API and the web app to the dev Azure apps (bcp-api-dev, bcp-web-dev). Confirmed afterwards against the live site that the security work is in effect there: the protective headers and HSTS are returned, unauthenticated requests are refused, and an unrelated azurewebsites.net origin is no longer trusted by the cross-origin rules

Bug fixes
- Sticky column headings on the Business workspaces list did not actually stick: the table wrapper allowed overflow but had no height to scroll within, so the page scrolled and the headings left the screen. The list now scrolls inside the table, which is what the shared sticky heading rule needs. Also removed a duplicate rule that was overriding the shared heading colours
- Import rulebook appeared to hang with the button spinning. The import was writing one row at a time so that each category had a stored parent to point at, which against the hosted database meant a separate round trip per row, over 250 of them for the UAE pack. The whole import is now written in one go. The ordering of new categories was also being read once per row, which gave every new sibling the same position
- Import rulebook then failed outright with an argument error, because the new lookups were keyed by the parent and a regulator at the top level has none. Both lookups now stand in a fixed value for the top level. Verified against a real import: 256 categories and 205 rulebooks in 1.9 seconds, where before it ran 41 seconds and timed out
- The build number shown in the app was a day behind late in the evening because it was stamped in UTC rather than local time. It now uses the local date, so today reads 2026.11.16
- The analysis-in-progress indicator in the left sidebar polled a pre-workspace endpoint on every page that is not Overview. That endpoint is only available in the Default workspace, so inside a client business it answered 403 every 30 seconds on every page while never having anything to report. It now runs only in the Default workspace, and stops if the workspace changes under it
- Deployed build 2026.11.16.26 to both Azure apps. Confirmed live: the web app reports the new build on both its azurewebsites address and app.comply-solutions.com, the API reports it and finished its startup bootstrap, the protective headers are returned, and an unauthenticated call is refused
- Rulebook content for the other Middle East countries: each of Bahrain, Saudi Arabia, Qatar and Oman had a single row, the regulator's portal link, so importing Bahrain produced one category. Read each regulator's own published rulebook and built a seed pack per country - Bahrain 172 entries (Volumes 1 to 7 and the Common Volume with their modules), Saudi Arabia 826 (each sector, its topics and the instructions under them), Qatar 703 (the QFC law, regulations, QFCA rules and the 20 QFCRA rulebooks with their parts), Oman 52 (laws, frameworks and AML guidance, each carrying its own PDF link). Quarterly updates, ad-hoc communications, archived parts, forms, consultation papers and circulars were left out as chronological listings rather than structure

Investigated, follow up pending
- The Business workspaces table still does not look right and is the main thing to pick up next. Several passes were made from screenshots (actions on one row, column widths, centring rows, showing clause limits beside the credit chip) and it is closer, but not settled. The likely remaining cause is the admins column: a business with several admins makes its row far taller than the rest, so capping that cell, for example showing the first admin and a count, is probably the real fix rather than more width tuning
- Everything is on the dev apps but has had very little hands-on use, so the regions picker, the settings tabs, renaming from the title and especially the bulk delete should be exercised on dev before this goes any further. Bulk delete removes everything beneath what is selected, so it is worth trying on a throwaway category first
- Removing the header that advertises the server software is only half done: the application no longer sends its own, but Azure's front end still adds one. Finishing it is an Azure-level setting rather than a code change
- Regulator links supplied so far are rulebook portal pages rather than single documents, so they import as reference categories; pulling in an actual rulebook document still needs a document link per rulebook. More country links are expected
- The web app that app.comply-solutions.com serves is the same Azure app as bcp-web-dev, so what has been called a dev deploy all week has been going to the address clients use. Nothing is broken by that, but a separate dev app, or a decision to treat this one as production and gate deploys, needs to be made before the next release
- After an import the page reloads the whole tree, which on a local machine talking to the hosted database is another 3 seconds on top, so the rows appear a moment after the button finishes. Having the import return the tree it just wrote would remove that second wait
- The new country packs are committed but have not been imported end to end yet: Bahrain should be imported on a test business to confirm the tree lands as expected before this goes out
- Oman's circulars are not in its pack. They are issued continuously and listed by year behind a filter on the CBO site, so they need a different approach from a fixed seed file if they are wanted

17 Nov 2026

Tasks
- Comply-Solutions repo: brought in everything done in bcp since the clean copy was made (35 commits: the security fixes, regions per business, rulebook packs for Bahrain, Saudi Arabia, Qatar and Oman, the Business workspaces changes). Each changed file was merged so the new work lands without undoing the cleanup
- Comply-Solutions repo: removed the regulation document detail page (not linked from anywhere) and the old "Dual verify model" setting and its endpoints; the OpenRouter privacy switches went with the OpenRouter client
- Comply-Solutions repo: added a .gitignore so the local secrets files and build output are never committed
- Comply-Solutions repo: the analysis pipeline-version setting and the local embedding model are kept as they are, so analysis works the same as in /nd
- Comply-Solutions repo: API and web app both build, the API starts against the existing database and the web app opens on the sign-in page at the root address
- Comply-Solutions repo: API tests run - 371 pass; the 19 that fail fail the same way on the current bcp code, so none come from the cleanup
- Comply-Solutions repo: checked signed in - all 23 pages open with no errors or failed requests, Overview matches the old app
- Comply-Solutions repo: committed the clean copy on branch clean-nd-copy (not pushed). No keys in the commit: the Supabase anon key moved out of the web environment files and scripts into the local, gitignored app-config.json and API settings, sample settings carry placeholders, and an old check script holding a key was removed
- Comply-Solutions repo: removed the Kafka leftovers - the Kafka settings, the old non-/nd route prefixes, the dual verify health types and the browser-side Kafka session store
- Comply-Solutions repo: full clean, keeping only what is used. Web: unreachable files, 23 unused API client methods and about 270 unused functions, types and class members. API: 16 endpoints no page calls, 11 unused classes and about 90 unused methods, plus the tests that only covered them. Settings: removed the sections for providers that are gone (Gemini, OpenAI, Anthropic, xAI, Moonshot, DeepSeek, Zhipu, Qwen, OpenRouter), NodeBridge, dual verify and the demo keys, and added an empty Azure Foundry section. Removed three unused scripts, a template request file, an unused schema and an unused package. Rewrote the API and web READMEs and the API deployment guide for this repo
- Comply-Solutions repo: after the clean, API and web both build, the API tests give the same results as bcp, and all 26 signed-in pages open with no failed requests. Committed as a second commit on clean-nd-copy and pushed to GitHub, secret scan clean
- Comply-Solutions repo: connected Azure AI Foundry. The endpoint and key are in the local, gitignored settings only. The resource has one deployment, FW-Kimi-K3, which is now the model list and default. A direct test call answered in under 3 seconds, and Admin settings shows Azure AI Foundry with FW-Kimi-K3 and the key configured for analysis and finalize

Bug fixes
- Two links added in the 16 Nov rulebook work still pointed at /nd/admin/business-settings in the new repo; they now use /admin/business-settings
- Publishing the API from the new repo would have failed: the build calls a script that copies keys into the publish settings, but that script was only a local file in bcp and never came across. Added it, copying only the sections this app uses
- The sign-in error for a slow database told the user to run a stuck-runs script whose endpoint no longer exists; that line was removed

Investigated, follow up pending
- Signed-in actions in the new repo not tried yet: opening a gap report, using New analysis, Parse and Extract, review approve and send back, exports
- First full analysis on Azure Kimi K3 (one clause) completed but took 5 min 19 s against under a minute on OpenRouter; see 19 Nov
- The API build still warns about constructor parameters left unused after the clean (about 20); removing them is a small follow-up
- The API startup log still prints the first characters of the JWT secret (Program.cs); this is the same in bcp and was left identical
- 18 API tests fail in the new repo and the same ones fail in bcp (normalisers, the section splitter, prompt versions, in-memory database mapping of the vector column, rulebook import) and need looking at
- Full move off Supabase onto Azure: went through what Supabase is actually doing for the platform today and what has to be created on Azure to replace it. Three things are in use - the Postgres database (all tables, including the vector columns the retrieval uses), sign-in (browser sessions, password reset, invite links, the admin user list) and the private document bucket. Mapped to Azure that is Azure Database for PostgreSQL Flexible Server with the vector extension (not Azure SQL - the data layer, the vector type and the raw SQL are Postgres specific, so Azure SQL would be a rewrite and would lose vector search), a Blob Storage account with a private container, a replacement for sign-in, an email sender for reset and invite mail, and Key Vault for the secrets that sit in local settings files today. Document Intelligence, the embedding deployment and the Kafka namespace are already on Azure. Sign-in decided on 19 Nov: our own API, see below

19 Nov 2026

Tasks
- Azure move: sign-in decided - the platform will handle sign-in itself (email and password, short access token plus refresh token, reset and invite links by email). All 20 users are email and password with standard bcrypt hashes and no two-step or Microsoft sign-in, so the hashes copy across and nobody resets a password. Entra External ID was the alternative but cannot import the hashes
- Azure move: DevOps request written for PostgreSQL 17, Blob Storage, Communication Services email, Key Vault, Application Insights and App Service checks. First version came to about 500 dollars a month; cut to a lean version (Burstable B2s database, LRS storage, no App Service upgrade, 1 GB log cap) at roughly 40 to 50 dollars a month for the new resources
- Checked the live Supabase database to size the move: PostgreSQL 17.6, extensions vector, pgcrypto and uuid-ossp, 114 MB of data, 433 MB of documents (160 files), no links from our tables to Supabase's sign-in tables
- Key Vault kv-comply-solutions created. Local runs of Comply-Solutions now read their keys from it after az login (secret names use -- for :). Only in Development, so the App Service keeps its own settings. Added a script that copies local keys into the vault without printing them; 15 keys imported, cleared from the local settings files, and the API verified to start and reach the database from the vault alone
- Committed abf3186 on clean-nd-copy, secret scan clean
- Wrote a Key Vault guide (bcp-api/KEY-VAULT.md) for other developers and the App Service; the import script and web config script now also cover the non-secret values a fresh clone needs, so a new developer only needs vault access and az login. Committed 2aa681b
- Settings files tidied for Key Vault (519e26a): the developer template no longer holds keys or a placeholder vault address, Landing AI settings removed everywhere, local secrets file retired
- Landing AI removed from Comply-Solutions (2dd3c6e): the web client, parser and extraction code are gone, the remaining text tools moved to a neutral DocumentText folder, the unused Dual verify V3 prompts left the Admin prompts page, and the Landing AI labels users saw were reworded. 83 files, about 2,300 lines removed. All 26 signed-in pages checked with no failed requests; tests unchanged (same 18 failures as before). Branch clean-nd-copy pushed to GitHub up to 2dd3c6e

- Sign-in moved behind our own API: the browser no longer calls Supabase at all. Sign in, token refresh, sign out, password change and reset/invite link checks now go to /nd/auth endpoints on our API, which talks to the identity provider server side. The Supabase JavaScript library and the project URL and anon key are out of the web app and its bundle
Bug fixes
- Kimi K3 on Azure ran at its default maximum reasoning effort because nothing was sent, doubling tokens and time compared with bcp. Now sends reasoning effort high and a 65,536 token budget, the same as bcp
- AI usage rows for Azure calls showed the raw Azure host instead of a provider name; new calls are labelled azure-foundry
- The Kimi K3 key had been typed into the committed appsettings.json; it is now empty there and lives only in Key Vault
- The web config script never found the local API settings file: the backslash in its path had turned into a control character. Fixed
- The developer settings template carried a placeholder Key Vault address that overrode the real one, so a new developer's vault would silently fail. Removed
- Analysis of an internal document without Azure Document Intelligence text fell back to Landing AI and stopped with 'Landing AI is not configured' even when its text was already saved. It now uses the saved text, or asks for Azure Document Intelligence to be run

Investigated, follow up pending
- Azure Kimi K3 runs about 40 tokens a second, roughly 3 times slower than the same model through OpenRouter. Expect analyses about 3 times longer even with the reasoning fix. DevOps to confirm the deployment type (Global Standard) and raise the tokens-per-minute quota so 8 clauses can run at once
- FW-Kimi-K3 has no price on the model price list, so its AI usage cost is an estimate at the default rate until the real Azure price is added
- The Kimi K3 key was pasted in chat and should be regenerated in AI Foundry, then updated in Key Vault
- Waiting on DevOps for the database, storage and email resources before the data copy, blob storage switch and sign-in work can be tested against Azure
- The six non-secret values (endpoints, embedding deployment, Google client ID, Supabase anon key) still need importing into the vault by re-running the import script
- 93 internal documents, 5 gap evidence files and 19 regulations have no saved text and need Azure Document Intelligence run on them before they can be analysed
- Database tables and columns named landing_ai_* stay as they are: they hold current analysis results in the database shared with bcp. Rename during the Azure database move

20 Nov 2026

Tasks
- Released the sign-in change on branch release/2026.11.20.01 and deployed it. Build 2026.11.20.03 is live on the API and the web app. Checked after deploy: the served config names only our own API, the web bundle has no identity provider code left, and a sign-in attempt on the live site is answered by our API in our own wording
- The deploy scripts now send the publishable sign-in key to the API app settings and no longer write the provider URL and key into the web app's config file
- Comply-Solutions: same sign-in change carried over (9d80b2e). The browser now only talks to our own API for sign-in, session refresh, sign-out, password changes and reset links. Tested with a real sign-in locally: the only outside requests left are Google Fonts, and all 26 pages load without errors
- Comply-Solutions: document links no longer hand the browser a storage-provider address. Opening a PDF now uses a one-hour signed link on our own API that streams the file; checked with an internal document (1.3 MB) and a regulation (9.3 MB)
- Comply-Solutions: messages that named the provider (storage or user management not configured, reset link errors) reworded, and the health check reports postgres instead of supabase

Bug fixes
- Comply-Solutions: password reset links pointed at /nd/auth/reset-password, a page that does not exist in the clean edition (its pages sit at the root). Now /auth/reset-password
- Comply-Solutions and bcp: the new publishable sign-in key was never read from settings by the API, so sign-in calls always used the admin key. Comply now reads it; bcp still has the same gap
- Comply-Solutions: a regulation whose title has no .pdf ending downloaded instead of opening, through the new document links. The file type now comes from the stored file

Investigated, follow up pending
- A real successful sign-in has not been tested end to end by us, only failed attempts. Worth one sign-in, one session left open past the access token's hour, and one password reset on the live site
- Reset emails are still sent by Supabase from its template; the link passes through supabase.co unless the Reset password template is changed to link straight to our page with the token hash
- Fonts load from Google Fonts; serving them from our own site would remove the last outside request
