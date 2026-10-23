"""Export our live analysis setup into client-baseline.json for the client package (read-only).

Reads the database in bcp-api/appsettings.Development.json and writes:
  - settings: the analysis system settings in SETTING_KEYS (AI model, pipeline version, ...)
  - prompts:  the current version of every analysis prompt
The API applies this file on client installs at startup (bcp-api/Infrastructure/ClientBaselineSeeder.cs).

Usage: python deploy/client-package/export-baseline.py <output path>
Needs: pip install "psycopg[binary]"
"""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import psycopg

# Only analysis behaviour; never billing, keys or per-user preferences.
SETTING_KEYS = [
    "regul_workflow_llm",
    "dual_verify_llm",
    "regul_pipeline_version",
    "regul_gap_check_skip_low",
    "regul_retrieval_prompt_cache",
    "regul_embedding_provider",
]


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("Usage: export-baseline.py <output path>")
    repo = Path(__file__).resolve().parents[2]
    dev = json.loads((repo / "bcp-api" / "appsettings.Development.json").read_text(encoding="utf-8-sig"))
    conn_str = dev["ConnectionStrings"]["PostgreSQL"]

    with psycopg.connect(conn_str, connect_timeout=30) as conn:
        settings = {
            key: json.loads(value)
            for key, value in conn.execute(
                "select key, value_json::text from nd_system_settings where key = any(%s) order by key",
                (SETTING_KEYS,),
            ).fetchall()
        }
        prompts = [
            {"key": key, "versionNumber": number, "label": label, "text": text}
            for key, number, label, text in conn.execute(
                "select prompt_key, version_number, label, prompt_text from nd_analysis_prompt_versions "
                "where is_current order by prompt_key"
            ).fetchall()
        ]

    if not prompts:
        sys.exit("No current prompt versions found; refusing to write an empty baseline.")

    baseline = {
        "exportedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "settings": settings,
        "prompts": prompts,
    }
    out = Path(sys.argv[1])
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(baseline, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"  settings: {', '.join(f'{k}={json.dumps(v)}' for k, v in settings.items())}")
    for p in prompts:
        print(f"  prompt {p['key']}: v{p['versionNumber']} {p['label'][:60]}")


if __name__ == "__main__":
    main()
