# Internal Documents — Parse & Multi-Doc Analysis

Short guide for internal policy PDFs: upload, parse, and how they are sent to Landing AI and Gemini during analysis.

---

## 1. Document lifecycle

| Step | What happens | DB |
|------|----------------|-----|
| **Upload** | PDF saved to storage only — **no parse** | `parse_status = pending` |
| **Run parse** (manual, table button) | Landing AI `/parse` → markdown | `landing_ai_parse_cache` + `parse_status = parsed` |
| **Analysis run** | If not parsed yet → auto-parse then compare | Same cache + status updated |

Gov docs use **extract points** at upload. Internal docs use **parse to markdown** only (no point extraction).

---

## 2. Internal documents table (ND UI)

Path: `/nd/internal-documents`

| Column / action | Meaning |
|-----------------|--------|
| **Pending parse** | Uploaded, markdown not generated yet |
| **Run parse** | Maker/super_admin — calls `POST /nd/internal-documents/{id}/parse` |
| **Parsed** | Markdown in `landing_ai_parse_cache` (key = `file_hash`) |
| **Failed** | Parse error stored in `parse_error` |

---

## 3. Multiple internal docs on one analysis

User can select **multiple** internal PDFs on analyse-v8 / run form.

- All IDs stored in `analysis_runs.selected_internal_doc_ids` (JSON array)
- **One gov point** is compared against **all selected internal docs together**
- Each internal doc: load PDF → ensure parsed (DB markdown or parse now) → merge into prompts

---

## 4. What is sent — per gov point

### Phase 1 — Landing AI

**Step A — Parse** (once per internal PDF, cached)

```
Each internal PDF → /v1/ade/parse → markdown → landing_ai_parse_cache
                  → stored_documents.parse_status = parsed
```

**Step B — Compare** (once per gov point)

Landing AI `/v1/ade/extract` accepts **markdown only** (no PDF in this call).

```
One markdown prompt containing:
  • Auditor rules (CBUAE/TFS)
  • ALL internal markdowns (labeled Document 1, Document 2, …)
  • ONE gov point (number + title + text)
```

**Can Landing AI get PDF + markdown together?**  
No — `/extract` only takes markdown. PDF is parsed first; full text goes into the prompt.

---

### Phase 2 — Gemini (dual verify)

After Phase 1 success (auto on first run):

```
Text prompt:
  • Pass 2 verifier instructions
  • Landing AI Pass 1 message
  • ONE gov point
  • ALL internal markdowns (parsed text supplement)

Attachments:
  • ALL internal PDFs (binary, one inline_data per file)
```

Gemini gets **both markdown and PDFs** for better accuracy. Landing AI compare does not.

---

## 5. Flow (one point, N internal docs)

```
For each gov point:
  │
  ├─► Load & parse all selected internal PDFs (cache/DB)
  │
  ├─► PHASE 1 Landing AI
  │     prompt = rules + all internal MD + 1 gov point
  │     fail → dual_verify skipped
  │
  └─► PHASE 2 Gemini
        prompt = Pass1 + gov point + all internal MD
        attachments = all internal PDFs
```

---

## 6. Rerun rules (unchanged)

| Action | Landing AI | Gemini |
|--------|------------|--------|
| Rerun point | ✅ all internals | ✅ all internals |
| Rerun dual verify only | ❌ | ✅ all internals |
| Resume run | parse if needed | Phase 2 only if Landing OK |

---

## 7. API

| Method | Path | Role |
|--------|------|------|
| GET | `/nd/internal-documents` | List + `parseStatus` |
| POST | `/nd/internal-documents/upload` | Upload (`parseStatus: pending`) |
| POST | `/nd/internal-documents/{id}/parse` | Manual parse |

---

## 8. Code references

| Piece | File |
|-------|------|
| Parse service | `bcp-api/Services/NewDashboard/NdInternalParseService.cs` |
| Multi-doc processor | `bcp-api/Services/NewDashboard/NdAnalysisProcessor.cs` |
| Phase 1 prompt | `bcp-api/Services/LandingAi/LandingAiComparePromptBuilder.cs` |
| Phase 2 prompt | `bcp-api/Services/GovPointsService.cs` (`DualVerifyPromptBuilder`) |
| Multi-PDF Gemini | `bcp-api/Services/GeminiService.cs` |
| UI parse button | `bcp-web/.../nd-internal-documents/` |

---

## 9. Gov vs internal markdown (reminder)

| Document | Markdown when | Used in compare |
|----------|---------------|-----------------|
| **Gov** | Regulation upload/extract | Only **one point text** (from snapshot) |
| **Internal** | Parse (manual or at run) | **Full markdown** of each selected PDF |
