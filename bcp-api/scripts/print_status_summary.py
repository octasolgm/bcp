import json
from pathlib import Path

report_path = Path(__file__).resolve().parents[1] / "SeedData" / "cbuae-demo-excel-merge-report.json"
data = json.loads(report_path.read_text(encoding="utf-8"))

print("OVERALL STATUS CHANGES:")
for m in data["matched"]:
    ch = m["changes"]
    if "overall_status" in ch:
        conf = ch.get("confidence", {})
        print(
            f"  {m['clause_no']:8} {ch['overall_status']['from']:14} -> {ch['overall_status']['to']:14}"
            f"  conf {conf.get('from', '-')} -> {conf.get('to', '-')}"
        )

print("\nSTATUS UNCHANGED (matched, other fields only):")
for m in data["matched"]:
    if "overall_status" not in m["changes"]:
        fields = ", ".join(k for k in m["changes"].keys())
        print(f"  {m['clause_no']:8} updated: {fields}")
