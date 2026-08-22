# Tests

Five suites, each its own Expecto assembly runner, plus the Fable parity leg.
`pwsh ./run.ps1` runs them all; `-SkipFable` drops the last one.

| Suite | What it pins |
|---|---|
| `Fuaran.Program.Tests` | the skeleton package |
| `Fuaran.Program.Bounded.Tests` | the interpreter's invariants and the server driver's loop |
| `Fuaran.Program.Runtime.Tests` | the client placement's loop and the effect seam |
| `Fuaran.Program.Server.Tests` | the server-logic placement: handlers, the server-effect seam, and tier-parity leg (d) |
| `Fuaran.Program.Parity.Tests` | tier parity, .NET legs (a) + (b) |
| `Fuaran.Program.Parity.Fable` | tier parity, leg (c) — the same runner under node |

## The tier-parity family

One tree, one event script, identical everywhere. `fixtures/<name>/` holds the
triple:

```
tree.json                the wire tree, canonical JSON
events.json              the scripted events
expected-resolved.json   one entry per step, index 0 = before any event
```

Four legs read those same files: the server driver (`BoundedDriver`), the client
placement on .NET, the client placement compiled to JavaScript, and the
server-logic placement (`ServerSession`). The comparison is **per step**, and it
reports the FIRST divergence — comparing only final trees would pass a fold that
diverges at step 2 and re-converges at step 5, which is precisely the bug this
family exists to catch.

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
--emit-fixtures`. The emit refuses to write an expectation while the placements
disagree, because an expectation minted from a divergence enshrines the bug as
the contract.

**Two things this family has already caught**, both worth keeping in mind:

- A deliberately broken shared interpreter fails all three legs with a per-step
  report. That is the probe, and it was run.
- Running the probe is also what exposed a **stale-build hazard**: a solution
  build refreshes each project's own output but does not reliably re-copy a
  transitively-referenced assembly into a test project's `bin`, so the suite
  passed against a stale copy of the very library that had just been broken.
  `run.ps1` therefore invokes `dotnet run` WITHOUT `--no-build`. Do not add it
  back.

## Graduating these fixtures to a public corpus

Not done here, and deliberately so — recorded for whoever picks it up.

A public driver-semantics family would need, beyond what is on disk today:

- **Naming and a manifest.** The estate's wire-format corpus is authoritative
  through its `manifest.json` rather than through counts stated in prose; a
  driver-semantics family would need the same, plus a family discriminator so it
  is not mistaken for a codec fixture.
- **A stated host obligation.** These fixtures assert what a *bounded program
  loop* does, which is a stronger claim than wire round-tripping. A conformant
  host would have to declare that it implements the bounded path at all before
  the family means anything for it — a host that only decodes is not
  non-conformant, it is out of scope.
- **A placement-independent expectation format.** `expected-resolved.json`
  currently embeds canonical JSON produced by this repo's encoder. A public
  family would need the expectation expressed so a host with its own encoder can
  compare semantically rather than byte-for-byte.
- **A decision about the effect vocabulary.** Effects here are compared by
  discriminator. A public family would have to say whether a host that performs
  an effect differently (or refuses it) is conformant — which is a question about
  the closed-vocabulary decision, not about fixtures.
