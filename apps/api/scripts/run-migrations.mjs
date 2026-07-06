/**
 * Run pending SQL migrations from docs/supabase/migrations.
 * Usage: npm run db:migrate  (from apps/api or repo root)
 */
import pg from 'pg';
import { readdirSync, readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import dotenv from 'dotenv';

const __dirname = dirname(fileURLToPath(import.meta.url));

dotenv.config({ path: join(__dirname, '..', '.env') });
dotenv.config({ path: join(__dirname, '..', '..', '..', '.env') });

function resolveMigrationsDir() {
  const candidates = [
    join(__dirname, '..', '..', '..', 'docs', 'supabase', 'migrations'),
    join(process.cwd(), 'docs', 'supabase', 'migrations'),
    join(process.cwd(), '..', '..', 'docs', 'supabase', 'migrations'),
  ];
  for (const dir of candidates) {
    try {
      const files = readdirSync(dir).filter((f) => f.endsWith('.sql'));
      if (files.length > 0) return dir;
    } catch {
      /* next */
    }
  }
  throw new Error('docs/supabase/migrations not found');
}

function buildConnectionString() {
  const direct = process.env.DATABASE_URL?.trim();
  if (direct) {
    try {
      const u = new URL(direct);
      if (u.password === 'password' || u.password === 'your-password') {
        throw new Error(
          'DATABASE_URL still has placeholder password. Supabase → Settings → Database → Connection string (URI) → paste real password into .env',
        );
      }
    } catch (e) {
      if (e instanceof Error && e.message.includes('placeholder')) throw e;
    }
    return direct;
  }

  const password = process.env.SUPABASE_DB_PASSWORD?.trim();
  const url = process.env.SUPABASE_URL?.trim();
  if (!password || !url) {
    throw new Error(
      'Set DATABASE_URL or SUPABASE_URL + SUPABASE_DB_PASSWORD in .env',
    );
  }
  const ref = new URL(url).hostname.split('.')[0];
  return `postgresql://postgres:${encodeURIComponent(password)}@db.${ref}.supabase.co:5432/postgres`;
}

async function main() {
  const migrationsDir = resolveMigrationsDir();
  const files = readdirSync(migrationsDir)
    .filter((f) => f.endsWith('.sql'))
    .sort();

  const client = new pg.Client({
    connectionString: buildConnectionString(),
    ssl: { rejectUnauthorized: false },
  });

  await client.connect();
  try {
    await client.query(`
      CREATE TABLE IF NOT EXISTS schema_migrations (
        id SERIAL PRIMARY KEY,
        name TEXT NOT NULL UNIQUE,
        applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
      );
    `);

    const { rows: doneRows } = await client.query('SELECT name FROM schema_migrations');
    const done = new Set(doneRows.map((r) => r.name));

    if (done.size === 0 && files.length > 0) {
      const { rows: regRows } = await client.query(
        "SELECT to_regclass('public.landing_ai_jobs') AS reg",
      );
      if (regRows[0]?.reg) {
        const baseline = files.find((f) => f.includes('landing_ai'));
        if (baseline) {
          await client.query(
            'INSERT INTO schema_migrations (name) VALUES ($1) ON CONFLICT DO NOTHING',
            [baseline],
          );
          done.add(baseline);
          console.log(`baseline ${baseline} (tables already existed)`);
        }
      }
    }

    let applied = 0;

    for (const file of files) {
      if (done.has(file)) {
        console.log(`skip  ${file} (already applied)`);
        continue;
      }
      const sql = readFileSync(join(migrationsDir, file), 'utf-8');
      console.log(`apply ${file}…`);
      await client.query('BEGIN');
      try {
        await client.query(sql);
        await client.query('INSERT INTO schema_migrations (name) VALUES ($1)', [file]);
        await client.query('COMMIT');
        applied++;
        console.log(`done  ${file}`);
      } catch (error) {
        await client.query('ROLLBACK');
        throw error;
      }
    }

    if (applied === 0) {
      console.log('All migrations up to date.');
    } else {
      console.log(`Applied ${applied} migration(s).`);
    }
  } finally {
    await client.end();
  }
}

main().catch((e) => {
  console.error('Migration failed:', e.message);
  console.error(
    'Tip: copy DATABASE_URL from Supabase → Settings → Database → Connection string (URI).',
  );
  process.exit(1);
});
