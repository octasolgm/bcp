"""Print human-readable demo-55 vs Excel match report."""
import json
from pathlib import Path

report_path = Path(__file__).resolve().parents[1] / "SeedData" / "cbuae-demo-excel-merge-report.json"
data = json.loads(report_path.read_text(encoding="utf-8"))

print("NOT FOUND IN EXCEL (5 demo points — keep as-is):")
for x in data["not_found_in_excel"]:
    print(f"  {x['clause_no']}")

print()
print("FOUND IN EXCEL:")
for m in data["found_in_excel"]:
    flag = "CHANGE" if m["will_change"] else "OK"
    st = m.get("excel_status", {})
    overall = st.get("overall") or "-"
    fields = ", ".join(m["changes"].keys()) if m["will_change"] else "already aligned"
    print(f"  [{flag}] {m['clause_no']:10} row {m['excel_row']:3}  excel={overall:14}  {fields}")
