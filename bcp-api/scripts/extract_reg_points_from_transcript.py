"""Extract regulation points JSON from agent transcript (one-time helper)."""
import json
from json import JSONDecoder
from pathlib import Path

TRANSCRIPT = Path(
    r"C:\Users\Pc\.cursor\projects\c-Users-Pc-Documents-GitHub-bcp-new\agent-transcripts"
    r"\e3715424-de7b-478c-8fcc-29916a9f9cbe\e3715424-de7b-478c-8fcc-29916a9f9cbe.jsonl"
)
OUT = Path(__file__).resolve().parents[1] / "SeedData" / "regulation-points-extract.json"


def extract_payload(text: str):
    decoder = JSONDecoder()
    pos = 0
    while True:
        start = text.find("{", pos)
        if start < 0:
            return None
        try:
            obj, end = decoder.raw_decode(text, start)
        except json.JSONDecodeError:
            pos = start + 1
            continue
        if isinstance(obj, dict) and obj.get("success") and isinstance(obj.get("data"), list):
            if obj["data"] and "pointNumber" in obj["data"][0]:
                return obj["data"]
        pos = end


for line in open(TRANSCRIPT, encoding="utf-8"):
    if "pointNumber" not in line:
        continue
    obj = json.loads(line)
    text = obj["message"]["content"][0]["text"]
    data = extract_payload(text)
    if not data:
        continue
    OUT.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"saved {len(data)} points to {OUT}")
    break
else:
    print("ERROR: no regulation points payload found in transcript")
