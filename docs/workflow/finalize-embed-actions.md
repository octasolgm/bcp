# Finalize: embedding resolved actions into internal documents

Applies to real accounts only. Demo accounts do not use this pipeline.

## Important: "embed" here does NOT mean vector embeddings

In this feature "embed" means **writing text into the internal document**. No embedding model and no
vector search is involved in finalize. The vector embeddings in the codebase (Azure OpenAI embeddings,
`RegulEmbeddingRetrievalService`, `PassageEmbeddingService`) belong to the analysis/retrieval step that
finds evidence before the gap is judged. Finalize only reads the results of that step (which document
and page the evidence came from).

## Short answer: does it copy the action words?

No. At finalize, the action text the checker typed is given to an LLM as context only. The LLM writes
new policy language (definitions, obligations, controls) and that is what lands in the document. If the
LLM fails or returns nothing, a fallback is used (see "Fallback" below); only then can action wording
appear, and only the quoted draft wording, not the checklist.

## End to end flow

1. Analysis produces gaps and action plans per clause. The checker resolves action plans.
2. Reviewer finalizes the run (`ReviewerController`, finalize endpoint). Re-finalize is also supported,
   and `regenerate-corrected-documents` re-runs only this step.
3. `NdCorrectedDocumentService.GenerateForRunAsync` runs for every internal document attached to the run:
   - Calls `NdActionPlanEmbedResolver.ResolveForRunAsync` to find, for each RESOLVED action plan, which
     internal document and which page it belongs to.
   - For each target calls `NdFinalizeEmbedContentService.GenerateEmbedBodyAsync` (the LLM step).
   - Downloads the source file, writes the notes in, uploads the result as a new file.
   - Creates a new `StoredDocument` row: same title, `VersionNumber` = highest sibling + 1 (v1 -> v2),
     status `review-due`, file name `<name> (v2).pdf`.
   - Writes a `corrected_copy_generated` entry in the new document's history with, per embed: clause,
     gap number, gap text, original action, page, the embedded text, who resolved it and when.
4. The original version is never modified. Re-finalizing always adds another version.

The unit is the action plan, not the gap: a gap with two actions where only one is resolved still gets
that one embedded.

## Where each note goes (resolver)

Document, best evidence first:
1. Documents named in the judgment's `document_reference` (page taken from the `p.N` in the label).
2. Documents whose parsed text contains one of the judgment's verbatim policy quotes.
3. The clause's best retrieval match (`fusedMatches` / `matches` / `bm25Matches` in the stored
   retrieval JSON: `sourceDocumentId`, `sourcePage`).
4. If the run has exactly one internal document, that document.
5. Otherwise the action is not embedded and a warning is logged. It is never dropped into a document
   with no evidence.

If the action text names a document ("Amend the AML Manual ..."), only that document is used.
Page: if the action names a section ("Section 7.7", "Definitions section"), the page of that heading in
the parsed text is used; otherwise the evidence page. The gap text for the prompt is that action's own
numbered gap only, not the whole clause's list.

## LLM used

- Setting key: `finalize_embed_llm` (Admin system settings, `SystemSettingsController`), stored as
  `{ provider, model }`. Class chain: `FinalizeEmbedLlmSettingsService` -> `FinalizeEmbedLlmService`.
- If the setting is not saved, it falls back to the **Regul workflow LLM** setting
  (`regul_workflow_llm`); if that is also unset, the catalog default is **Google `gemini-3.5-flash`**.
- Supported providers: google, openai, anthropic, xai, moonshot, deepseek, zhipu, qwen, openrouter.
  Per project policy, run it through the `openrouter` provider.
- The setting is cached for 2 minutes. Saving requires the provider API key to be configured on the server.
- Call parameters are fixed in the provider clients (not configurable per feature):
  - Gemini: temperature 0.1, maxOutputTokens 8192
  - OpenAI-compatible (OpenAI, xAI): temperature 0.1, max_tokens 8192 (reasoning models such as GPT-5/o-series
    omit temperature)
  - Anthropic: max_tokens 8192, no temperature sent
  - OpenRouter / DeepSeek / Zhipu / Qwen / Moonshot (shared chat-completions base): max_tokens 16384
    (Moonshot/OpenRouter Kimi variants use reasoning effort "high" and their own cap)
- Single user message, no separate system role: the instruction block and the data are sent as one prompt.
- One LLM call per resolved action (clause + gap + action) per finalize. When the same action lands in
  several internal documents, the text is generated once and the identical text is written into every
  one of them. A new finalize (or regenerate) generates fresh text.
- Usage page: these calls show as activity "Finalize - policy text", with a note naming the clause and gap.

## Prompt (verbatim, `NdFinalizeEmbedContentService`)

```
You are a senior compliance policy writer for a regulated financial institution.
The bank closed a regulatory gap found during an AML/compliance analysis. Write ONLY the policy
language that should be inserted into the internal document so a future automated review will
treat the requirement as satisfied.

Rules:
- Write as finished policy manual text (definitions, obligations, controls) - not a project plan.
- Do NOT include operational instructions such as re-run the assessment, present for approval,
  feed revised ratings, update methodology to reflect numbered gap bullets, or similar workflow steps.
- Do NOT paste the gap-analysis checklist or numbered audit findings verbatim.
- Do NOT mention "gap", "action plan", "resolved", or analysis metadata.
- Use professional third-person policy tone; complete sentences; 1-4 short paragraphs or a tight
  definition list as appropriate.
- Output plain text only (no markdown fences, no JSON).

---
Regulatory clause: {ClauseNo} - {ClauseTitle}

Write policy text for THIS ONE gap only. The clause may have other gaps; each of those gets its own
separate text, so do not cover, mention or repeat anything outside this gap.

What was missing (for your understanding only - do not repeat as a checklist):
{this action's own gap text}

What the bank implemented (context only - distill into policy prose, do not copy verbatim):
{action plan text}

Existing policy excerpts from this document (match tone where helpful):      <- only if present, max 5
- {policy extract}

Write the policy text to embed:
```

Inputs per call: clause number/title, this action's gap text, the action plan text, and up to 5
verbatim policy extracts the original judgment quoted from the same document (for tone). Output cleanup:
trimmed, and a leading/trailing code fence is stripped.

## What gets written into the document

Header and footer wrap the LLM text (`NdActionPlanEmbedNote`):

```
POLICY UPDATE - Regulatory Clause {ClauseNo} - {ClauseTitle}

{LLM policy text}

Note: This update addresses Regulatory Clause {ClauseNo}, referenced at p.{page} of this document.
```

- **PDF** (`NdCorrectedPdfEmbedder`, PdfSharpCore): new A4 page(s) inserted immediately after the cited
  page; several notes for the same page stack together. A PDF library cannot splice into the middle of an
  existing page, so this is a new page, not an inline paragraph. Unknown page -> appended after the last
  page. If the PDF cannot be parsed, pages are rasterised at 150 DPI and rebuilt (the note still lands
  at the right place, but the original text is no longer selectable in that copy).
- **DOCX/DOC** (`NdCorrectedDocxEmbedder`, OpenXML): a real paragraph (green left border, light green
  fill, bold first line) inserted right after the paragraph matching the anchor (first 60 chars of the gap
  text, else the clause number). No match -> appended at the end of the document.
- Other file types, missing storage, or any failure: the copy is still created and still shows v2, but
  it points at the same bytes as the source (no embedded text). Finalize never fails because of this; a
  warning is logged.

## Fallback when the LLM fails

`BuildLegacyPolicyFallback`: if the action text looks like `Amend <section> to include: "<wording>"`,
the quoted wording is used as the policy text; otherwise the action text as one line. This is the only
case where action words appear in the document, and it is logged as a warning.

## How to verify it works (checklist)

1. Finalize a real-account run that has at least one RESOLVED action plan and an attached PDF/DOCX internal document.
2. Response `embed` block should show `resolvedActionPlans`, `embedTargets` > 0 and the document list.
   `embedTargets` = 0 means the resolver found no evidence document for those actions.
3. Open the new version (v2): find the "POLICY UPDATE - Regulatory Clause ..." block. The text should read
   as policy language, not as the checker's action sentence.
4. Document history `corrected_copy_generated` entry lists each embed with `gap`, `action` and
   `embeddedText` side by side - compare to confirm they differ.
5. Logs: "Finalize embed LLM using {Provider}/{Model}" confirms which model ran; a warning
   "Finalize embed LLM failed ... using fallback policy text" means the fallback was used.

## Known limits

- PDF notes are added pages, not inline edits.
- The new version has `ParseStatus = pending`; it must be parsed before it can be used as evidence in a
  later analysis.
- A pass through the LLM can still paraphrase closely when the action is already policy-shaped; the rules
  ask for distillation, not guaranteed rewording.
- Tests: `NdFinalizeEmbedPromptTests`, `NdActionPlanEmbedResolverTests`.
