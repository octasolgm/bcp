# Client delivery package (internal guide)

How we build the package a client uses to install Comply Solutions into their own Azure, and to install updates.
The client-facing instructions are `client-files/README.html`, printed to `README.pdf` inside every package.

## What the client gets

One zip, the same for every client:

```
ComplySolutions-v1.2.0.zip
  README.pdf              install + update guide (from client-files/README.html)
  CHANGELOG.txt           from -Notes
  install.ps1             first-time install: creates plan + 2 App Services, saves settings, deploys
  deploy-patch.ps1        updates: deploys files only, never touches settings
  settings-template.json  client copies to settings.json and fills in their own keys
  backend.zip             dotnet publish output, our settings files removed
  frontend.zip            Angular build, assets/app-config.json = {} (filled per install at deploy time)
  lib/common.ps1          shared script code
  version.json            version, build time, commit
  checksums.txt           SHA-256 of the zips and scripts, checked by both scripts
```

Plus `ComplySolutions-v1.2.0.zip.sha256`: send that value in a separate message so the client can check the download.

Nothing client-specific and none of our keys go into the package. Client values live in their App Service settings:
API settings come from their `settings.json` at install; the web app's four public values (`supabaseUrl`,
`supabaseAnonKey`, `ndApiUrl`, `appUrl`) are stored as web app settings and written into `assets/app-config.json`
inside the frontend zip on every deploy.

## What a client install runs

Only the live product: **New analysis** (`/nd/analyse-regul-full-v2`) and the **Documents** pages on Azure Document
Intelligence (`*-documents-azure-di`), plus overview, reviews, libraries and admin.

- **Hidden on client installs:** older analysis versions (v8, v9, regul, regul-full, Analysis Version), the Landing AI /
  Tesseract / RapidOCR / Docling pages and the legacy `/old` pages. The deploy scripts write `"edition": "client"` into
  `app-config.json`; the web app then hides the Analysis Version menu item and `ndDenyClientEditionGuard` redirects those
  routes to New analysis or the Azure documents page. Our hosted web app has no edition, so nothing changes there.
- **Keys the client provides:** OpenRouter (analysis), Azure Document Intelligence (document reading) and Azure OpenAI
  with a `text-embedding-3-small` deployment (passage search, the same embeddings we use live).
- **Not configured, no keys:** Kafka (`KAFKA_ENABLED=false`), Landing AI, Gemini, Docling and the other engines.
- **Same analysis setup as live:** the build exports the current prompt versions and the analysis settings (model,
  pipeline version, gap re-check, dual-verify model) from the database in `appsettings.Development.json` into
  `client-baseline.json` inside `backend.zip`, read-only (`export-baseline.py`). With `ClientBaseline__Enabled=true`
  (set by `install.ps1`) the API applies it at startup (`ClientBaselineSeeder`). It replaces values the system set, so
  each patch brings our latest prompts, and keeps anything the client's own admin changed.

## Build a release

From the repo root, on committed code:

```powershell
.\deploy\client-package\build-client-package.ps1 -Version 1.2.0 -Notes "Faster analysis; fixed report export"
```

- `-Notes` items separated by `;` become CHANGELOG bullet points.
- Use a new version number for every package you send. `-Force` rebuilds an existing number (only before it was sent).
- The version is also stamped into the API (`/health/version`) and the web build's `deploy-version.json`, so the patch script can show before/after.
- The build fails if any of our keys from `bcp-api/appsettings.Development.json` appear in the output.
- Output goes to `deploy/client-package/out/` (gitignored). Keep every package you send; clients roll back with the previous one.

Needs: .NET 8 SDK, Node/npm with `bcp-web/node_modules` installed, Microsoft Edge (prints the PDF), Python with
`pip install "psycopg[binary]"` (baseline export).

## Changing the package

| Change | Where |
| --- | --- |
| Client instructions | `client-files/README.html` (`{{VERSION}}` and `{{DATE}}` are filled in at build) |
| New API setting every client needs | `install.ps1` (`$apiSettings`) and `settings-template.json`; one-off values can go in `extraApiSettings` |
| Deploy steps | `client-files/lib/common.ps1` (shared by install and patch) |
| Files removed from the API build | `$removePatterns` in `build-client-package.ps1` |

A new required setting means existing clients must re-run `install.ps1` (with an updated `settings.json`) for that version
instead of `deploy-patch.ps1`. Say so in the notes.

## Delivery checklist

1. Build from a clean, committed branch; note the commit in your release log.
2. Test `install.ps1` once on our own test subscription for each new major change, and `deploy-patch.ps1` on an existing install.
3. Send the zip (shared folder or download link) and, separately, the SHA-256 value.
4. For a first install, offer a screen-share session; the client runs the script, we guide.

## Known limits

- App Services are Windows (.NET 8), same as our hosted `bcp-api-dev` / `bcp-web-dev` (Tesseract OCR ships Windows binaries).
- Kafka is switched off for client installs (`KAFKA_ENABLED=false`); dual-verify runs in local mode.
- A fresh Supabase project gets its tables from the API's startup bootstrap; the first install on an empty project should be
  tested on our side before the first client.
- First administrator is made with one SQL statement in Supabase (README section 6); there is no bootstrap-admin setting yet.
