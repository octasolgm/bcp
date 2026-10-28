"""Copy 94-point judgments to main seed file and regenerate frontend clause list."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "SeedData" / "cbuae-aml-demo-judgments-94.json"
DST = ROOT / "SeedData" / "cbuae-aml-demo-judgments.json"
TS = ROOT.parent / "bcp-web" / "src" / "lib" / "nd" / "demo-cbuae-seed-clauses.ts"

data = json.loads(SRC.read_text(encoding="utf-8"))
DST.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

def esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace("'", "\\'").replace("\n", "\\n").replace("\r", "")

rows_ts = []
for row in data:
    cn = esc(row["clause_no"])
    ct = esc(row.get("clause_title") or "")
    rows_ts.append(f"  {{ clauseNo: '{cn}', clauseTitle: '{ct}' }},")

functions = '''
/** Align with backend DemoAnalysisSeedService.NormalizeClauseKey. */
export function normalizeDemoClauseKey(value: string): string {
  let trimmed = value.trim().replace(/^§\\s*/, '');
  while (trimmed.endsWith('.')) trimmed = trimmed.slice(0, -1);
  return trimmed;
}

export function buildDemoCbuaeJudgmentKeySet(): Set<string> {
  const keys = new Set<string>();
  for (const row of DEMO_CBUAE_SEED_CLAUSES) {
    keys.add(normalizeDemoClauseKey(row.clauseNo));
    if (row.clauseTitle?.trim()) keys.add(normalizeDemoClauseKey(row.clauseTitle));
  }
  return keys;
}

export type DemoGovPointLike = {
  point_id: string;
  title?: string | null;
  text?: string | null;
  section?: string | null;
  pointNumber?: string | null;
};

function parentClauseKey(clauseNo: string): string | null {
  const m = clauseNo.match(/^(.+)-[a-z]$/i);
  return m ? normalizeDemoClauseKey(m[1]) : null;
}

export function matchGovPointsToDemoCbuaeScope(
  points: DemoGovPointLike[],
  resolveDisplayNumber: (point: DemoGovPointLike) => string,
): string[] {
  const judgmentKeys = buildDemoCbuaeJudgmentKeySet();
  const used = new Set<string>();
  const ids: string[] = [];

  for (const point of points) {
    const num = resolveDisplayNumber(point).trim();
    const title = (point.title ?? '').trim();
    const section = (point.section ?? '').trim();

    let matched = false;
    if (num && judgmentKeys.has(normalizeDemoClauseKey(num))) matched = true;
    if (!matched && title && judgmentKeys.has(normalizeDemoClauseKey(title))) matched = true;
    if (!matched && section && judgmentKeys.has(normalizeDemoClauseKey(section))) matched = true;
    if (!matched && num) {
      const parent = parentClauseKey(normalizeDemoClauseKey(num));
      if (parent && judgmentKeys.has(parent)) matched = true;
    }
    if (!matched) continue;

    const dedupeKey = normalizeDemoClauseKey(num || title || point.point_id);
    if (used.has(dedupeKey)) continue;
    used.add(dedupeKey);
    ids.push(point.point_id);
  }

  return ids;
}
'''

header = f"""/** CBUAE AML demo judgments — mirrors SeedData/cbuae-aml-demo-judgments.json ({len(data)} clauses). */
export const DEMO_CBUAE_SEED_CLAUSES = [
"""
footer = f"] as const;\n\nexport const DEMO_CBUAE_ANALYSIS_POINT_COUNT = DEMO_CBUAE_SEED_CLAUSES.length;\n"
TS.write_text(header + "\n".join(rows_ts) + footer + functions, encoding="utf-8")
print(f"Updated {DST} ({len(data)} rows)")
print(f"Updated {TS}")
