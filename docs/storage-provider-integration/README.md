# Storage provider integration (OneDrive, Google Drive, Dropbox, Box, pCloud)

Status: built for Google Drive on 15 Oct 2026 (workspace Integrations page, Connect pop-up, Import from cloud on the
four Internal and Regulation document pages, import log). OneDrive and SharePoint, Dropbox, Box and pCloud are
not built yet and appear as Coming soon. Real sign-in needs the provider's client id and secret in server configuration.

This document explains how a workspace will connect its own cloud storage account and import documents from it
into Comply Solutions, and why the platform needs a one-time app registration with each provider.

Provider guides: [Google Drive](google-drive.md)

## 1. What the user gets

1. A workspace admin opens Admin settings > Integrations and sees a list of providers.
2. They click Connect next to a provider. A pop-up opens on the provider's own page.
3. They sign in with their own account and click Allow. The pop-up closes and the card shows Connected.
4. On Internal Documents or Regulation Documents, an Import from cloud button opens a file browser for that
   account. They open folders, tick files and click Import.
5. The files arrive as normal documents with status Pending parse. Parse and Extract stay manual, as today.

Each workspace connects its own account. One workspace never sees another workspace's connection or files.

## 2. Why the platform must register an app (and why the admin cannot do it for us)

The Connect pop-up is the provider's standard "Sign in and allow this app" screen. A provider only shows that
screen to an app it knows. The app registration is how Comply Solutions becomes known to the provider. It gives us:

- a client ID: the public name of our app, shown on the consent screen ("Comply Solutions wants to read your files")
- a client secret: a private key that proves later requests really come from our app
- a list of callback addresses: the only places the provider is allowed to send the user back to after sign-in

It is the same thing as the registration behind any "Sign in with Google" or "Sign in with Microsoft" button.

Two different jobs, two different people:

| | Who | How often | What |
|---|---|---|---|
| App registration | Platform owner | Once per provider, ever | Registers Comply Solutions with the provider and gets the client ID and secret |
| Connect | Each workspace admin | When they want | Clicks Connect, signs in with their own account, clicks Allow |

What the app registration does not do:

- It does not give the platform access to any client's files. A client's files can only be reached after that
  client's own admin clicks Allow with their own account.
- It does not use the platform owner's own storage account.
- Workspaces never see or enter the client ID or secret.

Why not let each workspace admin create their own registration instead? It is possible (a "bring your own app"
mode), but it would make every client create a developer project, a consent screen and a secret before they can
click Connect. That is slow, easy to get wrong and hard to support. The single platform registration is what makes
the Connect pop-up a one-click step for clients. Bring-your-own-app can be added later for a client with strict
security rules that forbids third-party apps.

One registration is per provider. A Google registration cannot cover Microsoft, Dropbox, Box or pCloud, because they
are separate companies. Within one provider one registration covers all environments (local, dev, production) by
listing several callback addresses.

## 3. The flows

### 3.1 One-time setup (platform owner)

1. Register the app with the provider (section 5).
2. Add the client ID and secret to the server configuration (or the Platform settings > Integrations page, if we
   choose to enter them in the browser).
3. Platform settings > Integrations shows the provider as Ready. Until then workspaces do not see the provider.

### 3.2 Connect (workspace admin)

1. Admin settings > Integrations > Connect on the provider card.
2. The server creates a signed one-time `state` value (workspace, user, provider, expiry) and a PKCE challenge where
   the provider supports it, and returns the provider's authorization address.
3. The pop-up opens that address. The user signs in and clicks Allow.
4. The provider sends the browser back to our callback address with a one-time code and the `state`.
5. The server checks the `state` (signature, expiry, same user, used once), exchanges the code for tokens, reads
   the account name for display, stores the tokens encrypted for that workspace, and closes the pop-up.

### 3.3 Import (admin or maker)

1. Import from cloud > choose a connection > browse folders (the server lists them using the stored token).
2. The user ticks files (PDF, Word) and clicks Import, choosing Internal or Regulation.
3. The server downloads each file from the provider and runs it through the same preparation and storage as a
   normal upload (type check, size limit 50 MB, hash, storage path), creating the same document record with status
   Pending parse. A file whose hash already exists is reported as already imported.
4. Each import is written to an audit log: who, which file, from which provider and account, when.

### 3.4 Disconnect (workspace admin)

Deletes the stored tokens and asks the provider to revoke access where it supports that. Already imported
documents stay, because they are our own copies.

## 4. Security rules

- Read-only permissions only. We never ask to write, change or delete the client's files.
- Tokens are stored encrypted, scoped to the workspace, and never sent to the browser.
- `state` is signed, expires in minutes and can be used once, to stop forged callbacks.
- PKCE is not used: the server holds a client secret (a confidential client), which is what PKCE is meant to replace for
  browser-only apps. The signed one-time `state` protects the round trip.
- Only a workspace admin can connect or disconnect. Admin and maker can import (same as upload today).
- Demo accounts get no integrations at all.
- New tables follow the workspace rules: they carry `tenant_id`, are tenant scoped, and are added to the workspace
  schema bootstrap list.
- Imports are rate limited and size limited so a connection cannot be used to flood storage.
- A token that stops working (revoked, expired, password changed) marks the connection Needs reconnect instead of
  failing silently.

## 5. Provider setup (platform owner, once each)

Callback address pattern (planned): `{API address}/nd/integrations/{provider}/callback`

| Environment | API address |
|---|---|
| Local | `http://localhost:5100` |
| Dev | `https://bcp-api-dev.azurewebsites.net` |
| Production | to be added |

Provider ids: `microsoft`, `google`, `dropbox`, `box`, `pcloud`.

### Microsoft (OneDrive and SharePoint), Azure portal

1. Microsoft Entra ID > App registrations > New registration. Name: Comply Solutions.
2. Supported account types: accounts in any organizational directory (multi-tenant) so client tenants can connect.
3. Redirect URI (Web): the callback addresses above with `microsoft`.
4. Certificates and secrets > New client secret. Copy the value now.
5. API permissions (delegated, Microsoft Graph): `Files.Read.All`, `Sites.Read.All`, `User.Read`, `offline_access`.
6. Some client tenants require their IT admin to approve the app once. That is normal and is the client's choice.

### Google Drive, Google Cloud Console

1. Create a project (Comply Solutions).
2. APIs and services > enable Google Drive API.
3. OAuth consent screen: app name, logo, support email, privacy policy address.
4. Credentials > Create OAuth client ID > Web application. Add the callback addresses with `google`.
5. Scope requested: `https://www.googleapis.com/auth/drive.readonly`.
6. While the app is in Testing, only listed test users can connect. For any client, Google reviews the app.
   `drive.readonly` is a restricted scope, so expect a verification process that can take days or weeks.
   A narrower option is the Google file picker with `drive.file`, which needs no restricted-scope review but lets
   the user choose files in Google's own picker instead of our browser.

### Dropbox, dropbox.com/developers

1. Create app > Scoped access > Full Dropbox (or App folder).
2. Permissions: `files.metadata.read`, `files.content.read`, `account_info.read`.
3. Add the callback addresses with `dropbox`. Copy the app key (client ID) and app secret.

### Box, developer.box.com

1. Create platform app > Custom app > User Authentication (OAuth 2.0).
2. Application scopes: read all files and folders.
3. Add the callback addresses with `box`. Copy client ID and client secret.
4. The client's Box admin may need to authorize the app for the enterprise.

### pCloud, docs.pcloud.com

1. Create an app in the pCloud developer console. Copy the app key and app secret.
2. Add the callback addresses with `pcloud`. pCloud has no fine-grained scopes, so access is to the whole account;
   state this clearly on the Connect screen.
3. pCloud accounts are tied to a data region (US or EU) and the API address differs. The connection stores it.

## 6. What we store (planned)

| Table | Holds |
|---|---|
| `nd_cloud_connections` | workspace, provider, account name, encrypted refresh token, expiry, status, who connected, when |
| `nd_cloud_import_log` | workspace, connection, provider file id, file name, imported by, stored document id, kind, time |

Provider client IDs and secrets live in server configuration (or encrypted platform settings), never in the browser
and never per workspace.

## 7. Build phases

1. Foundation: tables, token encryption, provider interface, settings pages, connect pop-up, import dialog and
   audit log, with a simulated provider for tests. Then Microsoft (OneDrive and SharePoint).
2. Google Drive.
3. Dropbox, Box and pCloud.

Nothing is shown to workspace users until a provider has real credentials.

## 8. Open decisions

- Where do platform credentials live: server configuration only, or an encrypted Platform settings form?
- Google: full read-only browsing (needs restricted-scope review) or the Google file picker (no review)?
- Do we want bring-your-own-app for clients that forbid third-party apps?
- Production API address for the callback list.

## 9. FAQ

Do clients need a Google, Microsoft or other developer account? No. They only sign in with their normal account.

Can the platform see a client's files after they connect? Only through the read-only connection, only when a user
of that workspace browses or imports, and every import is logged. Disconnecting removes access.

Why did I get an "unverified app" or "admin approval required" screen? The provider is protecting the client. For
Google it means the app is still in Testing or unreviewed. For Microsoft and Box it means the client's IT admin
must approve the app once.

What happens if the connection breaks? The card shows Needs reconnect. Click Connect again.
