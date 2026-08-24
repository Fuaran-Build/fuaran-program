# CLAUDE.md — fuaran-program (program/logic domain)

This repo is the **Fuaran program domain**: programs as data. Where the UI tier models an
*interface* as a typed tree, this domain models the *behaviour behind it* — a bounded, **total**
logic-tree algebra (sequencing, typed branching, named effects), the interpreters that run it, and
the program loop that connects it to a host. Ships as the `Fuaran.Program.*` NuGet package set.

**Status: three placements, one interpreter.** `Fuaran.Program.Bounded` carries the bounded
interpreter, the binding re-resolution pass, the resource budget and the server driver — moved whole
from the UI tier's server-driven package, so exactly one interpreter serves every placement.
`Fuaran.Program.Runtime` runs a wire-decoded tree interactively in the browser with no hand-authored
update function. `Fuaran.Program.Server` is the third placement: a generated tree names a
host-registered handler, and the handler runs as data behind a closed, default-deny server-effect
vocabulary. The design questions it opened are recorded in
[`docs/server-handler-atomicity.md`](docs/server-handler-atomicity.md); four were decided ahead of the
wire cut — where a call is recognised (D7), the host-effect atomicity mode (D8), result-target
ownership (D9), and what a query's reader declares (D10).

**The wire cut has happened, and this repo is its first conformant host.** The handler declared form,
the two effect vocabularies, the invocation record and the outcome report now have a **specification
and an executable conformance corpus of their own**, in a sibling home; `ProgramWire` (shared) and
`HandlerWire` (this placement) are the codec, and a fixture-driven suite certifies them against that
corpus. Three consequences are worth knowing before touching either file:

- **The corpus is a BUILD INPUT to this repo's gate, not a reference.** It is resolved as a sibling
  clone (the suite names the path it expects and honours `FUARAN_PROGRAM_SPEC`), and its absence
  **fails** the suite rather than skipping it — a conformance check that passes when its oracle is
  missing is worse than no check. **That now covers the tier-parity family too**: its scenarios
  graduated into the corpus as the driver-semantics family, so `tests/fixtures/` is gone and all four
  legs read the corpus. Graduation moved the home, not the truth — the emit path still refuses to
  record a trace while the placements disagree.
- **Forward coupling spans repositories.** A change to any specified member, ordering, encoding,
  refusal class or derived value updates the normative text, the schemas, the corpus's own emitter,
  its manifest **and** this codec, in one change-set. The corpus's `CONTRIBUTING.md` states the rule;
  landing four fifths of it leaves a specification of nothing.
- **Every string in a handler is now untrusted.** That was the asymmetry the spike relied on — a
  handler's body was host data, so only an endpoint came off the wire — and giving handlers a wire
  form abolished it. It changed no algorithm; it changed what a diagnostic is allowed to *say*. A
  diagnostic carries the derived capability, never a name a document supplied.

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
├── src/Fuaran.Program.Server/             # the server-logic placement — handlers as data + their wire form
├── docs/                                  # design notes: open questions recorded as input to later cuts
├── samples/client-program/                # the browser placement, wired to a real host
├── tests/                                 # one Expecto assembly runner per suite (see tests/README.md)
├── Fuaran.Program.slnx
├── tools/check-pins.ps1                   # preflight: every Fuaran.* pin resolves EXACTLY
├── pack-all.ps1                           # pack the producers into the shared local feed
└── run.ps1                                # tool restore -> format -> pins -> build -> test [-> pack]
```

Every test project is its own Expecto assembly runner; `run.ps1` invokes each in turn, then runs the
tier-parity family's Fable leg under node.

**One directory the tree above does not show, because this repository does not contain it:** the
program wire specification and its conformance corpus, resolved as a sibling clone. The conformance
suite reads it and names the exact path it expects; `FUARAN_PROGRAM_SPEC` overrides that path.

## Build pipeline

```powershell
pwsh ./run.ps1              # format -> pins -> build -> test (the pre-commit gate)
pwsh ./run.ps1 -SkipFormat  # fast iteration
```

The gate needs the conformance corpus present (see the note above). That is deliberate: the corpus,
not this repository, is the authority on the wire, so a gate that could go green without consulting it
would be certifying this host against itself.

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
