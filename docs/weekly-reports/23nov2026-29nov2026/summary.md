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

Bug fixes
- Staging sign-in failed with 'An unexpected error occurred'. The new logs showed why: the staging API cannot read Key Vault yet, so its settings are the unresolved reference text and the sign-in service address is empty. Needs the Key Vault Secrets User role, no code change

Investigated, follow up pending
- The staging API cannot read the Key Vault yet (AccessToKeyVaultDenied): it needs the Key Vault Secrets User role on kv-comply-solutions, and our account can manage secrets but not grant roles. Someone with Owner or User Access Administrator on the vault has to add it, then restart bcp-api-staging
- Staging uses the same Supabase database as live, so anything done on staging happens in real data until the Azure PostgreSQL server exists
- Google Drive import on staging needs the staging API address added to the Google app's redirect list before it can connect
- Security gaps for bank-grade assurance: multi-factor sign-in, monitoring and an incident plan, written policies, an accredited third-party penetration test, then SOC 2 and ISO 27001
- 17 endpoints still return raw error text, and the API logs the first characters of the JWT secret at startup
