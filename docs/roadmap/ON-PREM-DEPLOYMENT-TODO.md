# On-Prem / Private Deployment - TODO

## Why

The landing page claims "Deployment: On-premise / private," but today auth
(`SupabaseJwtValidator`) and file storage (`SupabaseStorageService`) are both
hard-tied to hosted Supabase. This doc captures the plan to close that gap:
a real private/on-prem deployment option, sitting alongside the existing
hosted system, without changing default behavior for current clients.

Not started yet. No code has been touched for this - this is the plan to
pick up later.

## Goal

Add a private/on-prem deployment mode, gated by config, defaulting to the
current hosted (Supabase) behavior so nothing changes for existing clients
until explicitly turned on.

## 1. Storage abstraction

- Introduce `IStorageService` (Upload/Download/CreateSignedUrl/Delete -
  mirror `SupabaseStorageService`'s existing public surface at
  `bcp-api/Services/Storage/SupabaseStorageService.cs`).
- `SupabaseStorageService` implements it unchanged.
- New `LocalDiskStorageService` implementation - saves files to a
  configurable folder on the server; "signed URL" replaced with a
  short-lived, token-gated local file endpoint (local disk has no native
  signed-URL concept).
- Update constructor parameter types from concrete `SupabaseStorageService`
  to `IStorageService` across the consuming files:
  - Controllers: `DocumentsController.cs`, `LocalDocumentsController.cs`,
    `InternalDocumentsController.cs`, `RegulationDocumentsController.cs`,
    `RunGapEvidenceController.cs`, `AnalysisPointAttachmentsController.cs`
  - Services: `NdRegulationUploadService.cs`, `NdInternalParseService.cs`,
    `NdAnalysisProcessor.cs`, `NdRegulAnalysisProcessor.cs`,
    `NdStoredDocumentUploadService.cs`, `CompliancePdfResolver.cs`,
    `TfsGuidelinesSeedService.cs`, `AnalysisBundleSeedService.cs`,
    `Pdf/PdfNativePageDocumentLoader.cs`,
    `Pdf/NdDocumentPageReferenceResolver.cs`, `NdDemoWorkspaceService.cs`
- DI registration in `Program.cs` (currently lines ~169-177) picks
  implementation based on new config flag.

## 2. Auth abstraction

- Introduce `IAuthTokenValidator` interface; `SupabaseJwtValidator`
  (`bcp-api/Infrastructure/NewDashboard/SupabaseJwtValidator.cs`) implements
  it unchanged.
- New `LocalAuthTokenValidator` for genuinely local login: own
  username/password store, own password hashing, locally-issued and
  locally-verified tokens - no calls to Supabase Auth. (Deferred design
  decision: whether local users live in a new table or reuse the existing
  `NdProfile`/users table - decide when this phase is actually picked up.)
- Update the single choke point `NdControllerBase.cs`
  (`ValidateJwt`/`RequireAuthAsync`/`RequireAuthWithUserAsync`, currently
  lines ~82, 101-105, 158-162) plus the ~20 ND controller constructors from
  concrete `SupabaseJwtValidator` to the new interface.
- DI registration in `Program.cs` (currently lines ~180-192) picks
  implementation based on new config flag.

## 3. Config pattern

- Follow the existing `Bcp:*` boolean-flag +
  `BcpConfiguration.GetString/IsTrue/IsFalse` convention already used for
  `RequireSupabase`/`AllowSqlite`/`UsePostgres`
  (`bcp-api/Infrastructure/DatabaseConfig.cs`).
- New flags, e.g. `Bcp:AuthMode` ("supabase" | "local") and
  `Bcp:StorageMode` ("supabase" | "local"), both defaulting to `"supabase"`
  - zero behavior change unless explicitly switched.
- New config section for local storage (e.g. `LocalStorage: { RootPath }`),
  following the same nesting style as `Supabase:*`/`LandingAi:*`.
- Mirror the existing example-file convention
  (`appsettings.NewDashboard.example.json` etc.) for any new local-mode
  config keys, so secrets stay out of git.

## 4. Database

- Already independently toggleable (SQLite/Postgres via `DatabaseConfig`) -
  no changes needed beyond ensuring `Bcp:AllowSqlite` is enabled when
  running fully on-prem.

## 5. Existing reusable building block

- `bcp-api/Services/LocalDocs/LocalDocumentExtractionService.cs` and
  related classes already run fully locally with zero Supabase coupling
  (parsing/OCR) - a working precedent/template for the local-storage and
  local-auth providers to follow. Note `LocalDocumentsController.cs` (the
  controller around it) still depends on `SupabaseStorageService`/
  `SupabaseJwtValidator` today - that's the first place the new interfaces
  would get exercised once built.

## Rollout note

Build the interfaces and local providers, keep the default config on
`"supabase"` so the existing hosted deployment is untouched; only turn on
`"local"` mode for a specific on-prem client deployment once tested.
