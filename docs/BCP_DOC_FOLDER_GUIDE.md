# BCP document folder guide

Source folder: `C:\Users\Hp\Downloads\bcp doc`

Simple list of **what each file is** and **where it goes in Reguliq (BCP ND)**.

---

## Quick categories

| Category | Meaning in BCP | ND portal |
|----------|----------------|-----------|
| **Gov / regulation** | Law, CBUAE, TFS rules — extract **requirement points** | **Regulation Docs Library** → Upload → **Run extraction** |
| **Internal** | Your bank/policy PDFs — parse to **markdown** for compare | **Document Library** → Upload → **Run parse** |
| **Other** | Excel, requirements, templates — **not** the main analysis upload | Keep as reference / manual work |

---

## step1 folder

### Gov / regulation (upload → Regulation Docs Library)

| File | Notes |
|------|--------|
| `TFS Guidelines.pdf` | Main TFS regulation — **used in app demo** (gov side) |
| `TFS Guidelines EOCN.pdf` | TFS guidelines (EOCN version) |
| `TFS Cabinet Resolution.pdf` | Cabinet resolution / legal basis |
| `TFS Typologies.pdf` | TFS typologies report |
| `CBUAE_EN_3945_VER2 (2).pdf` | CBUAE regulation / circular |
| `CBUAE_EN_699_VER1 (1).pdf` | CBUAE regulation / circular |

### Internal (upload → Document Library)

| File | Notes |
|------|--------|
| `I M P T F S.pdf` | Internal compliance policy — **used in app demo** (internal side) |

### Other / check before use

| File | Notes |
|------|--------|
| `A N C TI O N E.pdf.pdf` | Name unclear — open and confirm (not mapped in app by default) |
| `x.pdf` | **Same size as `I M P T F S.pdf`** — likely a duplicate copy |
| `Book 6.xlsx` | Spreadsheet — not a regulation PDF for extract |
| `sample output excel sheet (1).xlsx` | Sample output template |
| `Requirements - Open to Discussion.docx` | Project / business requirements — not for AI compare |

---

## tep2 folder

*(Folder name is `tep2`, not `step2`.)*

### Gov / law (upload → Regulation Docs Library)

| File | Notes |
|------|--------|
| `AMLCFT LAW.pdf` | UAE AML/CFT **law** |
| `amlcft cb uae decision.pdf` | CBUAE **decision** on AML/CFT |

### Internal (upload → Document Library)

| File | Notes |
|------|--------|
| `Internal A M L M a n u a l 290626.pdf` | Internal AML manual (PDF) |
| `internal -Implementation of AML CFTPF Manual.docx` | Internal implementation manual (Word — convert to PDF for ND upload if needed) |

### Other

| File | Notes |
|------|--------|
| `manul gap analysis document.xlsx` | Manual gap analysis workbook — reference, not analysis input |

---

## Typical analysis pair (what the app expects)

```
Gov:      TFS Guidelines.pdf          (regulation points)
    ×
Internal: I M P T F S.pdf             (your policy)
    →
Run analysis on /nd/analyse-v8
```

You can also run:

- **Multiple gov docs** (select points from several regulations)
- **Multiple internal docs** (one gov point compared against all selected internal PDFs)

---

## Upload checklist

### Regulation (gov)

1. `/nd/regulation-documents` → **+ Upload regulation**
2. **Run extraction** (get numbered points)
3. Use points in **New analysis**

### Internal

1. `/nd/internal-documents` → **+ Upload**
2. Status = **Pending parse** → click **Run parse**
3. Select in **New analysis** (can pick more than one)

### Do not upload to analysis as-is

- `.xlsx` — Excel templates / gap worksheets
- `.docx` — requirements or manuals unless converted to PDF for internal library

---

## Summary counts

| Category | Files |
|----------|------:|
| Gov / regulation / law | 8 |
| Internal policy | 3 (+ 1 docx) |
| Other / duplicate / unclear | 5 |

---

## File tree (reference)

```
bcp doc/
├── step1/
│   ├── TFS Guidelines.pdf              → Gov
│   ├── TFS Guidelines EOCN.pdf         → Gov
│   ├── TFS Cabinet Resolution.pdf    → Gov
│   ├── TFS Typologies.pdf              → Gov
│   ├── CBUAE_EN_3945_VER2 (2).pdf      → Gov
│   ├── CBUAE_EN_699_VER1 (1).pdf       → Gov
│   ├── I M P T F S.pdf                 → Internal
│   ├── A N C TI O N E.pdf.pdf          → Other (verify)
│   ├── x.pdf                           → Other (duplicate?)
│   ├── Book 6.xlsx                     → Other
│   ├── sample output excel sheet (1).xlsx → Other
│   └── Requirements - Open to Discussion.docx → Other
└── tep2/
    ├── AMLCFT LAW.pdf                  → Gov / law
    ├── amlcft cb uae decision.pdf      → Gov / law
    ├── Internal A M L M a n u a l 290626.pdf → Internal
    ├── internal -Implementation of AML CFTPF Manual.docx → Internal (convert to PDF)
    └── manul gap analysis document.xlsx → Other
```
