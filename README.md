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

## The gate decides on arguments, not only on the effect name

"`HostCall` allowed" is not "`HostCall` to `api.example.com` allowed". An admitted capability can
still carry a value that came off the wire, and a gate that decided on the capability's name alone
could not see where the call actually went. Since Phase 1739 a host declares an **argument policy**
beside the registration, as data — an **allow-list** over one named argument, a **ceiling** on the
declarative payload's canonical bytes, a **label** carried for a deployer — and the gate checks the
effect's actual arguments against it in its own position: after the capability is admitted by name,
and still before the pipeline, the apply engine and the performer lookup. An off-list endpoint is
refused with nothing having run, and the refusal names the host's own declaration rather than the
value it refused.

A capability nobody constrained is **unconstrained**, so a host that declares nothing behaves as it
did before. The declared policy travels in the demanded document and is part of what verification
recomputes, which is what lets a deployer read *HTTP to api.example.com, ≤ 64 KB* rather than *HTTP*
— and lets a verifier tell whether the bound still holds. See the placement's
[README](src/Fuaran.Program.Server/README.md#the-gate-decides-on-arguments-not-only-on-the-effect-name).

## Watching the denials: the pattern a probe leaves behind

Every refusal is already recorded — the effect registry fires a payload-free **denial sink** for each
one — and until Phase 1742 nothing watched it. A program repeatedly demanding effects its own
envelope never claimed is behaving the way a breach does, and that is a different fact from a program
being refused something it *did* claim: the first is a probe, the second is a policy working.

`DenialDetector` classifies each denial against the session's demanded document — **envelope-outside**
when the capability is absent from it, **ordinary** when it is present — counts the first class against
a tunable per-session threshold, and on a breach **reports the pattern before it acts**: the session,
the count, the threshold, and the capabilities reached for. Suspension is opted into
(`DenialDetector.suspending`); the default reports and does nothing else, so a host can run it in
report-only mode and see exactly what a suspending one would have acted on. When it does act it acts
through the controls that already exist — `ControlOp.Suspend`, recorded on the session's control
stream with the detector as a **machine** actor and the pattern itself as the reason.

A detector with no envelope, or one whose document carries no server tier, classifies **every** denial
as ordinary: `None` there means no server walk was performed, and reading "not asked" as "asked, and
the answer was nothing" would make every denial an intrusion signal. It adds no wire vocabulary, no
dependency, and no second denial stream. See [DECISIONS.md](DECISIONS.md) D16 for the control op it
raises.

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

### Mechanised: four of the interpreter's laws are theorems

The four laws the bounded interpreter's own header states — that its fold is total over the closed
`Action` union, that it never invokes a closure an action carries, that `Chain` is its homomorphism,
and that host-reserved `State` keys are not writable from a tree — are **proved**, in an F\* model of
the fold under a pinned prover with no admits. The extracted model is then run beside the shipped
interpreter over the conformance corpus's driver-semantics family, so the theorem is a claim about
the code rather than about a document.

That leg is separate from the gate above and stays that way: `.github/workflows/proofs.yml` runs
`proofs/check.ps1 -Runs 3`, and nothing in `run.ps1` or `dotnet build` needs a prover — the
extraction is committed precisely so a contributor with no interest in proofs never installs one.

**What is proved is narrow, and [`proofs/README.md`](proofs/README.md) is where the boundary is
drawn.** It carries the claims ladder — proved, differentially tested, assumed and stated, not
claimed — and it is the only place in this repository where the phrase "formally verified" appears.
The durable journal, the session cells, and the per-interaction resource budget are on the
not-claimed rung, explicitly: bounded code and bounded cost are two different properties, and only
the first of them has a theorem here. [`proofs.json`](proofs.json) is the same ladder as data.

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
