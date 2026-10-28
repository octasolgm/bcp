"""Apply Excel gap analysis to cbuae-aml-demo-judgments.json (seed only, not DB)."""
from pathlib import Path

from compare_demo_excel import apply


def main():
    root = Path(__file__).resolve().parents[1]
    seed_path = root / "SeedData" / "cbuae-aml-demo-judgments.json"
    excel_path = Path(r"c:\Users\Pc\Downloads\Tester-4_gap_analysis 0508.xlsx")

    matched, not_in_excel = apply(seed_path, excel_path)
    changed = sum(1 for m in matched if m["will_change"])
    print(f"Updated seed file: {seed_path}")
    print(f"Points changed: {changed}")
    print(f"Points unchanged (not in Excel): {len(not_in_excel)}")


if __name__ == "__main__":
    main()
