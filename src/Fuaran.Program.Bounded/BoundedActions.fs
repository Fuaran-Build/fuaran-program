namespace Fuaran.Program.Bounded

open Fuaran.UI.Types
open Fuaran.UI.Ops.Types
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.UI.ServerDriven

// ============================================================================
//  Bounded-`Action` interpreter — the placement-neutral core of the program
//  loop.
//
//  This is the interpreter that runs a program tree. It is deliberately
//  independent of WHERE the loop runs: the same fold drives a server session
//  (the driver in this package) and a browser client (`Fuaran.Program.Runtime`).
//  "One algebra, two placements" holds by construction — a single interpreter,
//  a single no-closure-invocation invariant, two hosts.
//
//  The algebra's shape — pipeline core, richer control structure as vocabulary
//  atop it rather than as a second evaluator — is DECISIONS.md D1. Do not
//  re-decide it here.
//
//  The complement is the UI tier's hand-authored driver, which keeps an Elmish
//  `update : 'Msg -> 'Model -> 'Model` on the server and natively invokes the
//  *full* `Action` space (the server-closure win — `Call`'s `onResult`,
//  `Computed`'s `f`, etc. run server-side because the closures are on the
//  server). This module is the other case: an **emitted, wire-decoded** tree,
//  where there is **no hand-authored `update` and no `'Msg` type**. The "model"
//  is the tree's *state store* (`BindingResolver.BindingSources`, whose `State`
//  map is the mutated channel); the "update" is *applying the bounded `Action`
//  set against that store*.
//
//  ── The load-bearing safety property (STATED AND TESTED) ────────────────────
//  Emitted trees are **bounded**: the wire format cannot carry arbitrary
//  closures. The JSON decoder substitutes inert placeholders for every closure
//  slot — `Action.Call`'s `onResult` decodes to `fun _ -> box "<closure>"`,
//  `Action.Dispatch`'s payload and `Action.ReadFileBody`'s `onRead` likewise
//  decode to inert sentinels, `Binding.Computed` to a placeholder. So a
//  generated tree carries no foreign code. THIS INTERPRETER ENFORCES THE OTHER
//  HALF: it **never invokes** any closure carried by an `Action`. The only state
//  mutation it performs is writing the `State` map (`SetState`); the only
//  outward effects are the closure-free `ClientEffect`s (`Navigate` /
//  `WriteToClipboard` / `ReadFileBody`).
//  Together — bounded wire ⇒ no foreign code in the tree; this interpreter ⇒ no
//  closure ever called — running a *generated* app has **no arbitrary-code-
//  execution surface**, which is the invariant multi-tenant "platform runs your
//  app" hosting rests on. (Resource bounds — no arbitrary *cost* — are the other
//  half, enforced by the driver's per-interaction budget; bounded code + bounded
//  cost = safe to run untrusted on shared infra.)
//
//  ── The bounded subset ─────────────────────────────────────────────────────
//  Only the **wire-representable** `Action` cases are reachable on this path:
//    - `SetState(key, value)` → write `sources.State[key]` (the only mutation).
//    - `Navigate` / `WriteToClipboard` / `ReadFileBody` → closure-free
//      `ClientEffect`s the host performs (inherently-browser arms; no server
//      form).
//    - `Chain` → fold in order, threading the store + concatenating effects.
//    - `Notify` / `AiTool` → host-channel computational arms with no store/DOM
//      effect of their own on the bounded path: NO-OP here (a generated app's
//      driver does not fan out to host channels / AI tools — that is the
//      hand-authored path's `InterpretHostEffect` seam). Documented, not
//      silently surprising.
//    - `Dispatch` → NO-OP: there is no `update` function to fold a `'Msg`
//      through on this path, and the wire `Dispatch` carries only the inert
//      sentinel anyway.
//    - `Call` → NO-OP **and the `onResult` closure is never invoked** (it is the
//      inert sentinel). `Call`'s API round-trip is a hand-authored-path concern;
//      a generated app expresses server data via `SetState` over the wire.
//  `Binding.Computed`'s closure is unreachable here too — it is a *binding*, not
//  an action, and binding re-resolution treats a decoded `Computed` as its
//  placeholder (it never carries author code over the wire).
// ============================================================================

/// The per-connection bounded store. A `BindingResolver.BindingSources` value:
/// the `State` map is the channel `Action.SetState` mutates and the loop
/// re-resolves `Binding.State` against; `Filters` / `Selections` /
/// `QueryResults` / `I18n` / `Locale` are host-supplied context the generated
/// tree reads but the bounded `Action` set does not mutate (there is no
/// `Action.SetFilter` / `Action.SetSelection` in the wire vocabulary).
type BoundedStore = BindingSources

/// A readable diagnostic from the bounded interpreter — the "this did nothing,
/// on purpose" signal. A generated tree that *intended* a `Call` / `Notify` /
/// `Dispatch` would otherwise get silent nothing — a dead end for emission
/// debugging. The no-op is still correct (the no-arbitrary-code invariant is
/// unchanged); this makes it observable so introspection tools can surface it.
[<RequireQualifiedAccess>]
type BoundedDiagnostic =
    /// The action is a documented no-op on the bounded (generated-app) path —
    /// it has no form there. `action` is the action's constructor name
    /// (log-safe, never payload values).
    | UnsupportedOnBoundedPath of nodeId: string * action: string
    /// The action was REFUSED, not merely inert: an `Action.Navigate` whose
    /// route is not a safe URL, or a State write addressing a host-reserved
    /// key. Distinct from `UnsupportedOnBoundedPath` because the two mean
    /// opposite things to whoever is debugging an emission: "this path does not
    /// implement that" versus "that was not allowed".
    | Refused of nodeId: string * action: string * reason: string

module BoundedDiagnostic =
    /// Human-readable, log-safe description for introspection / debugging.
    let describe (d: BoundedDiagnostic) : string =
        match d with
        | BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, action) ->
            sprintf
                "action '%s' on node '%s' is inert on the bounded path (no form for the generated-app loop)"
                action
                nodeId
        | BoundedDiagnostic.Refused(nodeId, action, reason) ->
            sprintf "action '%s' on node '%s' was refused: %s" action nodeId reason

/// The outcome of interpreting one bounded `Action`: the (possibly-updated)
/// store + the closure-free client effects to perform + the no-op diagnostics
/// (observability, never behaviour). The store is returned (not mutated in
/// place) so the loop threads it functionally — one interaction, one new store
/// value.
type BoundedOutcome =
    { Store: BoundedStore
      Effects: ClientEffect list
      Diagnostics: BoundedDiagnostic list }

module BoundedActions =

    /// Empty client-effect outcome that only carries the (unchanged or updated)
    /// store.
    let private store (s: BoundedStore) : BoundedOutcome =
        { Store = s
          Effects = []
          Diagnostics = [] }

    /// A documented-no-op outcome: unchanged store, no effects, one readable
    /// diagnostic naming the inert action.
    let private noOp (nodeId: string) (action: Action<obj>) (s: BoundedStore) : BoundedOutcome =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.UnsupportedOnBoundedPath(nodeId, Validation.describeAction action) ] }

    /// A REFUSED outcome: unchanged store, no effects, one readable diagnostic
    /// naming what was refused and why.
    let private refused (nodeId: string) (action: Action<obj>) (reason: string) (s: BoundedStore) : BoundedOutcome =
        { Store = s
          Effects = []
          Diagnostics = [ BoundedDiagnostic.Refused(nodeId, Validation.describeAction action, reason) ] }

    /// Interpret one resolved bounded `Action<obj>` against the store. `nodeId`
    /// is the originating event's node (for node-addressed client effects such
    /// as `ReadFileBody`). **Never invokes a closure carried by the action** —
    /// see the safety property at the top of this file. The only mutation is the
    /// `SetState` write; everything else either emits a closure-free
    /// `ClientEffect` or is a documented no-op.
    let rec runBoundedAction (nodeId: string) (action: Action<obj>) (s: BoundedStore) : BoundedOutcome =
        match action with
        // The one store mutation: write the `State` channel. The `JVal` payload
        // lowers to the structural obj shapes the store historically held —
        // `Binding.State` re-resolution reads it back.
        | Action.SetState(key, value, valueFrom) ->
            // The host-reserved key namespace is closed on the bounded path too.
            // This loop's whole premise is that the tree is untrusted, so a
            // generated tree writing `host.<x>` is exactly the case the
            // namespace exists for.
            if Fuaran.UI.Renderer.StateKeys.isHostReserved key then
                refused
                    nodeId
                    action
                    (sprintf
                        "State key '%s' is under the host-reserved '%s' namespace"
                        key
                        Fuaran.UI.Renderer.StateKeys.HostReservedPrefix)
                    s
            else
                // `valueFrom` (value XOR valueFrom, decode-enforced) evaluates AT
                // DISPATCH TIME against the store itself (the BoundedStore IS the
                // BindingSources). An unresolved / errored source performs NO
                // write and is diagnosed, never silent.
                let payload: Result<Fuaran.Core.JVal option, string> =
                    match valueFrom, value with
                    | Some b, _ ->
                        (match Fuaran.UI.Renderer.BindingResolver.resolveJVal s b with
                         | Resolved jv -> Ok(Some jv)
                         | NotResolved -> Ok None
                         | Errored m -> Error m
                         | I18nUnresolved k -> Error(sprintf "unresolved i18n key '%s'" k))
                    | None, v -> Ok v

                match payload with
                | Ok(Some jv) ->
                    store
                        { s with
                            State = Map.add key (JValObj.toObj jv) s.State }
                | Ok None -> refused nodeId action "valueFrom did not resolve to a value — no write performed" s
                | Error m -> refused nodeId action (sprintf "valueFrom errored: %s — no write performed" m) s

        // Inherently-browser arms → closure-free ClientEffects (no server form).
        // The route is sanitised before the effect is shipped; the host
        // navigates with its own router, so an unsafe scheme emitted here would
        // land as a client-side sink. A refusal emits no effect and one
        // diagnostic, never a silently-neutered `about:blank`.
        | Action.Navigate route ->
            match Fuaran.UI.Renderer.Sanitize.sanitizeUrl route with
            | Some safe ->
                { Store = s
                  Effects = [ ClientEffect.Navigate safe ]
                  Diagnostics = [] }
            | None -> refused nodeId action "route is not a safe URL" s
        | Action.WriteToClipboard text ->
            { Store = s
              Effects = [ ClientEffect.WriteToClipboard text ]
              Diagnostics = [] }
        | Action.ReadFileBody(_, _, encoding, _) ->
            // The `onRead` closure (3rd field) is the inert decode sentinel — NOT
            // invoked here. The host reads the browser-held blob and round-trips
            // the body back as a fresh `file-read` LiveEvent.
            let enc =
                match encoding with
                | FileReadEncoding.Text -> "Text"
                | FileReadEncoding.Base64 -> "Base64"
                | FileReadEncoding.DataUrl -> "DataUrl"

            { Store = s
              Effects = [ ClientEffect.ReadFileBody(nodeId, enc) ]
              Diagnostics = [] }

        // Compose: fold in order, threading the store, concatenating effects +
        // diagnostics.
        | Action.Chain actions ->
            actions
            |> List.fold
                (fun acc a ->
                    let next = runBoundedAction nodeId a acc.Store

                    { Store = next.Store
                      Effects = acc.Effects @ next.Effects
                      Diagnostics = acc.Diagnostics @ next.Diagnostics })
                (store s)

        // Computational host arms with no store/DOM effect on the bounded path:
        // documented no-ops (a generated app's loop does not fan out to host
        // channels / AI tools). Each emits a readable diagnostic so "this action
        // is inert on the generated-app path" is observable.
        | Action.Notify _
        | Action.AiTool _
        // Capability dispatch is a host channel; no store/DOM effect on the
        // bounded path (documented no-op, like AiTool).
        | Action.Invoke _ -> noOp nodeId action s

        // No `update` to fold a 'Msg through, and the wire `Dispatch` carries
        // only the inert sentinel: no-op (+ diagnostic).
        | Action.Dispatch _ -> noOp nodeId action s

        // The per-NodeId `Binding.Local` buffer is client-side on the bounded
        // path. The flushed value is delivered as the input event's payload,
        // which the loop applies as a `SetState` *before* interpreting the
        // commit; the `CommitLocal` action itself is therefore a store-level
        // no-op here (the explicit-commit boundary is a host concern) —
        // diagnosed, not silent.
        | Action.CommitLocal _ -> noOp nodeId action s

        // `Call`'s `onResult` is the inert decode sentinel — NEVER invoked (the
        // safety property). A generated app expresses server data via `SetState`.
        // The no-op emits the diagnostic — a generated tree that *intended* a
        // `Call` is observable, not a silent dead end.
        | Action.Call _ -> noOp nodeId action s
