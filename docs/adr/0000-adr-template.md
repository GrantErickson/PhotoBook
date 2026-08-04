# ADR-0000 — ADR template

This is the template for all PhotoBook Architecture Decision Records. Copy it to
`docs/adr/NNNN-short-kebab-slug.md` (four-digit, monotonically increasing number matching the
canonical file list in the spec kernel), fill in every section, and never delete a section — write
"None" if a section genuinely has nothing to say. ADRs are immutable history: when a decision
changes, write a new ADR and mark the old one Superseded rather than editing its content.

Related docs: [02-architecture.md](../02-architecture.md) · [README.md](../README.md)

## Status

One of: **Proposed** · **Accepted** · **Superseded by ADR-NNNN** (link the superseding ADR) ·
**Rejected**.
Include the decision date and who locked it, e.g.
`Accepted — locked by the user 2026-08-01; recorded 2026-08-03.`

## Context

The forces that make this decision necessary, stated so a reader in two years understands why the
losing options were live candidates. Cite product requirements as **R1–R28** (the numbered items
in the repo [readme.md](../../readme.md)) and kernel sections (e.g. "kernel §3") for any constant
or name you rely on. State constraints honestly: user-imposed constraints (e.g. "anything except
Python"), single-developer bandwidth, Windows-only scope, offline expectations.

## Options considered

Start with a one-line-per-option table:

| Option | Verdict | One-line summary |
|---|---|---|
| Chosen thing | **Chosen** | Why in ten words |
| Runner-up | Runner-up | What it wins, what killed it |
| Other | Rejected | The decisive flaw |

Then one short prose paragraph per losing option that is *fair to the loser*: what it does better
than the chosen option, and the specific, concrete reason it lost. An ADR that strawmans its
alternatives is worthless when circumstances change.

## Decision

Open with the decision as a blockquote, then bullet the drivers:

> **Decision:** One or two sentences stating exactly what we will do, with exact package/library
> names and versions where they are load-bearing.

- Driver bullets: the concrete facts (library names, measured numbers, requirement citations)
  that make this the right call. Every driver should be checkable, not vibes.

## Consequences

Two lists, both mandatory:

- **We gain:** the concrete benefits, with numbers where possible.
- **We pay:** the real costs and risks accepted — binary size, lock-in, maintenance exposure,
  capabilities given up. If mitigation exists (an isolation seam, a planned upgrade), name it here.

## Revisit when

Bullet list of specific, observable triggers that should reopen this decision (a requirement
appears, a dependency dies, a measured budget is blown). "Revisit if things change" is not a
trigger. If a runner-up exists, say which trigger promotes it.
