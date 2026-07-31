# CLAUDE.md — fuaran-program (program/logic domain)

This repo is the **Fuaran program domain**: programs as data. Where the UI tier models an
*interface* as a typed tree, this domain models the *behaviour behind it* — a bounded, **total**
logic-tree algebra (sequencing, typed branching, named effects), the interpreters that run it, and
the program loop that connects it to a host. Ships as the `Fuaran.Program.*` NuGet package set.

**Status: skeleton.** The repo currently carries the domain identity, the design commitments
([DECISIONS.md](DECISIONS.md)), and a buildable/testable package shell. The first substantive
surfaces are the bounded-interpreter extraction (the interpreter shipped and proven in the UI
tier's server-driven packages moves to its placement-neutral home here) and the client program
loop that runs a wire-decoded tree interactively with no hand-authored update function.

Cross-repo development conventions (formatting mandate, language-baseline pinning, feed layout)
live at the maintainers' workspace level and are not shipped here; everything a contributor needs
for this repo is below.

## Design commitments (summary — DECISIONS.md is authoritative)

1. **Pipeline core** — the algebra is sequencing + typed branching + named effects; richer control
   structure (statecharts, workflows) is *vocabulary atop the core*, never a new evaluator.
2. **Total, not Turing-complete** — structural recursion over finite data and bounded iteration
   only; genuinely unbounded computation exits through registered host functions.
3. **Closed effect vocabularies** — per-placement effect DUs are closed; extensibility comes from
   registered, policy-gated host performers, never from widening the wire vocabulary.
4. **No foreign code** — a program tree carries data only; no closure survives the wire, and no
   interpreter ever invokes one.

Together these make a program tree validatable before execution, diffable, journalable, and safe
to run untrusted — the properties the whole domain exists to provide.

## Layout

```
fuaran-program/
├── src/Fuaran.Program/            # the domain package (skeleton — About only, today)
├── tests/Fuaran.Program.Tests/    # Expecto suite
├── Fuaran.Program.slnx
└── run.ps1                        # tool restore -> format -> build -> test
```

## Build pipeline

```powershell
pwsh ./run.ps1              # format -> build -> test (the pre-commit gate)
pwsh ./run.ps1 -SkipFormat  # fast iteration
```

## Formatting mandate

Every commit is preceded by a Fantomas pass over changed F# files (`dotnet fantomas src tests`;
the tool is pinned in [.config/dotnet-tools.json](.config/dotnet-tools.json) — `dotnet tool
restore` first if missing).

## Public vocabulary discipline

Anything under this repo is visible to OSS consumers. **Do not reference private, unpublished
projects or package names in shipped artefacts** — code comments, READMEs, and files that ship to
NuGet name only the public `Fuaran.*` package sets and generic "host" / "downstream consumer"
framing. Cross-references stay one-way: private consumers may reference this repo; this repo never
references them.
