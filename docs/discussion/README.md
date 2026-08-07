# Discussion — Analysis workflows

Planning and comparison docs for Regul analysis. **Not all items here are implemented.**

## Hybrid retrieval (proposed)

| File | Description |
|------|-------------|
| [`REGUL-HYBRID-RETRIEVAL-WORKFLOW.md`](REGUL-HYBRID-RETRIEVAL-WORKFLOW.md) | Forward-only design: section extract + BM25 + embeddings |
| [`regul-hybrid-pipeline-detail.md`](regul-hybrid-pipeline-detail.md) | **V1** — step-by-step analysis run (each step explained) |
| [`regul-hybrid-pipeline-v2.md`](regul-hybrid-pipeline-v2.md) | **V2** — Ready Pipeline precompute + fast analysis run |
| [`regul-hybrid-pipeline-v2.png`](regul-hybrid-pipeline-v2.png) | V2 workflow diagram |
| [`regul-hybrid-retrieval-architecture.png`](regul-hybrid-retrieval-architecture.png) | Architecture diagram |
| [`regul-hybrid-retrieval-cost-breakdown.png`](regul-hybrid-retrieval-cost-breakdown.png) | FREE vs paid phases |

## Shipped analysis versions

| Version | Doc | Diagram |
|---------|-----|---------|
| **V3** — Regul workflow (`regul_pipeline`) | [`workflow-v3.md`](workflow-v3.md) | [`bcp-v3-pipeline-detail.png`](bcp-v3-pipeline-detail.png) |
| V3 keyword ranking detail | same doc | [`bcp-v3-ranking.png`](bcp-v3-ranking.png) |
| **V4** — Full markdown (`regul_pipeline_full`) | [`workflow-v4.md`](workflow-v4.md) | [`bcp-v4-pipeline-detail.png`](bcp-v4-pipeline-detail.png) |

## Implemented code & fixes (outside this folder)

| Doc | Location |
|-----|----------|
| Code map | [`../workflow/REGUL-AI-WORKFLOW-CODE.md`](../workflow/REGUL-AI-WORKFLOW-CODE.md) |
| §8.4 matching fix plan | [`../workflow/REGUL-FORWARD-MATCHING-FIX-PLAN.md`](../workflow/REGUL-FORWARD-MATCHING-FIX-PLAN.md) |
