# Fuaran.Program

**Programs as data.** The Fuaran program domain models application behaviour the way the Fuaran UI
tier models interfaces: as a typed tree with a canonical wire form, validated before execution,
applied through an op engine, and journaled for replay. The algebra is deliberately **bounded and
total** — sequencing, typed branching, and named effects; no general recursion, no closures over
the wire, no foreign code — so a program emitted by a machine can be checked, run, diffed, and
audited like data, because it is data.

## The four commitments

They are stated fully, with their reasoning, in [DECISIONS.md](DECISIONS.md); in short:

1. **A pipeline core.** Sequencing + typed branching + named effects. Richer control structure
   (statecharts, workflows) is *vocabulary that lowers to the core*, never a second evaluator.
2. **Total, not Turing-complete.** Structural recursion over finite data and bounded iteration only.
   Genuinely unbounded computation exits through registered host functions, which the effect
   signature segregates.
3. **Closed effect vocabularies.** Each placement's effect set is a closed DU. Extensibility is a
   host act — register a policy-gated performer — never a widening of the wire vocabulary.
4. **No foreign code.** A program tree carries data only. No closure survives the wire, and no
   interpreter ever invokes one.

Together these are what make a program tree checkable *before* it runs, diffable, journalable, and
safe to run untrusted: bounded code plus bounded cost.

## Packages

| Package | What it is |
|---|---|
| `Fuaran.Program` | the domain package |
| `Fuaran.Program.Bounded` | the bounded interpreter, the binding re-resolution pass, the resource budget, the server driver, and the demanded-effect projection — [README](src/Fuaran.Program.Bounded/README.md) |
| `Fuaran.Program.Runtime` | the **client placement**: run a wire-decoded tree interactively in the browser with no hand-authored update function — [README](src/Fuaran.Program.Runtime/README.md) |
| `Fuaran.Program.Server` | the **server-logic placement**: handlers as data behind a closed, default-deny server-effect vocabulary, plus a second interpreter of the same algebra under deterministic replay over an effect journal — [README](src/Fuaran.Program.Server/README.md) |

One algebra, several placements — and one *interpreter* shared between them, which is what makes
"the placements agree" a property of the code rather than a claim in a document. The server
placement carries a second interpreter of that same algebra, for durable execution; its
exactly-once claim ships with its boundary attached rather than in general.

## Build

```powershell
pwsh ./run.ps1              # tool restore -> format -> pin preflight -> build -> test
pwsh ./run.ps1 -SkipFormat  # fast iteration
```

Requires the .NET 10 SDK (pinned in `global.json`), and node for the Fable parity leg
(`-SkipFable` drops it).

**The gate needs the program wire conformance corpus, and fails without it.** The handler declared
form, both effect vocabularies, the invocation record and the outcome report are specified by a
corpus that does not live in this repository — this repository is a conformant *host* of it, not its
author. The suite resolves the corpus as a sibling clone and honours `FUARAN_PROGRAM_SPEC` as an
override; its absence **fails** the suite rather than skipping it, because a conformance check that
goes green without its oracle is worse than no check at all. See [tests/README.md](tests/README.md).

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
