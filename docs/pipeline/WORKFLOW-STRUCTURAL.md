# Workflow: structural chunking and indexing

Status: fully built and working. This is the numbering-based path - clauses are found by reading
the actual "3.4", "Article 12" style numbers in the document.

## The steps, in order

```
Upload  ->  Parse (once)  ->  Extract (structural)  ->  Index (structural)
```

### 1. Upload - once

The PDF is saved to storage. Nothing is read yet.

### 2. Parse - once, ever

The PDF is converted to plain text with page markers. This is the **only step that can cost
money** (if the Azure engine is used - free for Tesseract/RapidOCR/Docling). The result is saved
to the database (`nd_local_document_extractions.markdown_text`) and is never regenerated unless
someone deliberately re-parses. Every workflow below reads this same saved text - the PDF itself
is never touched again after this point.

### 3. Extract (structural) - regex, no AI, no API call, free every time

Reads the saved parsed text line by line. Any line shaped like `"3.4 Name Screening"` or
`"Article 12"` starts a new clause. Everything until the next matching line belongs to that
clause. Markdown formatting (bold, links) and tables are stripped first so they don't interfere
with the match or end up inside the clause text.

**Example**, using a real clause from the TFS Guidelines document:

Input (parsed text, one line shown):
```
3.4. Name Screening
In addition to the regular screening utilizing the UN Consolidated List...
```

Output (one row, saved to `sections_json` on the same extraction record):
```json
{ "clauseNo": "3.4", "clauseText": "3.4. Name Screening\nIn addition to the regular screening...", "sourcePage": 12 }
```

### 4. Index (structural) - automatic, free, local embedding model

The moment structural Extract finishes successfully **for an internal document** (never for a
regulation document - see below), this runs automatically, no click needed:

1. Every clause structural Extract just produced gets read.
2. Each clause's full text is embedded (turned into a 384-number vector) by a **local** model
   (`bge-micro-v2`, no cloud call, $0).
3. Each clause + its vector is saved as its own row in a new table,
   `nd_local_document_extraction_sections`, linked back to the parse/extract record.

**Example**, continuing clause 3.4 above:
```json
{
  "extractionId": "8f7de3e8-...",
  "sectionIndex": 3,
  "clauseNo": "3.4",
  "clauseText": "3.4. Name Screening\nIn addition to the regular screening...",
  "sourcePage": 12,
  "embedding": [0.0123, -0.0456, 0.0891, ...]   // 384 numbers
}
```

This row is now permanently searchable - a future analysis run can ask "which stored clauses are
closest in meaning to this gov clause" without re-embedding anything.

## Why indexing only runs for internal documents

Regulation (gov) clauses are the *query* side of the eventual analysis pipeline - read one at a
time and used to search. Internal document clauses are the *haystack* - they need to already be
searchable, ahead of time. You don't pre-build a search index out of the thing doing the
searching.

## What's shared with the semantic workflow, and what's different

| Step | Shared? |
|---|---|
| Upload | Same for both |
| Parse | **Exact same saved result reused by both** - this is the whole point |
| Extract | **Different method entirely** - regex here, embeddings in the semantic workflow |
| Index | Same *purpose* (make chunks searchable), different *input* (this workflow's own clauses only) |
