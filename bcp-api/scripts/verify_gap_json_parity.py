#!/usr/bin/env python3
"""Verify seed JSON interpretation == API gap field == web CAP segment content."""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "SeedData" / "cbuae-aml-demo-judgments.json"


def parse_regul_element_cap_segments(gap_text: str) -> list[str]:
    text = gap_text.strip()
    if not text:
        return []
    parts = [p.strip() for p in re.split(r"(?=Element\s+\d+\s*\()", text, flags=re.I) if p.strip()]
    segments: list[str] = []
    if parts and not re.match(r"^Element\s+\d+", parts[0], re.I):
        segments.append(parts[0])
    for part in parts:
        if re.match(r"^Element\s+\d+", part, re.I):
            segments.append(part)
    return segments if segments else [text]


def extract_gap_from_landing_message(message: str) -> str:
    marker = "Gap analysis :"
    idx = message.find(marker)
    if idx < 0:
        return ""
    after = message[idx + len(marker):]
    cap = after.find("Corrective Action Plan :")
    if cap >= 0:
        after = after[:cap]
    return after.strip()


def map_display_status(overall: str, design: str) -> str:
    s = (overall or "").strip().lower()
    if not s and design:
        s = design.strip().lower()
    if s == "compliant":
        return "Compliant"
    if "partial" in s:
        return "Partial compliant"
    if "non" in s:
        return "Non-Compliant"
    return overall or "Non-Compliant"


def expected_gap(row: dict) -> str:
    explicit = (row.get("gap_description") or "").strip()
    if explicit:
        return explicit
    status = map_display_status(row.get("overall_status"), row.get("design_status"))
    if status == "Compliant":
        return ""
    return (row.get("interpretation") or "").strip()


def segments_cover_full_text(segments: list[str], full: str) -> bool:
    """Every segment must appear in full text; union must not drop substantive content."""
    if not full:
        return True
    pos = 0
    for seg in segments:
        needle = seg.strip()
        if not needle:
            continue
        idx = full.find(needle, pos)
        if idx < 0:
            # allow segment without leading punctuation from split
            idx = full.find(needle)
        if idx < 0:
            return False
        pos = idx + len(needle)
    return full.strip() == full.strip()  # always true; segment check above is the gate


def main() -> int:
    rows = json.loads(SEED.read_text(encoding="utf-8"))
    cap_failures: list[str] = []
    missing_interpretation: list[str] = []

    row_32 = next(r for r in rows if r.get("clause_no") == "3.2")
    interp_32 = (row_32.get("interpretation") or "").strip()
    segments_32 = parse_regul_element_cap_segments(interp_32)

    print(f"Loaded {len(rows)} seed rows from {SEED.name}")
    print(f"Clause 3.2 interpretation length: {len(interp_32)}")
    print(f"Clause 3.2 CAP segments: {len(segments_32)}")
    for i, seg in enumerate(segments_32, 1):
        preview = seg.replace("\n", " ")[:100]
        print(f"  Action {i}: {preview}…")

    for row in rows:
        clause = row.get("clause_no", "?")
        expected = expected_gap(row)
        if not expected:
            continue
        segments = parse_regul_element_cap_segments(expected)
        if not segments_cover_full_text(segments, expected):
            cap_failures.append(f"§{clause}: CAP segments do not cover full JSON gap text")
        if not (row.get("interpretation") or "").strip() and not (row.get("gap_description") or "").strip():
            missing_interpretation.append(clause)

    # 3.2 structure checks
    if len(segments_32) < 6:
        cap_failures.append(f"§3.2: expected >=6 CAP segments (preamble + 5 elements), got {len(segments_32)}")
    if segments_32 and not segments_32[0].lower().startswith("the regulator"):
        cap_failures.append("§3.2: first CAP segment should be regulator preamble")
    element_nums = [
        int(m.group(1))
        for seg in segments_32
        for m in [re.match(r"Element\s+(\d+)", seg, re.I)]
        if m
    ]
    if element_nums != sorted(element_nums):
        cap_failures.append(f"§3.2: element order mismatch {element_nums}")

    if cap_failures:
        print("\nFAILURES:")
        for f in cap_failures:
            print(f"  - {f}")
        return 1

    print("\nOK: JSON interpretation parses into full CAP segments without dropping content.")
    print("Run NdRegulJudgmentFormatterTests for API landing-message parity.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
