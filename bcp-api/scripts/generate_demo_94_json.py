"""Generate 94-point demo analysis JSON: regulation text from API/DB, judgments from Excel."""
import json
import re
import sys
from pathlib import Path

import openpyxl

CONF_BY_STATUS = {
    "compliant": 0.86,
    "partial": 0.72,
    "non-compliant": 0.65,
}

REG_DOC_ID = "26085dad-6d2b-44a5-90b4-534f12d1cd22"
REG_POINTS_CACHE = Path(__file__).resolve().parents[1] / "SeedData" / "regulation-points-extract.json"
EXCEL_PATH = Path(r"c:\Users\Pc\Downloads\Tester-4_gap_analysis 0508 - Copy.xlsx")
TRANSCRIPT_PATH = Path(
    r"C:\Users\Pc\.cursor\projects\c-Users-Pc-Documents-GitHub-bcp-new\agent-transcripts"
    r"\e3715424-de7b-478c-8fcc-29916a9f9cbe\e3715424-de7b-478c-8fcc-29916a9f9cbe.jsonl"
)


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


def clean_interpretation(raw: str) -> str:
    text = (raw or "").strip()
    if "</interpretation>" in text:
        text = text.split("</interpretation>")[0].strip()
    return text


def extract_policy_from_cell(interpretation_raw: str, policy_raw) -> list[str]:
    policy = split_policy(policy_raw)
    if policy:
        return policy
    text = interpretation_raw or ""
    match = re.search(r'<parameter name="policy_extract">(\[[\s\S]*?\])', text)
    if not match:
        return []
    try:
        embedded = json.loads(match.group(1))
    except json.JSONDecodeError:
        return []
    if isinstance(embedded, list):
        return [str(p).strip() for p in embedded if str(p).strip()]
    return []


CLAUSE_REG_ALIASES = {
    "5": "4.1.1",
}


def split_policy(text):
    if not text:
        return []
    parts = re.split(r"\s*---\s*", str(text))
    return [p.strip() for p in parts if p and p.strip()]


def load_reg_points_from_transcript():
    if not TRANSCRIPT_PATH.exists():
        return None
    decoder = json.JSONDecoder()
    with open(TRANSCRIPT_PATH, encoding="utf-8") as f:
        for line in f:
            if "pointNumber" not in line:
                continue
            obj = json.loads(line)
            text = obj["message"]["content"][0]["text"]
            pos = 0
            while True:
                start = text.find("{", pos)
                if start < 0:
                    break
                try:
                    payload, end = decoder.raw_decode(text, start)
                except json.JSONDecodeError:
                    pos = start + 1
                    continue
                if (
                    isinstance(payload, dict)
                    and payload.get("success")
                    and isinstance(payload.get("data"), list)
                    and payload["data"]
                    and "pointNumber" in payload["data"][0]
                ):
                    return payload["data"]
                pos = end
    return None


def load_reg_points_from_db(root: Path):
    secrets_path = root / "bin" / "Debug" / "net8.0" / "appsettings.Secrets.json"
    if not secrets_path.exists():
        secrets_path = root / "appsettings.Secrets.json"
    if not secrets_path.exists():
        return None

    with open(secrets_path, encoding="utf-8-sig") as f:
        secrets = json.load(f)
    conn_str = secrets.get("ConnectionStrings", {}).get("PostgreSQL")
    if not conn_str:
        return None

    try:
        import psycopg2
    except ImportError:
        return None

    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    cur.execute(
        """
        SELECT point_number, point_title, point_content, page_reference,
               is_introduction_point, is_annex_point
        FROM regulation_points
        WHERE regulation_document_id = %s AND status = 1
        ORDER BY point_number
        """,
        (REG_DOC_ID,),
    )
    rows = cur.fetchall()
    cur.close()
    conn.close()

    return [
        {
            "pointNumber": r[0],
            "pointTitle": r[1],
            "pointContent": r[2],
            "pageReference": r[3],
            "isIntroductionPoint": r[4],
            "isAnnexPoint": r[5],
        }
        for r in rows
    ]


def load_reg_points_from_cache():
    if not REG_POINTS_CACHE.exists():
        return None
    with open(REG_POINTS_CACHE, encoding="utf-8") as f:
        data = json.load(f)
    return data if isinstance(data, list) and data else None


def load_reg_points_from_api():
    import urllib.request

    url = f"http://localhost:5100/nd/regulation-documents/{REG_DOC_ID}/points"
    try:
        with urllib.request.urlopen(url, timeout=5) as resp:
            payload = json.load(resp)
        data = payload.get("data", [])
        return data if data else None
    except Exception:
        return None


def load_excel_non_int_rows():
    wb = openpyxl.load_workbook(EXCEL_PATH, read_only=True, data_only=True)
    ws = wb["Gap Analysis"]
    rows = []
    for i, row in enumerate(ws.iter_rows(min_row=11, values_only=True), start=11):
        clause = row[1]
        if clause is None or str(clause).strip() == "":
            continue
        clause_str = str(clause).strip()
        if clause_str.upper().startswith("INT"):
            continue
        rows.append(
            {
                "row": i,
                "clause_no": clause_str,
                "interpretation": clean_interpretation(row[3]),
                "design": norm_status(row[5]),
                "operating": norm_status(row[6]),
                "overall": norm_status(row[7]),
                "document_reference": (row[8] or "").strip() if row[8] else "",
                "policy_extract_raw": row[9],
                "interpretation_raw": row[3],
                "compliance_status": norm_status(row[13]),
                "conclusion": norm_status(row[14]),
                "observation": (row[15] or "").strip() if row[15] else "",
                "action_plans": (row[16] or "").strip() if row[16] else "",
            }
        )
    wb.close()
    return rows


def extract_bullets_dash(content: str) -> list[str]:
    bullets = []
    for line in content.split("\n"):
        s = line.strip()
        if s.startswith("- "):
            bullets.append(s[2:].strip())
    return bullets


def extract_6_2_3_bullets(content: str) -> list[str]:
    parts = re.split(r"\s*[\*\-]\s+(?=When |In the case )", content)
    return [p.strip() for p in parts if p.strip().startswith(("When ", "In the case "))]


def extract_parenthetical_parts(content: str) -> dict[str, str]:
    markers = list(re.finditer(r"\(([a-z])\)\s*", content))
    parts: dict[str, str] = {}
    for i, match in enumerate(markers):
        label = match.group(1).lower()
        start = match.end()
        end = markers[i + 1].start() if i + 1 < len(markers) else len(content)
        parts[label] = content[start:end].strip()
    return parts


def subclause_suffix_index(suffix: str) -> int:
    return ord(suffix.lower()) - ord("a")


def fuzzy_find_point(excel_summary: str, reg_points: list) -> dict | None:
    if not excel_summary:
        return None
    summary_words = set(re.findall(r"\w{4,}", excel_summary.lower()))
    if not summary_words:
        return None
    best = None
    best_score = 0.0
    for point in reg_points:
        content = (point.get("pointContent") or "").lower()
        overlap = len(summary_words & set(re.findall(r"\w{4,}", content))) / len(summary_words)
        if overlap > best_score:
            best_score = overlap
            best = point
    return best if best_score >= 0.25 else None


def resolve_reg_point(clause_no: str, reg_index: dict, reg_points: list) -> tuple[dict | None, str]:
    key = normalize_key(clause_no)
    direct = reg_index.get(key)
    if direct:
        return direct, "exact"

    sub_match = re.match(r"^(.+)-([a-z])$", key, re.I)
    if sub_match:
        parent_key = normalize_key(sub_match.group(1))
        suffix = sub_match.group(2).lower()
        parent = reg_index.get(parent_key)
        if not parent:
            return None, "missing"
        content = parent.get("pointContent") or ""
        idx = subclause_suffix_index(suffix)

        if parent_key == "3.4":
            bullets = extract_bullets_dash(content)
        elif parent_key == "6.2.3":
            bullets = extract_6_2_3_bullets(content)
        elif parent_key == "6.3.4":
            parts = extract_parenthetical_parts(content)
            sub_content = parts.get(suffix, "")
            if sub_content:
                return {
                    **parent,
                    "pointNumber": clause_no,
                    "pointContent": f"({suffix}) {sub_content}",
                }, "subclause"
            return None, "missing"
        else:
            bullets = extract_bullets_dash(content)

        if idx < len(bullets):
            return {
                **parent,
                "pointNumber": clause_no,
                "pointContent": bullets[idx],
            }, "subclause"
        return None, "missing"

    if key == "5":
        alias = CLAUSE_REG_ALIASES.get("5")
        alias_point = reg_index.get(normalize_key(alias)) if alias else None
        if alias_point:
            return {
                **alias_point,
                "pointNumber": "5",
                "pointTitle": "Mitigation of ML/FT Risks",
            }, "alias"

    return None, "missing"


def build_reg_index(points):
    by_key = {}
    for p in points:
        num = normalize_key(p.get("pointNumber") or "")
        if num:
            by_key[num] = p
        title = normalize_key(p.get("pointTitle") or "")
        if title and title not in by_key:
            by_key[title] = p
    return by_key


def build_judgment_row(excel_row, reg_point):
    design = excel_row["design"] or excel_row["compliance_status"] or excel_row["conclusion"]
    operating = excel_row["operating"] or excel_row["compliance_status"] or excel_row["conclusion"]
    overall = excel_row["overall"] or excel_row["compliance_status"] or excel_row["conclusion"] or design
    policy = extract_policy_from_cell(
        str(excel_row.get("interpretation_raw") or ""),
        excel_row["policy_extract_raw"],
    )
    confidence = CONF_BY_STATUS.get(overall, 0.72)

    clause_no = excel_row["clause_no"]
    clause_title = (reg_point.get("pointTitle") or "").strip() if reg_point else clause_no

    gap_desc = excel_row["observation"]
    if gap_desc.lower() == "open":
        gap_desc = ""

    return {
        "clause_no": clause_no,
        "clause_title": clause_title,
        "clause_content": (reg_point.get("pointContent") or "").strip() if reg_point else "",
        "design_status": design,
        "operating_status": operating,
        "overall_status": overall,
        "confidence": confidence,
        "interpretation": excel_row["interpretation"],
        "policy_extract": policy,
        "document_reference": excel_row["document_reference"],
        "gap_description": gap_desc,
        "suggested_action": excel_row["action_plans"],
        "gap_direction": "missing_in_internal" if gap_desc or excel_row["action_plans"] else "",
    }


def main():
    root = Path(__file__).resolve().parents[1]
    out_path = root / "SeedData" / "cbuae-aml-demo-judgments-94.json"
    report_path = root / "SeedData" / "cbuae-demo-94-merge-report.json"

    reg_points = load_reg_points_from_cache()
    source = "cache"
    if not reg_points:
        reg_points = load_reg_points_from_api()
        source = "api"
    if not reg_points:
        reg_points = load_reg_points_from_db(root)
        source = "db"
    if not reg_points:
        reg_points = load_reg_points_from_transcript()
        source = "transcript"

    if not reg_points:
        print("ERROR: Could not load regulation points (API, DB, or transcript).")
        sys.exit(1)

    reg_index = build_reg_index(reg_points)
    excel_rows = load_excel_non_int_rows()

    output = []
    matched = []
    not_in_reg = []

    for ex in excel_rows:
        reg, match_kind = resolve_reg_point(ex["clause_no"], reg_index, reg_points)
        if not reg:
            not_in_reg.append({"clause_no": ex["clause_no"], "excel_row": ex["row"]})
        matched.append(
            {
                "clause_no": ex["clause_no"],
                "excel_row": ex["row"],
                "reg_matched": reg is not None,
                "match_kind": match_kind if reg else None,
                "reg_title": (reg.get("pointTitle") or "") if reg else None,
            }
        )
        output.append(build_judgment_row(ex, reg))

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)
        f.write("\n")

    report = {
        "summary": {
            "regulation_points_source": source,
            "regulation_points_count": len(reg_points),
            "excel_non_int_rows": len(excel_rows),
            "output_count": len(output),
            "matched_to_regulation": sum(1 for m in matched if m["reg_matched"]),
            "not_matched_to_regulation": len(not_in_reg),
        },
        "not_matched_to_regulation": not_in_reg,
        "rows": matched,
    }
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Regulation points source: {source} ({len(reg_points)} points)")
    print(f"Excel non-INT rows: {len(excel_rows)}")
    print(f"Matched to regulation: {report['summary']['matched_to_regulation']}")
    print(f"Not matched: {len(not_in_reg)}")
    if not_in_reg:
        print("Unmatched clause numbers:", [x["clause_no"] for x in not_in_reg[:20]])
    print(f"Output: {out_path}")
    print(f"Report: {report_path}")


if __name__ == "__main__":
    main()
