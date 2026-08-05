# Page & section references — before vs now

Simple guide: what changed and what stayed the same.

---

## Quick summary

| | **Before** | **Now** |
|---|------------|---------|
| **Section / point number** (`6.2`, `§8.4`) | Landing AI extract only | Landing extract **+ markdown recovery** if missed |
| **Section / point text** | Landing AI extract | Landing extract **+ markdown recovery** for missed headings |
| **PDF page** (`p. 14`) | Landing hints + sparse parse markers | **PdfPig + grounded markdown** (real viewer pages) |
| **Analysis citations** | Stale library pages / wrong refs | **Auto library prep** + grounded markdown before run |

---

## Section ref

**What it is:** The numbered label on a clause (`6.2`, `9.4.1`, `§8.4`).

| Doc type | DB field | Primary source |
|----------|----------|----------------|
| Internal policy | `sectionRef` | Landing extract → `clause_no` |
| Regulation | `pointNumber` | Landing extract → `point_id` |

**Now also:** If Landing extract misses a heading that exists in parse markdown (e.g. `9.4.1`), we scan markdown and **backfill** the section/point automatically — at extract, cache import, and **before every analysis run**.

---

## Page ref — what changed

**What it is:** Which **PDF viewer page** the section text appears on.

| Doc type | DB field | UI format |
|----------|----------|-----------|
| Internal policy | `sourcePage` | `p. 14` |
| Regulation | `pageReference` | `8.4 · p. 93` |

### Before
- Landing `source_page` / sparse markers → often wrong (printed page vs viewer page).
- Analysis read whatever was in the library.

### Now
- **PdfPig** + **grounded Landing markdown** → real viewer pages.
- **Before analysis:** recover missing sections, refresh pages, sync library → run.
- **During analysis:** grounded markdown for `document_reference`; regulation `pdfPage` via PDF resolver.

---

## When things run

| When | Internal | Regulation | Analysis |
|------|----------|------------|----------|
| After extract | Page resolve + section recovery | Page resolve + point recovery | — |
| Old docs | Repair page refs | Refresh / repair pages | — |
| **Start of analysis** | Recover + refresh + sync to run | Recover + refresh | Grounded citations |

Parse/extract/analysis **LLM prompts** are unchanged. Library prep is automatic.

---

## Example (AML manual)

| Section | Before | Now |
|---------|--------|-----|
| `6.2` | ~p. 52 (wrong) | **p. 14** |
| `9.4.1` Internal Audit | often **missing** | **recovered** + **p. 49** |
| Reg `§8.4` | ~p. 96 | **p. 93** |

---

## What you need to do

1. **New uploads** — sections, pages, and recovery run automatically.
2. **Old library docs** — run **Repair page refs** once; re-run analysis to pick up fixes.
3. No separate manual fix for missed `9.4.1`-style sections if the heading is in parse markdown.

---

## Related docs

- [`PAGE-REFERENCES.md`](./PAGE-REFERENCES.md)
- [`REGUL-FORWARD-MATCHING-FIX-PLAN.md`](./REGUL-FORWARD-MATCHING-FIX-PLAN.md)
