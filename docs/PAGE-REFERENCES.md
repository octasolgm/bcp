# Page references — architecture (internal + regulation)

**Goal:** Correct **PDF viewer page numbers** everywhere. **Do not change** Landing AI parse, extract, or analysis logic.

---

## Separation of concerns

| Data | Source | Used for |
|------|--------|----------|
| **Section / point number** (`6.2`, `8.4`, `9.4.1`) | Landing AI **extract** (`clause_no` / `point_id`) | Library sections, analysis chunks, gap labels |
| **Section / point text** | Landing AI **extract** | Judgment, reverse mapping |
| **PDF viewer page** (`p. 14`) | **PdfPig + grounded markdown** (not Landing) | Library UI, PDF links, citations |

Landing AI **parse** markdown is still used for extract and analysis context.  
Landing `source_page` / sparse `<!-- BCP_PDF_PAGE:N -->` markers are **never trusted** for page assignment.

---

## Why Landing page refs are wrong

| Regul.ai | BCP (Landing-only) |
|----------|-------------------|
| PyMuPDF: **one marker per PDF page** | Landing: **one marker per chunk** (~20–99 pages) |
| Page ≈ PDF viewer | TOC/footer printed pages matched (e.g. p. 52 vs p. 14) |

---

## Recommended approach (implemented)

Your suggestion — **Landing text + real PDF page markers** — is the right model:

```
┌─────────────────┐     ┌──────────────────┐
│ Landing parse   │     │ PdfPig per-page  │
│ (rich text)     │     │ (viewer pages)   │
└────────┬────────┘     └────────┬─────────┘
         │                       │
         └───────────┬───────────┘
                     ▼
         PdfGroundedMarkdownBuilder
         (align page text → inject dense BCP_PDF_PAGE:N)
                     │
                     ▼
         NdDocumentPageReferenceResolver
         1. Match section body in PdfPig text (fast)
         2. Else match in grounded Landing markdown
         3. Never use Landing AI page hints
                     │
                     ▼
         sourcePage / pageReference in DB
```

### Code

| Component | Role |
|-----------|------|
| `PdfNativePageDocument` | PdfPig text per viewer page 1…N |
| `PdfGroundedMarkdownBuilder` | Landing markdown + dense real-page markers (Regul.ai-style) |
| `NdDocumentPageReferenceResolver` | Single resolver for internal + regulation |
| `NdInternalDocumentSectionPageService` | Auto on extract + manual repair (async) |
| `NdRegulationUploadService` | Auto on regulation extract |
| `NdRegulationPointRepairService` | Manual regulation repair |

---

## When pages are assigned

| Event | Internal doc | Regulation doc |
|-------|--------------|----------------|
| **After section/point extract** | Automatic (`RefreshSectionPagesAsync`) | Automatic (during `ExtractInternalAsync`) |
| **Repair page refs** | Manual button (background job + poll) | Manual refresh / repair |
| **Analysis run** | Reads library `sourcePage` — no re-resolve | Reads library `pageReference` |

Parse and extract workflows are unchanged; only the **page assignment step** after extract uses PDF-grounded resolution.

---

## Section refs

**Section numbers stay from Landing extract** — we do not re-number sections from PdfPig.

If a section number is wrong (e.g. extract missed `9.4.1`), fix via:
- Re-extract sections/points, or
- Regulation **Repair points** (dedup/junk), not page repair.

Page repair only fixes **which PDF page** a known section/point appears on.

---

## Operational checklist

1. **Parse** document (Landing AI) — unchanged  
2. **Extract** sections/points (Landing AI) — unchanged  
3. Pages assigned automatically (PdfPig + grounded markdown)  
4. If old docs have wrong pages → **Repair page refs** once  
5. Re-run analysis only if you need updated judgments (library pages are copied into run)

---

## Limitations

- **Scanned PDFs:** PdfPig may miss text; OCR (Regul.ai uses Tesseract) may be needed later.  
- **Word uploads:** Grounding uses converted PDF in storage when available.  
- **Section retrieval in analysis** (e.g. §8.4 missing 9.4.1) is a separate retrieval/prompt issue — see `REGUL-FORWARD-MATCHING-FIX-PLAN.md`.
