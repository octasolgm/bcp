# System workflow diagrams

End-to-end picture of the compliance workflow: document intake, analysis, action plans,
maker/checker/reviewer approval, and the demo variant that never calls AI.

## 1. Document intake — upload → parse → extract → view

Every regulation document moves through fixed states. Buttons for a later stage stay disabled,
with a tooltip naming the missing step, until the earlier stage completes.

```mermaid
stateDiagram-v2
    [*] --> Uploaded: Upload PDF / Word
    Uploaded --> Parsed: Parse (Landing AI, or simulated for demo)
    Parsed --> Parsed: Re-parse
    Parsed --> Extracted: Extract regulation points
    Extracted --> Extracted: Re-extract / Repair pages
    Extracted --> Viewing: View points
    Viewing --> Extracted: Close panel

    note right of Uploaded
        Extract disabled — "Parse the document first"
        View disabled — "Extract points to view them"
    end note
    note right of Parsed
        View disabled — "Extract points to view them"
    end note
```

Available at every state, independent of processing: **Open**, **Download source**, **Delete**.

Internal policy documents use the same gated sequence.

```mermaid
stateDiagram-v2
    [*] --> Uploaded: Upload PDF / Word
    Uploaded --> Parsed: Parse (Landing AI, or simulated for demo)
    Parsed --> Extracted: Extract policy clauses / sections
    Extracted --> Viewing: View sections
```

## 2. Analysis run lifecycle

```mermaid
flowchart TD
    A[Select regulation document + internal policy docs] --> B[Create analysis run]
    B --> C{Demo user or demo-owned run?}
    C -- No --> D[Landing AI forward pass per clause]
    D --> E[LLM / dual verify pass]
    C -- Yes --> F[Replay seeded judgments - no AI]
    E --> G[Points scored: compliant / partial compliant / non compliant]
    F --> G
    G --> H[Gap report]
```

## 3. Gap → action plans → review → approval

```mermaid
flowchart LR
    G[Gap on a clause] --> P1[Action plan 1]
    G --> P2[Action plan 2]
    G --> Pn[Action plan n]
    P1 --> R1[Review comment]
    P1 --> R2[Review comment]

    subgraph Action plan fields
        F1[Action plan text]
        F2[Status: pending / resolved]
        F3[Priority: low / medium / high]
        F4[Target date - re-targetable, audited]
        F5[Responsibility: department or user]
        F6[Comment]
    end
```

All four roles (`super_admin`, `maker`, `checker`, `reviewer`) can add and edit action plans and
change target dates. Every target-date change is recorded with who changed it and when, and is
readable from the clock icon on the plan. Review comments can be added by `super_admin`,
`checker` and `reviewer`, and are listed in the right-hand panel.

## 4. Approval workflow

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> SubmittedForReview: Maker submits
    SubmittedForReview --> CheckerApproved: Checker approves
    SubmittedForReview --> Draft: Checker returns for correction
    CheckerApproved --> ReviewerApproved: Reviewer approves
    CheckerApproved --> Draft: Reviewer rolls back
    ReviewerApproved --> [*]
    Draft --> Draft: Retarget action plan dates
```

## 5. Re-checking gaps against a newly issued policy document

```mermaid
sequenceDiagram
    participant U as User
    participant W as Web app
    participant A as API
    participant AI as Landing AI / LLM

    U->>W: Upload new policy document (single clause or whole report)
    W->>A: POST attachments (point-level or run-level gap-evidence)
    A->>A: Store document, link to open gaps
    W->>A: POST rerun-point?evidenceOnly=true / rerun-with-evidence
    alt Real user
        A->>AI: Re-judge clause against the new document
        AI-->>A: Status, policy extract, document reference
    else Demo user or demo-owned run
        A->>A: Simulate: upgrade one compliance step, cite the uploaded file
    end
    A-->>W: Updated policy extract, reference, compliance status
    W-->>U: Gap refreshed in place
```

## 6. Priority rollup navigation

```mermaid
flowchart LR
    O[Overview: priority cards] --> P["/nd/action-plans/:priority"]
    P --> R[Analysis list with action plan counts]
    R --> G["Gap report with ?apPriority= — matching gaps highlighted and scrolled to"]
```

## 7. Retarget an action-plan date

Anyone who can edit an action plan can change its target date. The change is audited; the clock
icon on the plan opens the history.

```mermaid
sequenceDiagram
    participant U as User (any role)
    participant W as Web app
    participant A as API

    U->>W: Change target date (+ reason)
    W->>A: PATCH action plan
    A->>A: Write NdAnalysisActionPlanDateHistory (who, when, previous, new, reason)
    A-->>W: Updated plan
    U->>W: Click clock icon
    W->>A: GET date-history
    A-->>W: previous → new, changedByName, timestamp, reason
```

## 8. Real vs demo behaviour

| Stage | Real user | Demo user |
| --- | --- | --- |
| Parse | Landing AI | Simulated, staged progress |
| Extract | Landing AI clause extraction | Cloned from the demo admin template clause list |
| Regulation points shown | Whatever was extracted | Exactly the clauses configured in `nd/admin/demo` |
| Analysis | Landing AI + LLM | Seeded judgments replayed |
| Gap re-check with new evidence | Live re-judgement | Simulated one-step improvement citing the uploaded file |
| Action plans, reviews, exports | Live data | Demo-scoped data, same UI |

Demo regulation point counts stay consistent everywhere — regulation document list, points panel,
analysis runs and exports all read the same admin-managed clause set, and editing the template in
`nd/admin/demo` re-syncs existing demo documents and runs.
