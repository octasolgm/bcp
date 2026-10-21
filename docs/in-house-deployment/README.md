# In-house deployment plan (client servers)

Status: plan, written 15 Oct 2026. Nothing here has been deployed at a client yet. Section 12 lists what is not ready.
Client-facing explanation of data protection: [data-protection.md](data-protection.md).

## Documents in this folder (start here)

Added 19 to 20 Oct 2026. All are plans; nothing in them is implemented yet unless marked "Built".

| File | What it covers |
| --- | --- |
| [full-plan.md](full-plan.md) | Master plan: every deployment option, hosting, sizing, AI and network choices, security, runbook, operations, rollout phases, what is built vs to build, risks, open decisions |
| [licensing-overview.md](licensing-overview.md) | What we sell: trials, subscriptions, recurring add-ons, one-time packs, lifecycle, invoices, who does what |
| [licensing-design.md](licensing-design.md) | How licensing works technically: signatures, license file format, plans as templates, states, limit enforcement, online and offline delivery, tamper resistance, issuing licenses, updates |
| [pricing-ai-keys-profit.md](pricing-ai-keys-profit.md) | Managed AI (our keys, one per bank) vs Own AI (bank keys), cost tracking per bank, profit, non-payment |
| [azure-deployment.md](azure-deployment.md) | Installing into a bank's Azure subscription with one script (Bicep + PowerShell), routes, resources, package contents, Marketplace later |
| [implementation-guide.md](implementation-guide.md) | Build hand-off for a developer or AI: ground rules, work packages, exact code locations, acceptance criteria |
| This README | Original 15 Oct plan: deployment models A/B/C, data flow, protection controls, install runbook, settings, acceptance checklist |
| [data-protection.md](data-protection.md), [privacy-settings.md](privacy-settings.md) | Client-facing data protection and the privacy settings already built |

Related: [../roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md](../roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md),
[../roadmap/ON-PREM-DEPLOYMENT-TODO.md](../roadmap/ON-PREM-DEPLOYMENT-TODO.md), [../costing/AI-CREDITS-COSTING.md](../costing/AI-CREDITS-COSTING.md).

## 1. Why this exists

The hosted portal (bcp-web-dev on Azure, data in Supabase cloud) is a demonstration and evaluation environment. Banks and
other regulated clients do not want their documents on a shared public service. When a client decides to go ahead, we install
Comply Solutions in their environment: their servers or their private cloud account, their database, their storage, their keys.

Rule for the demo: evaluators upload sample or non-sensitive documents only. Real documents go in only after the in-house
deployment exists.

## 2. Three deployment models

| | A. Hosted demo (today) | B. In-house, protected cloud services | C. In-house, fully contained |
|---|---|---|---|
| Who runs it | Us | Client's environment, we install and support | Client's environment, we install and support |
| App, database, files | Our Azure + Supabase cloud | Client's servers (self-hosted Supabase) | Client's servers |
| Reading documents (parse, OCR) | Azure Document Intelligence (or local engines) | Azure Document Intelligence in the CLIENT's Azure tenant, region of their choice, private network; or local engines | Local engines only (PdfPig, Tesseract, RapidOCR, Docling): nothing leaves |
| Search embeddings | Local model or Azure OpenAI | Local model (recommended) or the client's own Azure OpenAI | Local model |
| AI analysis | OpenRouter (any model) | Client's own Azure OpenAI, or OpenRouter with zero-retention and no-training enforced, with a client-approved model list | Models the client hosts (needs an OpenAI-compatible endpoint, see section 12) |
| Documents leave the client environment? | Yes (demo data only) | Only to the services the client approves, over TLS, within the region they choose | No |
| Typical client | Evaluators | Most banks | Banks with a no-external-AI rule |

Recommendation: sell B as the standard. It keeps the product's quality (strong models) while putting every data store and
every key under the client's control. Offer C where the client forbids any external AI call, and be honest that local models
may give weaker results and need GPU or large CPU capacity.

## 3. Components

| Component | What it does | Notes |
|---|---|---|
| Web app (Angular, static files) | The portal users see | Served by IIS, nginx or similar behind the client's reverse proxy and TLS |
| API (.NET 8, `Bcp.Api`) | All logic: upload, parse, extract, analysis, reports | Runs as a service or container. Background workers run inside it |
| Supabase (self-hosted, open source) | PostgreSQL database, sign-in (auth) and file storage | Needs the pgvector extension (search vectors). The API expects Supabase's address, JWT secret, service key, database host and storage bucket |
| Document engines | Local: PdfPig (built in), Tesseract (built in, needs its language data), RapidOCR, Docling (separate service in `docling-service`) | Azure Document Intelligence is optional |
| Embedding model | Turns passages into vectors for search | Built-in local model (bge-micro-v2) runs on the API server. No external call |
| AI model access | Judges each clause against policy passages | Provider is a setting. Options in section 2 |
| Optional: queue | Kafka / Event Hubs for the dual-verify worker | The app falls back to a local in-process queue. Not needed for a first install |

## 4. Where data goes in model B (per step)

1. User uploads a file: browser to the client's own web app and API over TLS, stored in the client's own Supabase storage.
2. Parse and extract: file bytes go to the document engine. Local engine: stays on the server. Azure Document Intelligence in the
   client's tenant: leaves the server, goes only to the client's own Azure resource, in the region they chose.
3. Index: passages and vectors are written to the client's PostgreSQL.
4. Analysis: for each clause, the clause text and the retrieved policy passages are sent to the AI model (no user names or account
   details). Answers come back and are stored in the client's database. The OPTIONAL "qualitative assessment" is different: it builds its
   prompt from the regulation and policy text as a whole, so it sends far more. It is off unless someone ticks it for a run, and for
   in-house installs it should stay disabled unless the client approves it for their AI route.
5. Reports and exports: generated on the client's server.
6. Our team: no standing access. See section 6.

## 5. Protection controls

| Layer | Control | Status |
|---|---|---|
| Workspaces | Every row carries a workspace id and the database filter keeps one workspace from seeing another | Built |
| Sign-in | Supabase auth with JWT. Roles: admin, maker, checker, reviewer | Built |
| In transit | TLS on every hop (browser, API, database, AI and document services) | Client infrastructure + our configuration |
| At rest | Disk or volume encryption for database and storage (client's standard). Cloud-storage tokens are additionally encrypted by the app | Tokens: built. Disk: client |
| Secrets | Keys in the client's secret store (Key Vault, HashiCorp Vault) or protected environment variables. Never in git | To define per client |
| Network | Private network, firewall egress allow-list that allows only the AI and document endpoints the client approves. The API also refuses calls to hosts not in `Privacy:AllowedOutboundHosts` and can require https | App setting built 15 Oct. Firewall: client |
| AI vendor terms | OpenRouter: every request carries zero data retention and no training (on by default, can be switched off). Azure OpenAI in the client's tenant: Microsoft's terms apply. Settings and test results: [privacy-settings.md](privacy-settings.md) | OpenRouter flags built 15 Oct. Models without a qualifying provider fail with a clear message |
| Document reading vendor | Azure Document Intelligence: processed in its region, results held temporarily, encrypted, retrieved only with the same key (Microsoft data and privacy page) | Setting per client |
| Logging | Operational logs must not contain document text or AI prompts | Audited 15 Oct: none found; provider error bodies cut to 300 characters are the only residual (privacy-settings.md section 5) |
| Audit | Imports, reviews and status changes are recorded with who and when | Partly built |
| Retention and deletion | Client sets the period. Deleting a workspace removes its documents, vectors and results | To define and test |
| Backups | Client's standard backup of PostgreSQL and storage, encrypted, with a tested restore | Client + us |
| Our access | None by default. Support by screen-share, or temporary access the client grants and revokes, and logs | Process |
| Updates | Signed release package delivered to the client, applied by the client in a change window, with rollback | To build |

## 6. Support access rules
- No permanent accounts for our staff in the client environment.
- Access only when the client opens a ticket, only to the minimum, through the client's jump host or VPN, recorded, ended after.
- We never copy client documents out of their environment. Diagnostics are redacted logs.

## 7. Delivery phases

| Phase | Duration (typical) | What happens | Output |
|---|---|---|---|
| 0. Discovery | 1 week | Security questionnaire, architecture review, choose model A/B/C, AI and document service decisions, data classification, identity (SSO) needs | Signed scope and security decisions |
| 1. Environment | 1 to 2 weeks (client side) | Client provisions servers, network, DNS, TLS certificates, database and storage, accounts, Azure resources if used | Environment ready checklist |
| 2. Install | 2 to 3 days | Install self-hosted Supabase, API, web app, engines. Configure secrets. First workspace and admin | Working empty system |
| 3. Hardening and review | 1 week | Egress rules, backups and restore test, log review, vulnerability scan, role setup, client security review | Security sign-off |
| 4. Pilot | 2 weeks | Client loads real regulation and a few real policies, runs analyses, compares with their own judgement | Acceptance report |
| 5. Go-live and handover | 1 week | Admin training, runbook handover, support channel, monitoring | Live system |
| 6. Run | Ongoing | Updates, quarterly access review, backup tests, model and prompt version reviews | Service reports |

## 8. Install runbook (model B)

1. Prepare the server(s): 64-bit Linux or Windows Server. Start with 8 vCPU, 32 GB RAM, 500 GB disk (more if many OCR jobs) and size
   after the pilot. Time sync and OS updates on.
2. Install self-hosted Supabase (PostgreSQL with pgvector, auth, storage, gateway). Change every default password and key.
   Generate a new JWT secret and service key for this client only.
3. Create the storage bucket the API expects and note the bucket name.
4. Install .NET 8 runtime on the API server. Copy the release package. Place configuration (section 9) in protected storage.
5. Start the API. On first start it creates and updates its own database tables. Check the health address.
6. Publish the web app static files behind the client's reverse proxy with TLS. Set the API address the web app calls.
7. Set the allowed web addresses (CORS) to the client's portal address only.
8. Create the first workspace and its admin user. Create departments and users, roles as the client defines.
9. Configure document reading: local engines, or the client's own Azure Document Intelligence resource (private endpoint where possible).
10. Configure embeddings: choose the local model.
11. Configure AI analysis: the client's approved provider and model list. Test with a sample clause.
12. Configure cloud storage import only if the client wants it, with the client's own app registration (their Google Workspace as an
    internal app, or an app in their Microsoft Entra tenant). Callback address is their own API address.
13. Close the network: egress allow-list, no public database port, admin interfaces only from the admin network.
14. Turn on backups and run a restore test.
15. Run the acceptance checklist (section 10).

## 9. Configuration the installer sets (names, never values, in this document)

| Area | Setting names |
|---|---|
| Database and Supabase | `ConnectionStrings:PostgreSQL`, `Supabase:Url`, `Supabase:JwtSecret`, `Supabase:ServiceRoleKey`, `Supabase:DbHost`, `Supabase:DbPort`, `Supabase:DbUser`, `Supabase:DbPassword`, `Supabase:DbName`, `Supabase:StorageBucket` |
| Web access | `Bcp:CorsOrigins`, `Integrations:PublicApiUrl` |
| Document reading | `AzureDocumentIntelligence` section (endpoint, key) if used |
| Embeddings and AI | `AzureOpenAI` section, `OpenRouter` section, other provider sections. The AI model, prompt versions and embedding choice are admin settings inside the app |
| Cloud storage import | `Integrations:Google:ClientId` and `ClientSecret`, optional `Integrations:EncryptionKey` (set a long random value per client) |

## 10. Acceptance checklist
- [ ] Sign-in works, roles behave as designed, a second workspace cannot see the first.
- [ ] Upload, parse, extract and index finish for a regulation and a policy.
- [ ] Analysis runs and the report, Excel and PDF export are correct.
- [ ] Network capture or firewall log shows traffic only to the approved endpoints.
- [ ] No document text or AI prompts appear in application logs.
- [ ] Backup restore tested on a clean machine.
- [ ] Secrets not present in files that are in version control or backups of configuration without protection.
- [ ] Support access procedure tested.
- [ ] Update and rollback tested once.
- [ ] Client signs the acceptance report.

## 11. Responsibilities

| Item | Client | Us |
|---|---|---|
| Servers, network, TLS, DNS, OS patching | Yes | Advise |
| Supabase, database, storage, backups | Yes (we install and document) | Install, runbook |
| Azure and AI service accounts, keys, spend | Yes | Advise and configure |
| Application install and updates | Apply in their change window | Build, test, deliver, support |
| Users, roles, departments | Yes | Train |
| Content and decisions from the analysis | Yes | Not responsible (see Terms: decision support only) |
| Security review and penetration test of their environment | Yes | Support, fix findings in the product |

## 12. Not ready yet (engineering work before the first client install)

1. **Container and installer packaging.** There is no Dockerfile or compose file. Needed: container images (API, web, Docling service) and a
   documented self-hosted Supabase setup that works with the API.
2. **AI request data-protection flags.** DONE 15 Oct: every OpenRouter request carries zero retention and no training by default, with a clear error
   for models no provider serves under those rules (Qwen and the free models). Tested on the whole catalog, see privacy-settings.md. Still to do: the same
   check for each client's approved model list.
3. **Embedding default.** `appsettings.json` has set search embeddings to Azure OpenAI since 08 Oct. DONE 15 Oct as a profile: `bcp-api/appsettings.InHouse.example.json`
   sets `local`. The installer must apply it.
4. **Local or client-hosted AI.** `OpenAI:BaseUrl` plus a custom model name should reach an OpenAI-compatible server (for example vLLM), but this is untested.
   Test with a real self-hosted model and measure quality.
5. **Log hygiene audit.** DONE 15 Oct (none found, see privacy-settings.md). Repeat before each client release. A redaction setting for provider error bodies is still open.
6. **Documents sent to AI.** Standard steps send clause text and retrieved passages. The qualitative assessment can now be disabled
   (`Privacy:DisableQualitativeAssessment`, set in the in-house profile). Still to do: a written per-step data list for client approval.
7. **Update delivery.** Signed packages, version file, rollback steps.
8. **SSO.** Check whether the client's identity provider can sign users in through self-hosted Supabase (SAML or OIDC).
9. **Deletion.** Test full workspace deletion (documents, vectors, results, backups policy).
10. **Security evidence.** Architecture diagram, data-flow diagram, a short security whitepaper, a completed standard questionnaire, a
    third-party penetration test.
11. **Legal.** Data processing agreement template, sub-processor list for the hosted demo, support-access terms.

## 13. Open decisions
- Standard model to sell: B, with C on request?
- Which AI route is the default for B: client's Azure OpenAI or OpenRouter with strict flags?
- Pricing and support terms for in-house installs.
- Who owns update responsibility after go-live.
