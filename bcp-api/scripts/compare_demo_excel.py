import json
import re
from pathlib import Path

import openpyxl

CONF_BY_STATUS = {
    "compliant": 0.86,
    "partial": 0.72,
    "non-compliant": 0.65,
}


def normalize_key(value):
    if not value:
        return ""
    trimmed = str(value).strip().replace("§", "").strip()
    while trimmed.endswith("."):
        trimmed = trimmed[:-1]
    return trimmed


def norm_status(s):
    if not s:
        return ""
    s = str(s).strip().lower()
    s = s.replace("non compliant", "non-compliant").replace("noncompliant", "non-compliant")
    if "non-compliant" in s or "non compliant" in s:
        return "non-compliant"
    if "partial" in s:
        return "partial"
    if "compliant" in s:
        return "compliant"
    return s


def split_policy(text):
    if not text:
        return []
    parts = re.split(r"\s*---\s*", str(text))
    return [p.strip() for p in parts if p and p.strip()]


def load_demo_clause_keys(seed):
    """Exact clause_no keys for the 55 demo points (base set)."""
    return {normalize_key(item["clause_no"]) for item in seed}


def is_excel_row_in_demo_scope(clause_no: str, demo_keys: set[str]) -> bool:
    raw = str(clause_no).strip()
    if not raw:
        return False
    if raw.upper().startswith("INT"):
        return False
    return normalize_key(raw) in demo_keys


def load_excel_rows(excel_path: Path, demo_keys: set[str]):
    wb = openpyxl.load_workbook(excel_path, read_only=True, data_only=True)
    ws = wb["Gap Analysis"]
    rows = []
    skipped = 0
    for i, row in enumerate(ws.iter_rows(min_row=11, values_only=True), start=11):
        clause = row[1]
        if clause is None or str(clause).strip() == "":
            continue
        clause_str = str(clause).strip()
        if not is_excel_row_in_demo_scope(clause_str, demo_keys):
            skipped += 1
            continue
        rows.append(
            {
                "row": i,
                "clause_no": clause_str,
                "interpretation": (row[3] or "").strip() if row[3] else "",
                "design": norm_status(row[5]),
                "operating": norm_status(row[6]),
                "overall": norm_status(row[7]),
                "document_reference": (row[8] or "").strip() if row[8] else "",
                "policy_extract_raw": row[9],
                "compliance_status": norm_status(row[13]),
                "conclusion": norm_status(row[14]),
            }
        )
    wb.close()
    return rows, skipped


def find_excel_match(item, excel_by_key):
    """Match only by demo clause_no — never merge INT or sub-clause rows."""
    return excel_by_key.get(normalize_key(item["clause_no"]))


def build_excel_updates(item, ex):
    policy = split_policy(ex["policy_extract_raw"])
    new_design = ex["design"] or ex["compliance_status"] or ex["conclusion"]
    new_operating = ex["operating"] or ex["compliance_status"] or ex["conclusion"]
    new_overall = ex["overall"] or ex["compliance_status"] or ex["conclusion"] or new_design

    updated = dict(item)
    changes = {}

    if new_design:
        if new_design != item.get("design_status"):
            changes["design_status"] = {"from": item.get("design_status"), "to": new_design}
        updated["design_status"] = new_design
    if new_operating:
        if new_operating != item.get("operating_status"):
            changes["operating_status"] = {"from": item.get("operating_status"), "to": new_operating}
        updated["operating_status"] = new_operating
    if new_overall:
        if new_overall != item.get("overall_status"):
            changes["overall_status"] = {"from": item.get("overall_status"), "to": new_overall}
        updated["overall_status"] = new_overall

    if policy:
        old_policy = item.get("policy_extract") or []
        if policy != old_policy:
            changes["policy_extract"] = {
                "from_count": len(old_policy),
                "to_count": len(policy),
            }
        updated["policy_extract"] = policy

    if ex["interpretation"] and ex["interpretation"] != item.get("interpretation"):
        changes["interpretation"] = {
            "from_preview": (item.get("interpretation") or "")[:120],
            "to_preview": ex["interpretation"][:120],
        }
        updated["interpretation"] = ex["interpretation"]

    if ex["document_reference"] and ex["document_reference"] != item.get("document_reference"):
        changes["document_reference"] = {
            "from": item.get("document_reference"),
            "to": ex["document_reference"],
        }
        updated["document_reference"] = ex["document_reference"]

    new_conf = CONF_BY_STATUS.get(updated.get("overall_status") or "")
    if new_conf and new_conf != item.get("confidence"):
        changes["confidence"] = {"from": item.get("confidence"), "to": new_conf}
        updated["confidence"] = new_conf

    return updated, changes


def compare(seed_path: Path, excel_path: Path):
    with open(seed_path, encoding="utf-8") as f:
        seed = json.load(f)

    demo_keys = load_demo_clause_keys(seed)
    excel_rows, excel_skipped = load_excel_rows(excel_path, demo_keys)
    excel_by_key = {normalize_key(er["clause_no"]): er for er in excel_rows}

    matched = []
    not_in_excel = []

    for item in seed:
        ex = find_excel_match(item, excel_by_key)
        if not ex:
            not_in_excel.append(
                {
                    "clause_no": item["clause_no"],
                    "clause_title": item.get("clause_title"),
                    "overall_status": item.get("overall_status"),
                    "confidence": item.get("confidence"),
                }
            )
            continue

        _, changes = build_excel_updates(item, ex)
        matched.append(
            {
                "clause_no": item["clause_no"],
                "clause_title": item.get("clause_title"),
                "excel_row": ex["row"],
                "excel_status": {
                    "design": ex["design"],
                    "operating": ex["operating"],
                    "overall": ex["overall"],
                },
                "will_change": bool(changes),
                "changes": changes,
            }
        )

    return seed, matched, not_in_excel, excel_rows, excel_skipped


def apply(seed_path: Path, excel_path: Path):
    seed, matched, not_in_excel, _, _ = compare(seed_path, excel_path)
    demo_keys = load_demo_clause_keys(seed)
    excel_rows, _ = load_excel_rows(excel_path, demo_keys)
    excel_by_key = {normalize_key(er["clause_no"]): er for er in excel_rows}

    updated_seed = []
    for item in seed:
        ex = find_excel_match(item, excel_by_key)
        if not ex:
            updated_seed.append(item)
            continue
        updated, _ = build_excel_updates(item, ex)
        updated_seed.append(updated)

    with open(seed_path, "w", encoding="utf-8") as f:
        json.dump(updated_seed, f, indent=2, ensure_ascii=False)
        f.write("\n")

    return matched, not_in_excel


def main():
    root = Path(__file__).resolve().parents[1]
    seed_path = root / "SeedData" / "cbuae-aml-demo-judgments.json"
    excel_path = Path(r"c:\Users\Pc\Downloads\Tester-4_gap_analysis 0508.xlsx")
    report_path = root / "SeedData" / "cbuae-demo-excel-merge-report.json"

    seed, matched, not_in_excel, excel_rows, excel_skipped = compare(seed_path, excel_path)

    print("=== DEMO 55-POINT EXCEL MATCH (strict clause_no only) ===")
    print(f"Demo points (base): {len(seed)}")
    print(f"Excel rows scanned (skipped INT / non-55): {excel_skipped}")
    print(f"Excel rows matching demo 55: {len(excel_rows)}")
    print(f"FOUND in Excel: {len(matched)}")
    print(f"NOT FOUND in Excel (keep demo as-is): {len(not_in_excel)}")
    print(f"Would update judgment fields: {sum(1 for m in matched if m['will_change'])}")
    print(f"Found but already aligned: {sum(1 for m in matched if not m['will_change'])}")

    report_path.write_text(
        json.dumps(
            {
                "summary": {
                    "demo_point_count": len(seed),
                    "excel_rows_skipped": excel_skipped,
                    "excel_rows_in_demo_scope": len(excel_rows),
                    "found_in_excel": len(matched),
                    "not_found_in_excel": len(not_in_excel),
                    "would_update": sum(1 for m in matched if m["will_change"]),
                    "found_already_aligned": sum(1 for m in matched if not m["will_change"]),
                },
                "matching_rules": [
                    "Base set is exactly the 55 demo clause_no values.",
                    "Excel INT rows and sub-clauses (e.g. 3.4-a) are ignored.",
                    "Only judgment fields update: status, confidence, policy_extract, interpretation, document_reference.",
                    "clause_no and clause_title (reg point identity) are never changed.",
                    "gap_description / suggested_action kept unless Excel has action columns filled.",
                ],
                "not_found_in_excel": not_in_excel,
                "found_in_excel": matched,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"Report: {report_path}")


if __name__ == "__main__":
    main()
