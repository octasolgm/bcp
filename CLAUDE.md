# Project instructions

## Demo accounts vs real accounts - keep them separate

This project has two completely separate worlds. Do not blur them.

- **Real accounts** use the actual document pipeline - real OCR/parsing engines (Tesseract, RapidOCR,
  Docling Light, Docling GLM-OCR, Azure Document Intelligence), real AI analysis, real uploaded documents.
  This is where all the local-pipeline engineering work happens (`bcp-api/Services/LocalDocs/`,
  `bcp-api/Controllers/NewDashboard/LocalDocumentsController.cs`, the `*-local` frontend components).
- **Demo accounts** do not use AI or any parsing engine at all. They are a simulation - pre-seeded,
  fixed judgment data (`bcp-api/SeedData/cbuae-aml-demo-judgments*.json`) that gets replayed into a demo
  analysis run. Nothing is parsed, nothing is OCR'd, nothing calls an AI or cloud service for a demo
  account. This is intentional and correct - it is a controlled, repeatable demo experience for
  prospects/clients, not a lightweight version of the real pipeline.

**The rule going forward:** any new pipeline engine, OCR option, or parsing feature is for real accounts
only, and must stay invisible to demo accounts unless explicitly asked otherwise. Demo accounts should
never see the engine nav groups (Tesseract/RapidOCR/Docling/Azure Doc Intelligence local pipeline pages)
or any new one added later - only their existing simple demo document view. This is already enforced via
`auth.isDemoViewer()` checks in `bcp-web/src/app/pages/nd/nd-shell.component.ts` (see the
`localEngineGroups` construction in `navForRole`) - when adding a new engine/nav group, add it inside that
same demo-hidden list, not outside it.

Do not touch the demo seed data, the demo workspace/interception logic
(`NdDemoInterceptionService.cs`, `NdDemoWorkspaceService.cs`, `AnalysisBundleSeedService.cs`,
`DemoAnalysisSeedService.cs`), or how demo accounts render results, as a side effect of pipeline work -
those stay exactly as they are, working as a fixed simulation, regardless of what changes on the real
pipeline. If a task explicitly asks to change something about demo accounts specifically (like fixing a
demo seed-data bug), that is fine - the point is pipeline work should never *accidentally* leak into or
change the demo experience.

## Weekly summary docs

At the end of any session in this repo where real work gets done (features, fixes, investigations), update the current week's report in `docs/weekly-reports/`.

- Folder per week, named like `23aug2026-29aug2026` (Monday through Sunday of that week, no spaces).
- Two files inside: `summary.md` and `detail.md`.
- `summary.md`: plain bullet points, grouped by date. Each date gets two subsections, "Tasks" and "Bug fixes". Keep it short, no long explanations.
- `detail.md`: the same week's work written out in full sentences, client facing tone, suitable for posting to the client portal as a product update. No internal file paths, class names, or repo jargon, describe things from the user's point of view.
- If the folder or files for the current week do not exist yet, create them.
- If a session's work does not fit neatly into "Tasks" or "Bug fixes" (an investigation with no fix yet, something still awaiting a decision), add it under a third "Investigated, follow up pending" list in summary.md, and mention it under a "Being worked on" heading in detail.md.
- This applies across every chat session working in this repo, not only the one that first set this up. If you are a fresh session with no memory of past work, check git log and git status/diff for the current week to reconstruct what happened, in addition to what happened in your own conversation.
- Do not use em dashes, smart quotes, or other characters that read as AI generated. Use plain hyphens and straight quotes.
- The user does not maintain these docs, they only review them. Keep them accurate and do not ask permission before updating them at the end of a work session.
