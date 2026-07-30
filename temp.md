# analyse-v8 vs analyse-v9 — short diff

| | **V1 — `/nd/analyse-v8`** | **V2 — `/nd/analyse-v9`** |
|---|---|---|
| Role | Production — **do not change** | Experiment clone (Regul.ai prompts) |
| Prompt version | **V2** (BCP default) | **V3** (`comparePromptVersion: 'v3'`) |

---

## Pipeline steps — **same**

Both pages use the same ND workflow:

1. Pick internal doc(s) + regulation points (or library)
2. Create ND analysis run
3. **Pass 1** — Landing AI compare → structured JSON
4. **Pass 2** — Dual-verify second LLM → confirm/correct Pass 1
5. Save run points → gap analysis / results UI

No extra Regul.ai steps (no reverse coverage, no qualitative pass, no quote-verify code pass).

---

## What actually changes — **prompts only**

### Pass 1 — Landing AI (`PromptTemplateV3`)

V3 adds Regul.ai **judgment rules** on top of the same JSON schema:

- **Document perspective** — regulator-only text (disclaimers, supervisor instructions, other-entity scope) is **not** a gap when the bank manual correctly omits it
- **Vendor/list DD** — AML “due diligence on list provider” = verify list completeness, **not** procurement/vendor onboarding
- **Element-level** — multi-part clauses checked **per element**, not one holistic guess
- **Verbatim quotes** — evidence must be character-for-character from policy text
- **Gap text** — mandatory detail when Partial / Non-Compliant
- **Confidence** — prefer lower score when evidence is weak

### Pass 2 — Dual verify (`AppendPass2RulesV3`)

Same V3 judgment ideas applied again when the second LLM re-checks Pass 1.

---

## UI / code — **mostly clone**

- Same layout, panels, Run button, status tabs, embedded gap report
- v9 only difference on create run: sends `comparePromptVersion: 'v3'`
- v8 has a bit more point-label / detail-modal polish; v9 snapshot builder is simpler — **does not change AI steps**

---

## Not ported from Regul.ai (yet)

- Claude `EXTRACTION_SYSTEM_PROMPT` (BCP still loads regulation points from library/DB)
- Reverse coverage mapping
- Qualitative assessment pass
- Server-side `verify_quotes()` logic

---

## Quick verify

1. Restart API after prompt changes
2. Run on **v9** → DB run should have `compare_prompt_version = v3`
3. Run on **v8** → stays `v2` (unchanged)
