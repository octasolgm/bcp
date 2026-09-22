# Structural extraction bug: nested numbering gets split off as a fake new clause

Found while reviewing a real document (TFS Guidelines, clause 3.4 "Name Screening"). See
the comparison diagram shown in chat alongside this doc.

## The bug, in one line

The regex that detects clause headings cannot tell the difference between a real
top-level clause number and a numbered sub-list item sitting inside another clause's own
text - both look identical to it: a number, then a capitalized title.

## The real example that exposed it

Clause 3.4 of the TFS Guidelines contains a numbered sub-list:

```
3.4. Name Screening
...
1. Ownership/Control Rule: Individuals or legal entities that are directly or
   indirectly owned or controlled ...
```

`"1. Ownership/Control Rule: ..."` matches the exact same "number + title" shape the
extractor looks for to detect a brand new clause. It has no way to know this "1." is a
sub-item that belongs inside 3.4, not a new clause of its own.

## What actually happens today

- Clause 3.4 gets cut short - only its intro line is kept under "3.4".
- Everything from "1. Ownership/Control Rule" onward - the entire sub-list, several
  paragraphs of real compliance content - gets filed as its own separate chunk, labeled
  clause "1".
- Nothing is deleted or lost - every word still ends up saved somewhere - but it's under
  the wrong clause number, disconnected from 3.4 where it actually belongs.
- Worse: if another part of the same document also has a bare "1." sub-list (this
  document's own Annex 1 "Red Flag Indicators" section does), both unrelated pieces of
  text end up labeled clause "1" - ambiguous for anyone citing or searching by clause
  number later.

## Where in the code

`bcp-api/Services/LocalDocs/LocalSectionSplitter.cs` - `TryMatchHeading()` runs the
numbered-heading regex against every line with no concept of nesting or depth. Any match
starts a brand new clause, unconditionally.

## Why this matters more than the "no numbering at all" case

Semantic extraction (see `PIPELINE-WORKFLOW.md`) was planned as a fallback for documents
with *no* numbering at all. This is a different failure mode - the document *is* numbered,
just at more than one level, and the current logic can't tell the levels apart. Semantic
extraction wouldn't automatically prevent this either, since structural extraction runs
first and would still make the same mistake before any fallback logic ever triggers.

## Status: found, not yet fixed

Two real ways to fix it, not yet decided or built:

1. Teach the regex/splitter about nesting - e.g. don't start a new top-level clause from a
   bare single-level number ("1.") while already inside a multi-level clause ("3.4") whose
   sub-items would plausibly be numbered that way.
2. Use meaning (the same embedding comparison semantic extraction would use) as a
   secondary check - if a candidate new heading's content reads as a continuation of the
   same topic as what came before it, don't treat it as a new clause even though the
   number pattern matched.

Neither is built yet. This file exists so the bug doesn't get lost - update this file's
"Status" line once a fix ships, and say which of the two approaches (or another) was
used.
