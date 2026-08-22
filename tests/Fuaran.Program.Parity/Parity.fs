module Fuaran.Program.Parity.Runner

open Fuaran.UI.Types
open Fuaran.UI.Ops
open Fuaran.UI.OpStream.Abstractions
open Fuaran.UI.ServerDriven
open Fuaran.UI.ServerDriven.Validation
open Fuaran.UI.Renderer.BindingResolver
open Fuaran.Program.Bounded
open Fuaran.Program.Bounded.BoundedDriver
open Fuaran.Program.Runtime

// ============================================================================
//  Tier parity — one tree, one event script, identical everywhere.
//
//  "One algebra, two placements" is a claim about behaviour, and a claim about
//  behaviour is worth exactly what its executable check is worth. This module is
//  that check: it drives ONE scenario through every placement and compares them
//  STEP BY STEP.
//
//  Per-step is the load-bearing part. Comparing only final trees tells you that
//  something diverged, not where — and a fold that diverges at step 2 and
//  re-converges at step 5 would pass a final-tree comparison while being exactly
//  the bug this family exists to catch.
//
//  The legs:
//    (a) the server placement — `BoundedDriver`, whose transport is DomPatches
//    (b) the client placement on .NET — `Fuaran.Program.Runtime`
//    (c) the client placement under Fable — the same runner, compiled to JS
//
//  This module is Fable-clean and does no IO, so all three legs run the SAME
//  comparison code. Reading scenarios from disk is the host's job, because that
//  is the one part that genuinely differs between .NET and node.
//
//  ── The scenarios live in the conformance corpus ────────────────────────────
//  They are the corpus's DRIVER-SEMANTICS family, not this repository's private
//  fixtures: the same directory that specifies the wire also specifies what a
//  bounded loop DOES with it, and this repository certifies against that as a
//  host rather than as its author. Two rules follow, and both are stated where
//  they are enforced below:
//
//    - a step's resolved tree is compared SEMANTICALLY — the corpus's document
//      is decoded and re-encoded through THIS host's encoder before comparison,
//      so a host whose encoder differs is measured against its own bytes;
//    - a step's client effects are compared BYTE-for-byte, deliberately — their
//      envelope is the specification's one enumerated exception, and putting
//      them through a canonical encoder is exactly the "fix" that would erase it.
// ============================================================================

/// One scripted event. Deliberately narrower than `LiveEvent` — a fixture
/// declares intent, and the connection id and sequence are harness concerns.
type ScriptedEvent =
    { NodeId: string
      Event: string
      Payload: Map<string, string> }

/// What a placement produced at one step, and what the corpus records for it.
///
/// `ResolvedJson` is a wire TREE DOCUMENT. From a placement it is that
/// placement's own encoding of the resolved tree (a `Node` carries closures and
/// so has no structural equality — encoding is what makes two placements
/// comparable at all). From the corpus it is the document the scenario records,
/// which is why it must go through `normaliseExpectation` before a comparison:
/// the two are the same MEANING, and only accidentally the same bytes.
///
/// `Effects` are client-effect documents in their AS-EMITTED form, one per
/// effect, in order. Not merely the arm names: a family that recorded only the
/// arm could not tell a navigation to one route from a navigation to another,
/// and computing the wrong route is precisely the kind of fold defect this
/// family exists to catch.
type StepObservation =
    { ResolvedJson: string
      Effects: string list
      Refused: bool }

/// Where two placements stopped agreeing.
type Divergence =
    { Fixture: string
      Step: int
      LegA: string
      LegB: string
      Field: string
      ValueA: string
      ValueB: string }

module Divergence =
    /// A report that names the FIRST differing step and what differed in it.
    let describe (d: Divergence) : string =
        sprintf
            "%s: %s and %s diverged at step %d on %s\n  %s: %s\n  %s: %s"
            d.Fixture
            d.LegA
            d.LegB
            d.Step
            d.Field
            d.LegA
            d.ValueA
            d.LegB
            d.ValueB

/// A scenario: a wire tree, the event script to drive it with, and the
/// per-step expectation. `Expected` is empty when the scenario is being
/// generated rather than checked.
type Fixture =
    { Name: string
      TreeJson: string
      Events: ScriptedEvent list
      Expected: StepObservation list }

let private toLiveEvent (index: int) (ev: ScriptedEvent) : LiveEvent =
    { ConnId = "parity"
      NodeId = ev.NodeId
      Event = ev.Event
      Payload = ev.Payload |> Map.map (fun _ v -> LiveValue.Str v)
      LastSeq = index }

/// Canonical JSON of a resolved tree — the comparable projection. Canonical
/// encoding is what makes two trees from two placements comparable at all:
/// a `Node` carries handler slots, so structural equality is unavailable by
/// construction.
let private canonical (node: Node<obj>) : string = CanonicalJson.encodeNode node

// ─── Leg (a): the server placement ───────────────────────────────────────────

/// Drive a fixture through `BoundedDriver`. The fragment renderer is a stub:
/// this family compares the RESOLVED TREE and the effects, which is the shared
/// semantics, not the DOM patches, which are this placement's transport.
let runServerPlacement (fixture: Fixture) : Result<StepObservation list, string> =
    match JsonDecode.decodeNode fixture.TreeJson with
    | Error err -> Error(sprintf "decode failed: %A" err)
    | Ok wire ->
        let services =
            BoundedServices.createPermissive (fun node -> sprintf "<f id='%s'/>" node.Id)

        let session = BoundedDriver.init services empty wire

        let observations =
            fixture.Events
            |> List.mapi toLiveEvent
            |> List.scan
                (fun (session, _) ev ->
                    let next, out = BoundedDriver.step session ev

                    next,
                    Some
                        { ResolvedJson = canonical next.Resolved
                          Effects = out.Effects |> List.map ClientEffect.encode
                          Refused = out.Rejected.IsSome })
                (session, None)
            |> List.choose snd

        // The initial resolved tree counts as step 0: a placement that resolved
        // the tree differently BEFORE any event has already diverged.
        Ok(
            { ResolvedJson = canonical session.Resolved
              Effects = []
              Refused = false }
            :: observations
        )

// ─── Leg (b/c): the client placement (.NET and Fable run this same code) ─────

/// Drive a fixture through `Fuaran.Program.Runtime`. The render seam records
/// nothing here — the resolved tree is read from the step output, so this leg
/// needs no host at all.
let runClientPlacement (fixture: Fixture) : Result<StepObservation list, string> =
    match JsonDecode.decodeNode fixture.TreeJson with
    | Error err -> Error(sprintf "decode failed: %A" err)
    | Ok wire ->
        // Every built-in arm registered and permitted, so an effect the tree
        // emits is observable rather than refused — the effect SEAM has its own
        // tests; this family is about the fold agreeing.
        let effects =
            [ "WriteToClipboard"
              "Navigate"
              "PushState"
              "Focus"
              "Download"
              "ReadFileBody" ]
            |> List.fold (fun reg name -> EffectRegistry.register name ignore reg) EffectRegistry.denyAll
            |> EffectRegistry.permissive

        let services =
            { ProgramServices.createPermissive ignore with
                Effects = effects }

        let program = Program.mkBounded services empty wire

        let observations =
            fixture.Events
            |> List.mapi toLiveEvent
            |> List.scan
                (fun (program, _) ev ->
                    let next, out = Program.handleEvent program ev

                    next,
                    Some
                        { ResolvedJson = canonical out.Resolved
                          Effects = out.Effects |> List.map ClientEffect.encode
                          Refused = out.Rejected.IsSome })
                (program, None)
            |> List.choose snd

        Ok(
            { ResolvedJson = canonical program.Resolved
              Effects = []
              Refused = false }
            :: observations
        )

// ─── The comparison ──────────────────────────────────────────────────────────

/// Compare two placements' observations step by step, returning the FIRST
/// divergence. Field order matters for the report's usefulness: the resolved
/// tree first (the semantics), then the effects, then the refusal — a
/// divergence in the tree explains a later divergence in the effects, so
/// reporting the effects first would name the symptom and hide the cause.
let compare (fixture: string) (legA: string) (a: StepObservation list) (legB: string) (b: StepObservation list) =
    if List.length a <> List.length b then
        Some
            { Fixture = fixture
              Step = min (List.length a) (List.length b)
              LegA = legA
              LegB = legB
              Field = "step count"
              ValueA = string (List.length a)
              ValueB = string (List.length b) }
    else
        List.zip a b
        |> List.indexed
        |> List.tryPick (fun (step, (x, y)) ->
            let mismatch field va vb =
                Some
                    { Fixture = fixture
                      Step = step
                      LegA = legA
                      LegB = legB
                      Field = field
                      ValueA = va
                      ValueB = vb }

            if x.ResolvedJson <> y.ResolvedJson then
                mismatch "resolved tree" x.ResolvedJson y.ResolvedJson
            elif x.Effects <> y.Effects then
                mismatch "effects" (String.concat "," x.Effects) (String.concat "," y.Effects)
            elif x.Refused <> y.Refused then
                mismatch "refusal" (string x.Refused) (string y.Refused)
            else
                None)

// ─── The expectation, made placement-independent ─────────────────────────────

/// Bring a recorded expectation into THIS host's own terms before comparing it.
///
/// The corpus records each step's resolved tree as a wire tree DOCUMENT, not as
/// a string of one implementation's bytes. A host is therefore never held to the
/// corpus's encoder: it decodes the recorded document with its own decoder and
/// re-encodes it with its own encoder, so both sides of the comparison are its
/// own bytes and the only thing being compared is the MEANING.
///
/// The effects beside it are deliberately left alone. Their envelope is the
/// specification's one enumerated exception — a `kind` discriminator and
/// declaration-ordered members — and those bytes are pinned normatively rather
/// than left to a host. Putting them through a canonical encoder here would
/// silently "correct" the exception the family is supposed to preserve, so this
/// function touches the tree and nothing else.
let normaliseExpectation (name: string) (expected: StepObservation list) : Result<StepObservation list, string> =
    let rec go (index: int) (acc: StepObservation list) (rest: StepObservation list) =
        match rest with
        | [] -> Ok(List.rev acc)
        | step :: tail ->
            match JsonDecode.decodeNode step.ResolvedJson with
            | Error err -> Error(sprintf "%s: the expectation's tree at step %d does not decode: %A" name index err)
            | Ok wire ->
                // Reifying is safe and is the point: the recorded tree is being
                // re-ENCODED, never dispatched through, so its inert closure
                // slots are exactly what a resolved tree's are.
                go
                    (index + 1)
                    ({ step with
                        ResolvedJson = canonical (Fuaran.UI.Ops.Types.WireTree.reify wire) }
                     :: acc)
                    tail

    go 0 [] expected

/// Run every placement available in this host and compare them pairwise against
/// the first. Returns the divergences found, empty when the placements agree.
let checkFixture (fixture: Fixture) : Result<Divergence list, string> =
    match runServerPlacement fixture, runClientPlacement fixture with
    | Error e, _
    | _, Error e -> Error e
    | Ok server, Ok client ->
        let againstEachOther =
            compare fixture.Name "BoundedDriver" server "Runtime" client |> Option.toList

        if List.isEmpty fixture.Expected then
            Ok againstEachOther
        else
            match normaliseExpectation fixture.Name fixture.Expected with
            | Error e -> Error e
            | Ok expected ->
                Ok(
                    againstEachOther
                    @ (compare fixture.Name "expected" expected "Runtime" client |> Option.toList)
                )
