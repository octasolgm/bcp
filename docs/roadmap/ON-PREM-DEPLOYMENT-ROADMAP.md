# On-Prem / Private Deployment - Full Roadmap

Not implemented yet. This is a planning document for scheduling and discussion
with the client and team lead - no code has been touched for any of this.

## Why this matters

The client is a bank. Privacy is the first priority, not a nice-to-have. The
document-parsing work done this session (PdfPig + Tesseract OCR, replacing
Landing AI) removed the one place document *content* was leaving the network
to a third-party AI service. That was a necessary step, but it is only one
piece. Today, several other parts of the platform still depend on hosted
cloud services and would need to move onto the bank's own infrastructure
before this can be called a genuine on-prem deployment.

## Current state (as of today)

- **Document parsing/OCR is fully local** - PdfPig (text-layer PDFs),
  Tesseract (OCR for scanned pages), PDFtoImage (page rendering), OpenXml
  (.docx). Zero network calls per document, zero per-page cost. See
  `bcp-api/Services/LocalDocs/`.
- **Database** is Supabase-hosted PostgreSQL (AWS, Japan region).
- **File storage** is Supabase Storage - every uploaded document's bytes are
  sent to Supabase's cloud object storage over HTTP
  (`bcp-api/Services/Storage/SupabaseStorageService.cs`).
- **Authentication** is Supabase Auth - login and user lookups call
  Supabase's hosted Auth API directly
  (`bcp-api/Infrastructure/NewDashboard/SupabaseJwtValidator.cs`).
- **Background messaging** (the dual-verify worker) points at Azure Service
  Bus, used as a Kafka-compatible broker.
- **Known platform gap:** the `Tesseract` NuGet package only ships Windows
  native binaries. Production `bcp-api` is deployed to a **Linux** Azure App
  Service (`bcp-api/DEPLOYMENT.md`), so OCR would silently return empty text
  there today. Needs a decision (Windows hosting, or a Docker image that
  installs `tesseract-ocr` via apt) before this matters for any real
  deployment, cloud or on-prem.

## Target architecture for a private/on-prem client

| Layer | Today (cloud) | On-prem target | Notes |
|---|---|---|---|
| App hosting | Azure App Service (Linux) | Client's own Windows or Linux server/VM, inside their private network, no internet egress required | .NET 8 runs on both. If Linux is chosen, `tesseract-ocr` must be installed on the host or baked into a container image. |
| Database | Supabase-hosted Postgres (AWS) | Self-hosted PostgreSQL on client infrastructure | Already toggle-ready in code (SQLite/Postgres via `DatabaseConfig`) - Postgres is the right on-prem choice for a multi-user bank deployment, not SQLite. |
| File storage | Supabase Storage (cloud HTTP API) | Self-hosted Supabase Storage (same code, writes to local disk on the client's server) | Recommended path - near-zero code change, see Path A below. |
| Authentication | Supabase Auth (hosted, calls out to supabase.co) | Self-hosted Supabase Auth (same code, same calls, internal URL) | Recommended path - see Path A below. |
| Document parsing/OCR | Already fully local as of today | No change needed | Just needs the right native binaries for whatever OS it ends up hosted on. |
| Background messaging (dual-verify) | Azure Service Bus (Kafka-compatible) | Self-hosted Kafka, or drop/defer this worker if the bank doesn't need it day one | Confirm requirement with the client before building this out. |

## Two paths for Auth + Storage + Database together

**Path A - self-host Supabase's own open-source stack (recommended).**
Supabase's self-hosted release is the same software as the cloud version -
Postgres, the Storage API, and Auth (GoTrue) - packaged as Docker
containers that run on infrastructure we (or the client) control, not on
Supabase's cloud. Running it this way is not a cut-down or "lite" version;
it is the real thing, just not managed by Supabase.

The app's code barely changes for this path. `SupabaseStorageService` and
`SupabaseJwtValidator` already just make plain HTTP calls to a Supabase
URL (today: `https://prxmkrmwqxlltwjnazay.supabase.co`). Self-hosting only
means that URL becomes an address inside the client's own network (e.g.
`https://supabase.bank-internal.local`) instead of Supabase's cloud - same
calls, same code, different address and credentials. This is genuinely a
small, low-risk change, not a rewrite.

**Windows Server works too.** Supabase's containers are built for Linux,
but Docker on Windows Server can run Linux containers via Hyper-V
isolation (or WSL2) - a standard, supported Docker feature, not a
workaround. So the whole self-hosted Supabase stack can run on a Windows
Server box if that is what the client has, though a Linux host is simpler
if one is available.

**Path B - build fully independent local providers**, as already scoped in
`docs/roadmap/ON-PREM-DEPLOYMENT-TODO.md`: a `LocalDiskStorageService` and
a `LocalAuthTokenValidator`, config-gated alongside the existing Supabase
implementations, defaulting to unchanged behavior for current hosted
clients. Meaningfully more engineering work than Path A, and only worth it
if the client specifically needs to say "no Supabase software runs
anywhere in this deployment, self-hosted or not" for their own
vendor-risk/audit policy - not a security difference, a procurement/audit
one. If the client is comfortable with well-audited open-source software
run entirely inside their own network with no internet egress, Path A is
equally private and far less work.

**Recommendation: lead with Path A.** It is materially less work, reuses
already-tested code, and - as long as it runs fully inside the client's
network with no internet access configured - is just as private as Path B.
Only fall back to Path B if the client's own compliance/vendor policy
specifically rules out running Supabase (even self-hosted).

## How a bank's private/on-prem deployment actually works

For context, since this is the first time this platform is being scoped
for on-prem: a bank's "private server" typically means -

- A physical server or VM inside the bank's own data center or private
  network - not on AWS, Azure, or any public cloud, and with no public IP
  or inbound internet access at all.
- Owned and managed by the bank's own IT/security team - patching,
  backups, firewall rules, physical security are all theirs, not ours.
- Reachable only from inside their network - an employee reaches the app
  the same way they'd reach any other internal banking system: office
  network, VPN, or intranet.
- Outbound internet access is usually locked down too, often entirely -
  which is exactly why every remaining cloud dependency above (Supabase
  cloud, Azure Service Bus) has to be replaced. Those calls would either be
  blocked outright, or require an internet-access exception someone at the
  bank has to formally approve, which most banks try hard to avoid.

**What "we deploy it for them" looks like in practice:** we hand over a
deployable package - a Docker Compose bundle is the natural shape (our
app + self-hosted Supabase + Postgres, pinned versions) - plus a runbook.
Their IT team installs and runs it on their own hardware inside their
network. After that, we typically do not have standing access to their
servers; updates ship to them as new package versions, which their team
applies on their own schedule (sometimes through a formal change-approval
process, which is normal for banks).

## Storage, specifically, under self-hosted Supabase

Self-hosted Supabase Storage is not a separate cloud product - by default
it writes uploaded files straight to a local disk folder on the same
server (it can also be pointed at an S3-compatible store if the bank
already has one, e.g. for redundancy across multiple app instances, but
that is optional, not required). In a single-server on-prem deployment,
an uploaded document's bytes sit on a disk physically inside the bank's
building the entire time - same as the database - and never cross the
internet at any point.

## Phased rollout plan

- **Phase 0 - done.** Local parsing/OCR (this session). Proves the local
  pipeline works end to end without Landing AI or any per-document cloud
  call.
- **Phase 1 - OCR platform fix.** Resolve the Linux/Tesseract binary gap (or
  settle the Windows vs Linux vs Docker hosting decision first, which
  settles this automatically).
- **Phase 2 - Stand up self-hosted Supabase (Path A).** Deploy Supabase's
  own open-source Docker Compose stack (Postgres + Storage + Auth) on
  target infrastructure, confirm the app works unchanged against it by
  pointing config at the self-hosted URL instead of the cloud one. Low
  code risk - mostly infrastructure and config work. If the client's
  policy specifically rules out running Supabase software even
  self-hosted, fall back to Path B instead: introduce `IStorageService` /
  `IAuthTokenValidator`, ship `LocalDiskStorageService` /
  `LocalAuthTokenValidator` alongside the existing Supabase
  implementations, gated by config flags defaulting to unchanged behavior.
  Full file-by-file task breakdown for Path B already written up in
  `ON-PREM-DEPLOYMENT-TODO.md`.
- **Phase 3 - Database.** Confirm Postgres-only for on-prem (not SQLite),
  document the self-hosted Postgres setup the client's ops team will run
  (sizing, backup schedule, restore drill). Covered by Phase 2 if Path A
  is chosen, since Postgres ships inside the self-hosted Supabase stack.
- **Phase 4 - Messaging.** Decide the on-prem story for the dual-verify
  worker/Kafka with the client - self-hosted broker, or drop/defer.
- **Phase 5 - Packaging.** Produce a repeatable on-prem installer/runbook.
  Likely end state: a Docker Compose bundle (app + Postgres + storage +
  self-hosted Supabase), pinned versions, a single `docker compose up` for
  the client's ops team to run.
- **Phase 6 - Security/compliance pass.** Network isolation review, confirm
  no outbound internet requirement, patch/update process, backup/restore
  drill, access control review - the specific things a bank's IT/security
  team will ask for before go-live.
- **Phase 7 - Pilot.** Run in the client's actual environment, then hand off
  documentation and runbooks to their ops team.

## Open questions for the client conversation

- Windows Server or Linux server/VM available - and are containers (Docker)
  allowed on their infrastructure?
- Do they already run PostgreSQL with a DBA team we should integrate with,
  or do they want it bundled with our deployment?
- Is dual-verify (Kafka-based) required on day one, or can it be deferred or
  dropped for the on-prem build?
- Update/patch expectations - do they pull new versions themselves on their
  own schedule, or do we ship periodic builds to them?
- Do they have an existing identity provider (LDAP / Active Directory /
  SAML) they'd rather integrate with instead of a from-scratch local login
  system?

## Related docs

- `docs/roadmap/ON-PREM-DEPLOYMENT-TODO.md` - file/line-level task breakdown
  for the storage and auth code abstractions (Phases 2-3 above).
- `bcp-api/DEPLOYMENT.md` - current (cloud, Azure) deployment instructions,
  for contrast against the on-prem target.
- `bcp-api/Services/LocalDocs/` - today's local parsing/OCR implementation,
  the working precedent that proves fully local processing is viable here.
