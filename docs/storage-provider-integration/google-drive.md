# Google Drive integration

Part of the storage provider integration, see [README.md](README.md) for the overall design.
Status: Google side set up and the feature built on 15 Oct 2026. Tested locally up to the Google sign-in screen and with
simulated Google responses; a full sign-in with a real test user is the next check.

## 1. Environments and addresses

| Environment | API address (Google redirects here) | Web address |
|---|---|---|
| Local | `http://localhost:5100` | `http://localhost:3002` |
| Dev and production | `https://bcp-api-dev.azurewebsites.net` | `https://bcp-web-dev.azurewebsites.net` |

Production currently uses the same addresses as dev, so one Google client covers both and nothing extra is
registered for production. If production moves to its own address later, see section 7.

Callback address pattern (planned): `{API address}/nd/integrations/google/callback`

- `http://localhost:5100/nd/integrations/google/callback`
- `https://bcp-api-dev.azurewebsites.net/nd/integrations/google/callback`

## 2. What was created in Google Cloud Console

Owner: the Gmail account that created the project. Whoever owns it controls the keys and the app, so keep that
account long term and use two-step sign-in.

| Item | Value |
|---|---|
| Project | `comply-solutions` (name: Comply Solutions) |
| OAuth client type | Web application |
| Client ID | `451146350287-69itro2enal7809kki7rm1kaf0nv3h6o.apps.googleusercontent.com` (public, not a secret) |
| Client secret | held only in server configuration, never in git, chat or this document |
| Publishing status | Testing |

The client ID is public: it appears in the address of the Google sign-in pop-up. The secret is the private part.

## 3. Setup steps (how it was done)

1. console.cloud.google.com, sign in with the owner Gmail account, accept the terms.
2. Create a project named Comply Solutions. Billing is not needed for these steps, even though the console offers a
   free trial banner. Do not start the trial.
3. APIs and Services > Library: enable Google Drive API. Also enable Google Picker API if the file picker is used.
4. Google Auth Platform > Branding:
   - App name: Comply Solutions
   - User support email and developer contact email: set
   - Logo: skip for now (adding one forces a verification step)
   - Application home page: optional while in Testing; later a public address on a domain we own
   - Privacy policy and terms links: optional while in Testing; required to publish
   - Authorized domains: leave empty while in Testing. This field takes bare domain names only (no `https://`, no
     path) and only domains we can prove we own. `azurewebsites.net` cannot be verified.
5. Google Auth Platform > Audience: user type External, add test users (the owner and any tester accounts).
6. Google Auth Platform > Data Access: add the scope (section 4).
7. Google Auth Platform > Clients > Create client > Web application, name Comply Solutions web.
   Under Authorized redirect URIs add the two full callback addresses from section 1 (with `http://` or `https://`).
   Optionally under Authorized JavaScript origins add `http://localhost:3002` and
   `https://bcp-web-dev.azurewebsites.net` (only needed if the Google file picker runs in the browser).
8. Create. Copy the client secret straight away into the server configuration (section 5).

## 4. Which permission (scope) to request

| Option | Scope | What the user does | Google review |
|---|---|---|---|
| Picker (recommended first) | `https://www.googleapis.com/auth/drive.file` | Chooses files in Google's own picker. We only see files they pick. | Not restricted. Quick. |
| Full browsing | `https://www.googleapis.com/auth/drive.readonly` | Browses folders in our own import dialog | Restricted scope. Verification, and for apps that store the data on servers usually an annual third-party security assessment, which can cost money. Check Google's current terms. |

Decision: the app requests `drive.readonly`, because the import dialog is our own folder browser (the same for every
provider), and `drive.file` cannot list files the app did not create. While the app is in Testing this needs no review for
listed test users. The review and possible paid assessment only matter before real clients connect; if that cost is not
acceptable, the fallback is the Google file picker with `drive.file`, which needs a different import screen for Google.
Both are read-only: we never change or delete a client's files. Add `.../auth/drive.readonly` under Data Access.

## 5. Where the secret goes (and the other settings)

Local file `bcp-api/appsettings.Development.json` is ignored by git. The deploy script pushes it to the Azure app
settings. Planned block, filled in by the platform owner:

```json
"Integrations": {
  "Google": {
    "ClientId": "451146350287-69itro2enal7809kki7rm1kaf0nv3h6o.apps.googleusercontent.com",
    "ClientSecret": "paste-the-secret-here"
  }
}
```

Other settings, all optional:

| Setting | Meaning |
|---|---|
| `Integrations:EncryptionKey` | Key that protects stored sign-ins and the sign-in `state`. If empty, one is derived from `Supabase:JwtSecret`. Set a long random value to separate them. Changing it makes stored connections unreadable, so users reconnect. |
| `Integrations:PublicApiUrl` | Overrides the address used for the callback if the app sits behind a proxy that hides its real address. |
| `Bcp:CorsOrigins` | Also the list of web addresses allowed to start a connection (already set for localhost:3002 and bcp-web-dev). |

Deploy: `scripts/sync-secrets.ps1` copies the `Integrations` section from `appsettings.Development.json` into the
`appsettings.Secrets.json` that ships in the zip, so the deploy carries it to Azure.

Never put the secret in a file that is committed, in a screenshot, or in a chat. If it leaks, open the client in
Google Auth Platform > Clients, add a new secret, update the server configuration, then disable the old one.

## 6. How a workspace admin uses it

1. Admin settings > Integrations > Google Drive > Connect. (The Integrations page is for real workspace admins; demo
   accounts do not see it.)
2. A pop-up opens on Google. Sign in with their own account and click Allow. Because the app is in Testing the first
   screen says the app is unverified: the account must be a listed test user, then Continue.
3. The card shows Connected with the account name.
4. On Internal Documents or Regulation Documents (all four versions of those pages), Import from cloud, next to Upload >
   open folders, search, tick files (up to 25 at a time) > Import. Admin and maker can import. Files arrive as Pending
   parse. Parse and Extract stay manual.
   Google Docs are converted to Word on import. Files over 50 MB, or that are not PDF/Word/Google Doc, are skipped with a
   reason. A file already in your documents (same content) is reported as already there instead of being added twice.
5. Disconnect removes the stored tokens and revokes access at Google. Recent imports are listed on the Integrations page.

## 7. Going to production and going live to clients

Same addresses as dev: nothing to change in Google.

If production gets its own API address, in Google Auth Platform > Clients open the Web client and add
`https://NEW-API-ADDRESS/nd/integrations/google/callback` under Authorized redirect URIs, then save. Put the client
ID and secret in the new environment's server configuration. Google rejects a redirect address that is not listed
exactly.

Before real clients (not just test users) can connect:

1. Own a domain and host a public home page, privacy policy and terms on it.
2. Verify the domain in Google Search Console and list it under Authorized domains.
3. Add the logo, then Audience > Publish app.
4. Complete Google verification for the chosen scope (section 4).

While in Testing:

- only listed test users (maximum 100) can connect
- the connection expires after 7 days and the user must reconnect
- an unverified-app warning page appears

## 8. Cost

| Item | Cost |
|---|---|
| Project, Drive API, Picker API, OAuth client | Free |
| Verification for `drive.file` | Free |
| Security assessment for `drive.readonly` (if required) | Paid, annual |

## 9. Problems seen and fixes

| Message | Cause | Fix |
|---|---|---|
| "Invalid domain: must not specify the scheme" | Redirect addresses were pasted into Authorized domains | Clear that field. Redirect addresses go under Clients > Authorized redirect URIs |
| "Missing domain: bcp-web-dev.azurewebsites.net" | A home page address needs its domain listed, and azurewebsites.net cannot be verified | Clear the home page field while in Testing, or use a domain we own |
| `redirect_uri_mismatch` when connecting | The address Google received is not listed exactly | Add the exact address under Authorized redirect URIs (scheme, host, port and path must match) |
| `access_denied` or "app not verified" | The account is not a test user | Add it under Audience > Test users |
| Connection stops working after about a week | Testing mode expires tokens after 7 days | Reconnect, or publish the app |
| `invalid_client` | Wrong client ID or secret in configuration | Check the `Integrations:Google` block |

## 9a. Decision (15 Oct 2026): demo only, no Google verification

Real clients run Comply Solutions in-house (see ../in-house-deployment/README.md). Our Google app therefore only serves the hosted
demo and evaluation portal. We do NOT pursue Google verification, the paid security assessment, or custom domains for the app and API.
- The app stays in Testing (test users listed, reconnect weekly) or is published unverified (anyone can connect behind Google's
  "unverified app" warning, capped at 100 users).
- For an in-house client, the client registers its own app: a Google Workspace "Internal" app (no verification) or a single-tenant
  Microsoft Entra app in its own tenant, and its own callback address goes in its server configuration.
- Because many banks use Microsoft 365, OneDrive and SharePoint is the likely next provider to build.

## 10. Open decisions

- `drive.file` (picker) or `drive.readonly` (full browsing)?
- Domain to use for the home page, privacy policy and terms before going live.
- Whether production will get its own API address and its own Google client.
