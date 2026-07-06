#!/usr/bin/env node
/**
 * Verify Kafka dual-verify pipeline prerequisites (no paid AI calls).
 * Usage: node apps/api/scripts/verify-kafka-dual-verify-flow.mjs
 */
const API = process.env.API_BASE ?? 'http://localhost:4000';

async function get(path) {
  const res = await fetch(`${API}${path}`);
  const text = await res.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    throw new Error(`${path} → invalid JSON (${res.status})`);
  }
  return { ok: res.ok, status: res.status, json };
}

function pass(msg) {
  console.log(`✓ ${msg}`);
}

function fail(msg) {
  console.error(`✗ ${msg}`);
}

async function main() {
  console.log(`Verifying Kafka dual verify at ${API}\n`);
  let errors = 0;

  try {
    const health = await get('/dual-verify-kafka/health');
    if (!health.ok) throw new Error('health failed');
    const p = health.json.data?.persistence;
    if (p?.mode === 'supabase') pass(`Persistence: Supabase`);
    else if (p?.mode === 'file') pass(`Persistence: disk (${p.fileDataDir})`);
    else {
      fail(`Persistence: ${p?.mode ?? 'unknown'} — paid runs may lose data`);
      errors++;
    }
    if (p?.dualVerifyTablesReady) pass('dual_verify_* tables ready');
    else {
      fail('dual_verify_* tables missing');
      errors++;
    }
    if (p?.complianceSessionsTableReady) pass('landing_ai_compliance_sessions ready');
    else {
      fail('compliance sessions table missing');
      errors++;
    }
  } catch (e) {
    fail(`Health: ${e.message}`);
    errors++;
  }

  try {
    const gov = await get('/landing-ai/stored-points?docId=gov-tfs-guidelines');
    const count = gov.json.points?.length ?? 0;
    if (gov.ok && count > 0) pass(`Gov points loaded: ${count}`);
    else {
      fail('Gov points empty — seed builtin docs');
      errors++;
    }
  } catch (e) {
    fail(`Gov points: ${e.message}`);
    errors++;
  }

  try {
    const sessions = await get(
      '/landing-ai/compliance-sessions?limit=5&granularity=dual-leaf',
    );
    if (sessions.json.diagnostics?.sessionsTableReady) {
      pass('Compliance sessions API OK');
    } else {
      fail('Compliance sessions table not ready');
      errors++;
    }
  } catch (e) {
    fail(`Compliance sessions: ${e.message}`);
    errors++;
  }

  try {
    const kafkaSessions = await get('/dual-verify-kafka/sessions');
    if (kafkaSessions.ok) pass('Kafka sessions list API OK');
    else {
      fail('Kafka sessions API failed');
      errors++;
    }
  } catch (e) {
    fail(`Kafka sessions: ${e.message}`);
    errors++;
  }

  console.log('');
  if (errors === 0) {
    console.log('All checks passed. Safe to run Kafka dual verify.');
    console.log('UI: http://localhost:3000/landing-ai/kafka-dual-verify');
    process.exit(0);
  }
  console.log(`${errors} check(s) failed. Fix before running paid jobs.`);
  process.exit(1);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
