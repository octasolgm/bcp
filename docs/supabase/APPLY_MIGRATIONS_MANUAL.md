# Apply Supabase migrations manually

`npm run db:migrate` needs a working `DATABASE_URL`. If it fails with `ENOTFOUND db.xxx.supabase.co`, use the Supabase SQL Editor instead.

## Steps

1. Open [Supabase Dashboard](https://supabase.com/dashboard) → your project → **SQL Editor**
2. **Fast path:** open `docs/supabase/migrations/RUN_002_003_combined.sql`, copy all, paste, click **Run**

   Or run each file separately:

| Order | File |
|-------|------|
| 1 | `docs/supabase/migrations/001_landing_ai_cache.sql` (skip if you already have `landing_ai_jobs`) |
| 2 | `docs/supabase/migrations/002_compliance_sessions.sql` |
| 3 | `docs/supabase/migrations/003_dual_verify_kafka.sql` |

3. Restart the API: `npm run dev:api`
4. Open `/landing-ai/kafka-dual-verify` — **Save: Supabase** should show green

## After migrations

- Kafka dual verify results persist in `dual_verify_sessions` + `dual_verify_point_jobs`
- Full reloadable reports in `landing_ai_compliance_sessions`
- Disk backup still runs as fallback (`data/dual-verify-kafka/`)

## Fix DATABASE_URL (for `npm run db:migrate`)

Your `.env` currently has placeholder `password` in `DATABASE_URL`. Replace it:

1. Supabase → **Settings** → **Database** → **Connection string** → URI
2. Copy the full URI (with real database password) into root `.env` as `DATABASE_URL=...`
3. Run `npm run db:migrate` from repo root

If direct host `db.xxx.supabase.co` does not resolve, use the **Session pooler** URI from the same page.
