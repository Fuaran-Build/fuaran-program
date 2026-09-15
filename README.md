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

## Verifying before load: the signed effect envelope

`Demanded.ofTree` answers what a program can ever ask for as a document. Since Phase 1744 that
document can be **signed**, and the signed pair verified **cold** — no host, no performer, no model:

- `SignedEnvelope.sign sink project tree` signs the pair (canonical tree hash, demanded document
  bytes) through a **host-supplied sink** consumed by shape (`Fuaran.Core.IAttestationSink`); the key
  never crosses it. The server placement's `ServerDemanded.sign` is the same call with the two-tier
  walk.
- `SignedEnvelope.verify crypto key project tree signed` verifies by **recomputation**: it re-derives
  the envelope from the tree, compares, and only then checks the signature over the pair it just
  recomputed, under a public key the host supplies. The envelope is never trusted from the
  signature — it is proof-carrying data the verifier can produce itself.

Three failures are named distinctly, never as a boolean: **drift** (the document does not describe
this tree — a tree whose effects exceed its envelope is this, with the excess enumerated), a **bad
signature** (the document is exact, but this is not the pair the signer made), and a **foreign key**.
A verify with no key **refuses** rather than skipping the signature check. What the signature attests
is the *pairing* — that a named key vouched for this tree with this envelope — and not that the
effects are safe; that remains `Demanded.check`'s question, asked of a host's coverage afterwards.
The demanded wire is unchanged: a consumer that ignores the signature reads what it read before. See
[DECISIONS.md](DECISIONS.md) D15.

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

### Continuous integration

`.github/workflows/ci.yml` runs the same `run.ps1` on every push to `main`, on every pull request,
and on demand. It assembles the runner into the layout the two repo-root-relative paths need — the
empty local folder feed `nuget.config` declares, and the corpus beside the repository — and then runs
the gate verbatim. Nothing is curated out: a CI-only subset drifts from what a maintainer runs, and
the divergence is invisible from both sides.

The corpus is not public today, so the lane fetches it with a repository credential. Where that
credential is not available — a pull request from a fork receives no secrets, by GitHub's design —
the run **says so, loudly, and makes no conformance claim**: it carries a warning annotation naming
the three legs that did not run, and a step whose title is the skip. The rest of the gate still runs,
so such a contribution is still verified for everything the corpus does not decide. A silent skip
would be the same defect the paragraph above rules out for a local run.

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
