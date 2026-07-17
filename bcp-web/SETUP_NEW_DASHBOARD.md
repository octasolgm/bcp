# New Dashboard Setup (bcp-web /nd routes)

The enterprise compliance dashboard lives inside **bcp-web** at `/nd/*` (same app as the legacy Angular UI on port 3002).

> **Navigate to `/nd` to use the new dashboard.** The root `/` route still redirects to the legacy `/dashboard` — existing behavior is unchanged.

## Prerequisites

- **Node.js** 20+
- **bcp-api** running (API URL resolved via `environment.apiUrl` / `environment.ndApiUrl`)
- **Supabase** project with Auth enabled
- PostgreSQL schema from `bcp-api/scripts/supabase/005_enterprise_platform.sql` applied

## 1. Supabase

1. Create a Supabase project at [supabase.com](https://supabase.com).
2. Run the SQL migration in `bcp-api/scripts/supabase/005_enterprise_platform.sql` in the Supabase SQL editor.
3. Under **Authentication → URL configuration**, add:
   - Site URL: your bcp-web origin (e.g. `http://localhost:3002`)
   - Redirect URLs: `http://localhost:3002/**`
4. Copy from **Project Settings → API**:
   - Project URL → `supabaseUrl` in environment
   - `anon` public key → `supabaseAnonKey` in environment
5. For user invites (admin), the API also needs the Supabase **service role** key configured in `bcp-api` (see API setup).

## 2. Environment variables

Set values in `bcp-web/src/environments/environment.ts` (development) and `environment.production.ts` (production build):

```typescript
supabaseUrl: 'https://your-project.supabase.co',
supabaseAnonKey: 'your-anon-key',
ndApiUrl: '', // defaults to apiUrl when empty
appUrl: '',   // optional; forgot-password uses window.location.origin when empty
```

`apiUrl` and `ndApiUrl` are resolved automatically for localhost vs Azure via `api-url.ts`.

## 3. Install and run

```bash
cd bcp-web
npm install
npm start
```

Open [http://localhost:3002/nd](http://localhost:3002/nd) for the new dashboard.

Legacy routes (`/login`, `/dashboard`, etc.) are unchanged.

## 4. bcp-api configuration

Ensure `bcp-api` is running and configured for JWT validation:

```env
Supabase__JwtSecret=your-jwt-secret
Supabase__ProjectUrl=https://your-project.supabase.co
```

Use the existing `ConnectionStrings` / Postgres config — no separate New Dashboard connection string.

JWT secret is found in Supabase **Project Settings → API → JWT Settings**.

Start the API (from repo root):

```bash
cd bcp-api
dotnet run
```

Default API port is `5100` unless configured otherwise.

## 5. First super admin

1. Sign up a user in Supabase Auth (or use invite flow after an admin exists).
2. In Supabase SQL editor, set the profile role:

```sql
UPDATE profiles SET role = 'super_admin', full_name = 'Admin User' WHERE id = 'your-user-uuid';
```

3. Sign in at `/login` — you will land on the dashboard with full admin navigation.

## 6. Role overview

| Role | Capabilities |
|------|----------------|
| **super_admin** | Departments, users, all documents, libraries, analysis |
| **maker** | Upload docs, build libraries, run analysis, edit results |
| **checker** | Review queue — approve or pull back submissions |
| **reviewer** | Final review queue — finalize or return to checker |

## 7. Typical workflow

1. **Super admin** — create departments and invite users.
2. **Maker** — upload regulation PDFs, extract points, build libraries.
3. **Maker** — upload internal policy PDFs.
4. **Maker** — **Run Analysis** (library or manual points + internal docs).
5. Monitor progress at `/run-analysis/[runId]` (polls every 2s).
6. Review/edit action plans at `/results/[runId]`, export PDF/Excel.
7. Submit for checker review.
8. **Checker** — review at `/checker/review/[runId]`.
9. **Reviewer** — finalize at `/reviewer/review/[runId]`.

## 8. Production build

```bash
npm run build
npm start
```

Set the same env vars in your hosting provider. Update Supabase redirect URLs to your production domain.

## Troubleshooting

| Issue | Fix |
|-------|-----|
| Redirect loop on login | Check `NEXT_PUBLIC_SUPABASE_*` values and Site URL in Supabase |
| API 401 errors | Verify JWT secret in API matches Supabase; sign out and back in |
| Profile missing after login | API auto-creates profile on first `/nd/auth/profile` call (login page triggers this) |
| Upload fails | Confirm Supabase Storage is configured in `bcp-api` for internal documents |
| CORS errors | Ensure API allows your dashboard origin |

## Project structure

```
app/
  (auth)/          Login, password reset, invite acceptance
  (dashboard)/     Main app pages (sidebar layout)
components/
  shell/           Sidebar, TopNav, RoleBadge
  library/         LibraryBuilder (3-column point picker)
  analysis/        RunAnalysisForm (3-step wizard)
  results/         ResultsView with export and action plans
lib/
  api/             bcp-api-client.ts
  auth/            Server/client auth helpers
  export/          PDF and Excel export
  supabase/        Supabase SSR clients
```
