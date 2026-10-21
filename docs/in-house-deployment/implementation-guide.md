# Implementation guide: in-house deployment and licensing

Status: not started. Written 20 Oct 2026 as the hand-off for whoever builds this later (a developer or an AI coding session).
Read in this order: [full-plan.md](full-plan.md), [licensing-design.md](licensing-design.md), [licensing-overview.md](licensing-overview.md),
[azure-deployment.md](azure-deployment.md), [pricing-ai-keys-profit.md](pricing-ai-keys-profit.md), then this file. Older background:
[README.md](README.md) (install runbook, settings, acceptance checklist), [../roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md](../roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md)
and [../roadmap/ON-PREM-DEPLOYMENT-TODO.md](../roadmap/ON-PREM-DEPLOYMENT-TODO.md).

Code paths below were checked on 20 Oct 2026 (branch `release/2026.10.19.02`); line numbers drift, so search by name.

## 1. Ground rules (do not break these)

1. **Hosted behaviour must not change.** Everything new sits behind one setting, for example `Licensing:Mode` = `hosted` (default) or
   `inhouse`. With `hosted`, every existing test passes and nothing on bcp-api-dev / bcp-web-dev changes.
2. **Demo accounts stay untouched** (repo `CLAUDE.md`): do not change demo seed data, `NdDemoInterceptionService`, `NdDemoWorkspaceService`,
   `AnalysisBundleSeedService`, `DemoAnalysisSeedService`, or how demo accounts render. In-house builds may simply not include demo accounts.
3. **Workspaces:** licensing is **install-wide** (one license covers all workspaces of an install), so license and install tables are
   platform-level, not `ITenantScoped`. Usage counters for limits must sum across all workspaces of the install (use
   `IgnoreQueryFilters()` deliberately and comment why). Any new table that holds client data must still follow the `ITenantScoped` rules in
   `CLAUDE.md` and `bcp-api/Infrastructure/NdWorkspaceSchemaBootstrap.cs`.
4. **No secrets in the repo.** The signing private key never exists outside Azure Key Vault. Only public keys go in code.
5. **A missing or invalid license in `inhouse` mode = Locked**, never unlimited.
6. **Never block reading or exporting existing data** because of license state.
7. Update the weekly report in `docs/weekly-reports/` at the end of each session (repo `CLAUDE.md`).

## 2. Work packages and order

| WP | What | Needed for first bank? | Effort |
| --- | --- | --- | --- |
| 1 | Container packaging | Yes | 1.5 to 2 weeks |
| 2 | License core in the API + web | Yes | 2 weeks |
| 3 | `license-tool` CLI with Key Vault signing | Yes | 3 to 4 days |
| 4 | Offline extras: vouchers, hash chain, clock check, usage export | Yes | 1 week |
| 5 | Release packaging, signing, `updatesUntil` | Yes | 1 week |
| 6 | Azure deploy script and Bicep | First Azure bank | 1 to 1.5 weeks (route A first) |
| 7 | Online check-in + license server | Second or third bank | 3 weeks |
| 8 | Billing automation | Later | 1 to 2 weeks |

## 3. WP1: container packaging

- Dockerfiles: `bcp-api` (.NET 8 runtime image), `bcp-web` (build Angular, serve with nginx), `docling-service` (Python).
- **Tesseract on Linux:** the API image must `apt-get install tesseract-ocr` plus language data, and the native library must load
  (`../roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md` notes the NuGet package only ships Windows binaries). Add a startup self-test that OCRs a tiny
  bundled image and logs a clear error if it returns nothing.
- `docker-compose.yml` for a single server: web, api, docling + the self-hosted Supabase stack (PostgreSQL with pgvector, GoTrue auth,
  storage, gateway). Secrets from an `.env` file that is git-ignored.
- API runs with the in-house profile (`bcp-api/appsettings.InHouse.example.json`: local embeddings, outbound allow-list, OpenRouter zero
  retention, qualitative assessment off).
- Health: `/health/startup` and `/health/version` already exist in `bcp-api/Program.cs`; use them in compose health checks.
- Done when: `docker compose up` on a clean Linux VM gives a working portal; upload, parse, extract and analysis work end to end; network
  capture shows only allowed hosts.

## 4. WP2: license core

New folder `bcp-api/Services/Licensing/` (suggested names):

| Piece | Responsibility |
| --- | --- |
| `LicenseDocument` | Model of the JSON in [licensing-design.md](licensing-design.md) section 3 |
| `LicenseVerifier` | Canonicalise JSON, verify ECDSA P-256 signature with the public key for `keyId` (keys embedded in code, two allowed at once for rotation), check `installId` |
| `InstallIdentity` | Create the install id on first start, store it (new platform-level table, for example `nd_install`) |
| `LicenseStore` | Persist the current license (table, for example `nd_license`) and its history; load on start; reload hourly and on upload |
| `LicenseStateService` | Compute Active / Low / AllowanceUsed / Grace / ReadOnly / Locked from the license, counters and clock |
| `LicenseUsageService` | Period counters: clauses, documents, users, workspaces, AI credits, across all workspaces of the install |
| `LicenseGate` | One helper the controllers call before new work: returns null or a 402/403 result with a clear message |

Gate points (search for these):

| Action | Where today | Notes |
| --- | --- | --- |
| Start analysis run | `bcp-api/Controllers/NewDashboard/AnalysisRunsController.cs` (existing credit guard uses `summary.IsExhausted`; clause guard uses `usage.Fits(toRun)`) | Add license gate next to these; keep existing guards |
| Gap-evidence re-run | `GapEvidenceRerunsController.cs` (existing `IsExhausted` check) | Same |
| Regulation upload | `RegulationDocumentsController.cs` `[HttpPost("upload")]` | Documents-per-period limit |
| Internal document upload | `InternalDocumentsController.cs` `[HttpPost("upload")]` | Same counter |
| Invite / activate user | `UsersController.cs` `[HttpPost("invite")]`, `[HttpPost("{id:guid}/activate")]` | `maxUsers` |
| Create workspace | `WorkspacesController.cs` `[HttpPost]` | `maxWorkspaces` |
| Engines / features | Engine controllers under `Controllers/NewDashboard/LocalDocumentsController.cs` etc. | `features.engines`, `dualVerify`, `qualitativeAssessment` |

Existing pieces to reuse, not duplicate:

- Clause counting: `NdAiCreditService.GetClauseUsageAsync` (`bcp-api/Services/NewDashboard/Ai/NdAiCreditService.cs`) counts
  `ProcessedPointsCount` per workspace, lifetime. Add a period window (`validFrom`..`paidThrough`) and an install-wide variant.
- Credits: `NdAiCreditService.GetSummaryAsync` and the ledger `nd_ai_credit_ledger` (`Data/NewDashboard/AiCreditEntities.cs`). In
  `aiMode = own`, keep recording usage rows but do not block on credits.
- Workspace limits: `NdWorkspace.AiCreditLimit`, `ClauseLimit` (`Data/NewDashboard/WorkspaceEntities.cs`). In in-house mode these are set
  from the license, not by an admin.

Disable in `inhouse` mode (platform-admin endpoints in `AiCreditsController.cs`, guarded by `RequirePlatformAdminAsync` from
`NdControllerBase.cs`): `PUT pricing`, `POST workspaces/{id}/top-up`, `PUT .../credit-limit`, `PUT .../clause-limit`, `PUT .../markup`.
Return a clear "managed by license" message. Credits arrive only through signed vouchers.

New API (admin of the install only):

| Endpoint | Purpose |
| --- | --- |
| `GET /nd/license` | State, dates, plan label, each counter as used/allowed, install id |
| `POST /nd/license` | Upload a `.lic` (offline) |
| `POST /nd/license/vouchers` | Redeem a voucher |
| `GET /nd/license/usage-report` | Signed monthly usage export (offline) |

Web (`bcp-web`): a License page under admin; state banners in the ND shell (`bcp-web/src/app/pages/nd/nd-shell.component.ts`); hide credit
top-up and pricing screens in in-house mode. Respect the existing `auth.isDemoViewer()` nav rules; do not change demo behaviour.

Done when: unit tests show a changed field fails verification, wrong install id locks, each state transition happens at the right date
(fake clock), each gate blocks with the right message, reads and exports still work in Read-only and Locked, and with `Licensing:Mode =
hosted` all existing tests pass unchanged.

## 5. WP3: `license-tool` CLI

- .NET console app in `tools/license-tool/` (the repo already has a `tools/` folder).
- Commands: `issue`, `voucher`, `verify`, `show`. Plan templates as JSON in `tools/license-tool/plans/` (trial, starter, standard,
  enterprise); `--set key=value` overrides.
- Signing through Azure Key Vault (`Azure.Security.KeyVault.Keys` `CryptographyClient`, ES256 over the canonical JSON); the key is
  non-exportable. Writes every issued license and voucher to an append-only local log (later: the license server database).
- Done when: `issue` then `verify` round-trips; the API accepts the file; editing one character makes both reject it.

## 6. WP4: offline extras

- Vouchers: table of redeemed `voucherId`s; one redemption per voucher, only for its install; adds to a pack balance (clauses/documents) or
  a credit top-up row in the ledger.
- Hash chain: add `prev_hash` and `row_hash` to `nd_ai_credit_ledger` and to a usage-event table; verify on start and before every export;
  a broken chain sets Locked.
- Clock check: store the highest time seen (DB + signed local file); clock more than 24 hours behind = invalid.
- Usage export: install key pair created at first start (public part sent to us with the install id); export file = counters + ledger
  head hash, signed with the install key.

## 7. WP5: releases and updates

- Release manifest (version, build date, image digests, checksums) signed with our key; checked by the installer and the Azure script.
- The API refuses to start new work when its build date is after the license's `updatesUntil` (build date can come from the existing
  deploy version info, `Infrastructure/DeployVersionInfo.cs`).
- Rule for in-house releases: schema changes only add tables/columns (the schema bootstrap classes in `bcp-api/Infrastructure/` already
  create and upgrade tables on start).

## 8. WP6: Azure script

Follow [azure-deployment.md](azure-deployment.md): `deploy-inhouse-azure.ps1` + Bicep under a new folder such as `deploy/azure/`. Route A
(VM + compose) first. Must support `-WhatIf`, install, `-Upgrade`; generate secrets into Key Vault; verify the signed manifest; print the
install id. Keep it separate from the hosted deploy scripts in `bcp-api/scripts/` and `bcp-web/scripts/`.

## 9. WP7 and WP8 (later)

- License server: small separate service we host. Customers, installs, plans, billing periods, issued licenses, vouchers, received usage;
  check-in endpoint; per-bank OpenRouter key management (OpenRouter management API: create key with `limit` and `limit_reset`, disable,
  read usage); Key Vault signing.
- Online check-in in the API: daily job, payload in [licensing-design.md](licensing-design.md) section 7, `nextCheckInBy` + 7-day tolerance.
- Billing: invoices, mark paid, reminders, overage and AI credit lines ([licensing-overview.md](licensing-overview.md) section 9).

## 10. Decisions still open (ask the owner before building)

See the "Open decisions" lists in each document. The ones that change code: `onLimit` default (block or overage), whether packs expire,
grace days, period reset rules, archive length, Route A or B on Azure, certified model list.
