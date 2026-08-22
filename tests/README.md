# Tests

Five suites, each its own Expecto assembly runner, plus the Fable parity leg.
`pwsh ./run.ps1` runs them all; `-SkipFable` drops the last one.

| Suite | What it pins |
|---|---|
| `Fuaran.Program.Tests` | the skeleton package |
| `Fuaran.Program.Bounded.Tests` | the interpreter's invariants and the server driver's loop |
| `Fuaran.Program.Runtime.Tests` | the client placement's loop and the effect seam |
| `Fuaran.Program.Server.Tests` | the server-logic placement: handlers, the server-effect seam, the pre-execution query-schema check, and tier-parity leg (d) |
| `Fuaran.Program.Parity.Tests` | tier parity, .NET legs (a) + (b) |
| `Fuaran.Program.Parity.Fable` | tier parity, leg (c) — the same runner under node |

## The tier-parity family

One tree, one event script, identical everywhere.

**The scenarios are not in this repository.** They are the **driver-semantics
family** of the program wire conformance corpus — the sibling clone the codec
suite already certifies against — and this repository reads them as a *host*
rather than owning them. Same resolution as the codec suite: the sibling path,
overridable with `FUARAN_PROGRAM_SPEC`, and its absence **fails** the gate rather
than skipping it. Each scenario is a directory of three files:

```
tree.json          the wire tree the scenario starts from
events.json        the scripted events
expectation.json   one entry per step, index 0 = before any event
```

Four legs read those same files: the server driver (`BoundedDriver`), the client
placement on .NET, the client placement compiled to JavaScript, and the
server-logic placement (`ServerSession`). The comparison is **per step**, and it
reports the FIRST divergence — comparing only final trees would pass a fold that
diverges at step 2 and re-converges at step 5, which is precisely the bug this
family exists to catch. The corpus states that as a harness obligation, so it is
now a contract rather than a local convention.

**Two comparison rules, and they are not the same.** A step's recorded tree is an
embedded *document*, so it is decoded and re-encoded through THIS host's own
encoder before anything is compared (`Runner.normaliseExpectation`) — a host with
a different canonical form is measured against its own bytes. A step's client
effects are recorded as strings holding those documents' own bytes and are
compared byte-for-byte, because that family's envelope is the specification's one
enumerated exception; putting them through a canonical encoder is exactly the
"fix" that would erase it.

**The enumeration is the corpus manifest, never a directory listing.** A scenario
the manifest forgot is a behaviour nobody is required to reproduce, and every leg
here asserts that the number of scenarios it ran equals the number the manifest
declares.

Leg (d) lives in `Fuaran.Program.Server.Tests` rather than in the shared runner,
because the shared runner is Fable-clean — leg (c) compiles it to JavaScript, and
a server loop has no browser leg by definition. It reads the same files through
the same loader; a separate corpus would prove nothing.

**Two fixtures are read two ways each.** `server-handler-call`'s tree names a
handler through a call action; `nested-handler-call` names the same handler from
inside a chain, between two writes. With no handler registered — which is every
leg but (d)'s second reading — the arm is a documented no-op, so both fixtures
pin that the server-logic placement inherits the shared algebra and diverges
*only* where a host has registered something. With the handler registered, the
same trees and the same event scripts exercise the server-effect arms. Same
fixture, different host: the axis the family exists to vary.

The nested one earns its own place rather than duplicating the first. It pins
that a call is **spliced where it sits** (DECISIONS.md D7): the write before it
is overwritten by the handler's own and the write after it survives, which a
top-level-only recognition could not produce. And because the registered handler
declares a host call in the middle of its stage list while reporting it last, the
family also carries the visible consequence of two-phase staging (D8) — the audit
trail is execution order, not stage order.

**Regenerating:** `dotnet run --project tests/Fuaran.Program.Parity.Tests --
--emit-fixtures [corpus/wire-fixtures]`. The emit refuses to write an expectation
while the placements disagree, because an expectation minted from a divergence
enshrines the bug as the contract. It survived the move into the corpus unchanged:
the home moved, the rule did not.

It writes the scenario **bytes** and nothing else. The corpus's manifest is the
authoritative enumeration and belongs to the corpus, so refreshing its digests and
step counts is that repository's own tool's job (`node
wire-fixtures/check-scenarios.mjs --write`), and adding a scenario to the index is
a deliberate hand edit there. A host able to add itself to the index it is
certified against would be grading its own paper.

**Two things this family has already caught**, both worth keeping in mind:

- A deliberately broken shared interpreter fails all three legs with a per-step
  report. That is the probe, and it was run.
- Running the probe is also what exposed a **stale-build hazard**: a solution
  build refreshes each project's own output but does not reliably re-copy a
  transitively-referenced assembly into a test project's `bin`, so the suite
  passed against a stale copy of the very library that had just been broken.
  `run.ps1` therefore invokes `dotnet run` WITHOUT `--no-build`. Do not add it
  back.

## Graduation — done, and what it settled

This family used to live here and is now the corpus's. Four things had to be
decided for it to travel, and all four are in the specification rather than in a
harness:

- **A manifest and a family discriminator.** Scenarios are enumerated in their own
  top-level array, under their own family list, with a `kind` no wire vector may
  spell — and they are directories of three files where a vector is one file whose
  bytes are the document. A codec harness cannot mistake one for the other, and
  the corpus's manifest checker enforces all three separations.
- **A stated host obligation.** The family applies only to a host that has
  **declared** it implements the bounded path. A host that only decodes, records,
  relays or validates these documents is **out of scope** — a different verdict
  from non-conformant, and kept different deliberately.
- **A placement-independent expectation format.** The two rules above, plus
  first-divergence reporting stated as a harness obligation rather than assumed.
- **A decision about the effect vocabulary.** Recognition is normative;
  *performance* is host-defined, so a surface that satisfies an effect differently
  is conformant; *refusal* is conformant too, because the vocabularies default to
  deny — but it must be reported as a denial carrying the derived capability.
  Silence is the only non-conformant answer, because a silently-dropped effect and
  a performed one are indistinguishable in the outcome document.

**Not done, and not pretended:** a second, independently-written bounded loop. One
host reproducing the family shows it is implementable; it does not triangulate it.
The corpus records that as deliberately deferred — and it now has a contract for
such a leg to certify against, which it did not before.
