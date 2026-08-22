# CLAUDE.md — fuaran-program (program/logic domain)

This repo is the **Fuaran program domain**: programs as data. Where the UI tier models an
*interface* as a typed tree, this domain models the *behaviour behind it* — a bounded, **total**
logic-tree algebra (sequencing, typed branching, named effects), the interpreters that run it, and
the program loop that connects it to a host. Ships as the `Fuaran.Program.*` NuGet package set.

**Status: three placements, one interpreter.** `Fuaran.Program.Bounded` carries the bounded
interpreter, the binding re-resolution pass, the resource budget and the server driver — moved whole
from the UI tier's server-driven package, so exactly one interpreter serves every placement.
`Fuaran.Program.Runtime` runs a wire-decoded tree interactively in the browser with no hand-authored
update function. `Fuaran.Program.Server` is a **spike** at the third placement: a generated tree
names a host-registered handler, and the handler runs as data behind a closed, default-deny
server-effect vocabulary. It commits no wire form — see
[`docs/server-handler-atomicity.md`](docs/server-handler-atomicity.md) for the questions the wire cut
inherits, three of which are now decided ahead of it: where a call is recognised (D7), the host-effect
atomicity mode (D8), and result-target ownership (D9).

The dependency direction is settled and one-way — this repo consumes published `Fuaran.UI.*`
packages, and nothing in the UI tier references back ([DECISIONS.md](DECISIONS.md) D4/D5). That is
what keeps a cold clone buildable against published packages alone.

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
├── src/Fuaran.Program/                    # the domain package (skeleton — About only, today)
├── src/Fuaran.Program.Bounded/            # the bounded interpreter + re-resolution + budget + server driver
├── src/Fuaran.Program.Runtime/            # the client (browser) placement of the program loop
├── src/Fuaran.Program.Server/             # the server-logic placement — handlers as data (spike)
├── docs/                                  # design notes: open questions recorded as input to later cuts
├── samples/client-program/                # the browser placement, wired to a real host
├── tests/                                 # one Expecto assembly runner per suite (see tests/README.md)
├── Fuaran.Program.slnx
├── pack-all.ps1                           # pack the producers into the shared local feed
└── run.ps1                                # tool restore -> format -> build -> test [-> pack]
```

Every test project is its own Expecto assembly runner; `run.ps1` invokes each in turn, then runs the
tier-parity family's Fable leg under node.

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
