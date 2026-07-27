# Another Project — Master Plan

A milestone plan: numbered headings, delivered ones marked in the heading or by a body status
line, and one deliberately blocked. The early milestones carry no marker at all, which is the
shape that makes the tally's backfill rule necessary.

## 11. Milestones

### M0 — Scaffold, CI, knowledge base (S)
**Goal:** real skeleton, green CI, privacy rules enforced.

### M1 — Layout engine spike (L)
**Goal:** prove text wrap around exclusions before any UI investment.

### M2 — Document model & file format (M)
**Goal:** real model plus round-trip and undo infrastructure.

### M3 — Read-only viewer (M) — **delivered 2026-07-26**
**Status:** implemented; design and open items in `docs/M3-spec.md`.

#### The sibling trap — one new command
A deeper heading belongs to the milestone above it, and must not end it.

### M4 — Text editing (L) — **delivered 2026-07-27**
**Post-milestone status (2026-07-27):** done. The graph was updated and the wiki ingested.

### M5 — Frames & direct manipulation (M)
**Blocked:** the drag interaction needs a decision from the owner about snapping.

### M6 — Imaging (M)
**Goal:** photos that look good with one button.

### Sizing & sequencing notes

Not a milestone, and the `M<digits>` anchor is what keeps it out of the tally.

- M1 before everything else.
- M6 can move if the imaging work slips.

## 12. Verification

- [x] Snapshots byte-identical across the three-operating-system matrix
- [!] An installer signed on macOS — needs an Apple Developer account
