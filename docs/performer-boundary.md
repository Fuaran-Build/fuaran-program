# The performer boundary — what is constrained, what is trusted, and why no label model

**Status:** design note, Phase 1759 (2026-10-01); extended to the op performer by Phase 1981
(2026-10-02, D24). Records a DECISION about the state discipline at the performer boundary; the
mechanism it sits beside is `proofs/EffectGate.fst` and its ladder in
[`proofs/README.md`](../proofs/README.md#the-effect-gate-theorem).

## The boundary, as it ships

`Handler.run` (`src/Fuaran.Program.Server/Handler.fs`) reaches outside the interpreter in two arms:
`ServerEffect.HostCall`, through a performer the host registered under a name, and — under a
registered op performer (`OpPerformance.Performed`, Phase 1967, D19) — `ServerEffect.ApplyOps`, one
staged call per op, through the performer the host registered for the state axis. Both are staged in
the plan phase and run in the perform phase through ONE loop, and what crosses either boundary is
bounded in both directions:

- **In:** the gate decides on the capability before anything else is read (`gate_before_perform`);
  the argument policy decides on the declared arguments — a host call's, or an op's REACH (Phase
  1739, D19); and the performer receives VALUES, immutable, with no reference to the tree, the
  bindings, the store or the handler's accumulator: a host function receives the effect's
  declarative argument, a `JVal`; an op performer receives the planned state as of the op and the
  op (Phase 1974) — the domain's own immutable state value, not the handler's store.
- **Out:** the performer answers a `Result<JVal, string>` — a host function its result, an op
  performer its RECEIPT (Phase 1981): what it says it did. With a `ReturnContract` declared at
  registration (`ServerEffectRegistry.registerChecked`), a host call's value is checked before the
  perform phase lands it in the declared slot; with an `OpContract` declared at registration
  (`OpPerformance.performedChecked`), an op's receipt is checked against the planned state and the
  op before the op is reported as performed. A rejected one, on either arm, is a typed
  `PerformFailed` naming the contract and never the value, with the handler rolled back
  (`return_contract`, `op_return_contract`). An op's receipt lands in no slot: it is checked, recorded
  by the durable journal as the step's completed value (D23), and otherwise discarded.

The trust-ledger row therefore moves from "performers are trusted" to "performers are CONSTRAINED
at the boundary: policy before the effect, contract on return, values in and no reference" — for the
op performer as for the host function, which D19 had left "trusted on the terms a host function is"
when a host function's terms were already the constrained ones. It is still not a proof of the
performer — what it does when invoked is the host's — and the ladder says so.

**What a receipt is, and is not.** A receipt is the performer's own ACCOUNT of what it did, and the
contract checks the account. That is what makes the reach obligation checkable — "an op's reach
covers what its performer touches" (D24) becomes, for a verb, "every path the receipt names is one
the op's reach names" — and it is also the boundary of what it checks: a performer that overreached
and reported only what was within reach passes the contract, exactly as a host function that lied in
its result passes a return contract that the lie satisfies. The in-repo verb witness pins both sides
(`Receipt.withinReach` and the `Escaping` adversary, whose honest account of an escape is refused;
`tests/Fuaran.Program.Tests/VerbWitnessTests.fs`). Closing the other side — a receipt the host
computes rather than the performer reports, a performer sandboxed so it cannot touch what it does not
name — is host territory, outside the theorems, on the terms the extension hook is below.

## SecRef\*'s label model, read against this seam

SecRef\* (Andrici, Ahman, Hrițcu, Rivas, Winterhalter, ICFP 2025) labels heap references so that
verified and unverified code can share them: `private` references the unverified side cannot
reach, `shareable` ones it may read and write, `encapsulated` ones it may hold but whose
invariant the verified side keeps. The theorem is that unverified code can modify only
`shareable` references, and the discipline is enforced by typing the boundary. The model is
first-order by the paper's own account: a reference cannot hold an effectful function, which is
what keeps the invariant on `encapsulated` references statable.

Two things in this estate's shape are relevant to whether that model transfers.

**At the handler seam there is nothing to label.** The performer is handed no reference at all —
`args: JVal` is a value, and the store a `HostCall` lands into is the handler's own immutable
record, updated by the perform phase through `Assign` and never visible to the performer. In
SecRef\*'s terms, the set of `shareable` references handed to unverified code is EMPTY, and the
"only `shareable` references are modified" theorem holds vacuously, because the modification the
performer can make to interpreter state is the one the handler makes on its behalf: a value
into a declared slot, checked by the contract. A `shareable`-only rule for this seam is therefore
statable, and it is already a consequence of the shape — no label is needed to prove it, and
adding one would be a second mechanism for a property the type of the performer already fixes.

**At the extension hook there is nothing the model can reach.** `withExtensions` / `IHostedService`
is the forge SDK's composition hook, and the escape-hatch inventory
(`Fuaran-UI/docs/security/ESCAPE-HATCHES.md`, fuaran#900) resolved it to HOST-TRUST TERRITORY: a
hosted service runs in the process, with the process's reach, and the composition-capability gate
that would enforce an effect declaration defaults to disabled. The state it can touch is
higher-order — services, closures, the host's own singletons — and that is exactly the case
SecRef\* excludes. A label model over it would have to label the whole process, and the paper's
own restriction says the labels would not be sound once a reference held a function.

## The decision

**A `shareable`-only rule is statable at the handler seam, trivially, and NOT statable for the
extension hook; the ledger says the first as a consequence of the performer's type and the
second as host trust, and no label model is committed to.** Concretely:

1. The performer boundary's state discipline is recorded as: *a performer receives values and
   no reference; the only interpreter state it can affect is the slot its result lands in, and
   that result is checked against its declared contract first.* This is the `proofs.json` row
   `performer-state-discipline-not-labelled`, at the `policy` level — a statement of what the
   shape gives, not a claim a proof made.
2. The extension hook stays where the inventory put it: host-trust territory, outside the
   theorems. Its row in the ledger is unchanged by this phase.
3. SecRef\* is NOT adopted. The reason is recorded so it is not re-proposed as an obvious next
   step: at the one seam where its theorem would apply it is vacuous, and at the one seam where
   the ledger would want it the model is excluded by its own first-order restriction.

## What would reopen it

- A performer signature that hands a reference — a mutable store, a tree cursor, a callback into
  the handler — rather than a value. That is a contract change to `ServerEffectRegistry` or to
  `OpPerformance`, and the moment it happens the vacuous theorem becomes a real one and a label (or
  the absence of references, restored) has to be decided. The guard is the type: `HostFunctions`
  maps a name to `JVal -> Result<JVal, string>`, `OpPerformance.Performed` carries
  `'Node -> 'Op -> Result<JVal, string>` over the domain's immutable state, and a widening is
  visible in `STABILITY.md`.
- A first-order extension hook — a typed, effect-labelled DU case for `withExtensions`, which the
  research programme names as the alternative to host-trust territory. If the hook's contributions
  become values rather than services, SecRef\*'s model applies to them and the question is worth
  reopening; until then it is not.

## What this note does not decide

The stateful gate — Π over a monitor state, so that history-dependent policies ("at most B host
calls") become provable — is a separate proposal, recorded in the README's ladder under
`policy_sufficient`. It is a change to the gate's signature, not to the state discipline, and it
is not taken here.
