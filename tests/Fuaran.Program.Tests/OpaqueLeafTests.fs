module Fuaran.Program.Tests.OpaqueLeafTests

// ============================================================================
//  A leaf may declare itself OPAQUE (Phase 2130, DECISIONS.md D40), at the
//  toy witness.
//
//  What is pinned here:
//    * the declaration: `LeafDeclaration.none` declares nothing and is
//      analysable; `LeafDeclaration.opaque` names the act and its reason class;
//    * the projection: `ofAction` and `ofTree` carry every opaque leaf a tree
//      reaches — through a sequence, both arms of a selection, a repeat's body
//      and a literal `Each`'s lowered bodies — distinct and sorted, and a leaf
//      that declares nothing still reads as demanding nothing, so the two are
//      told apart;
//    * the document: version 8 carries `opaqueLeaves`, round-trips it, and
//      refuses a document without it rather than defaulting it;
//    * coverage: an opaque leaf is REFUSED unless the host accepted its reason
//      class, a refusal no effect policy lifts; accepting one class accepts
//      only that class;
//    * the signed envelope's drift names an opaque leaf the signed bytes did
//      not carry.
// ============================================================================

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Tests.ToyDomain

/// The toy witness, with `Hush` declared as an in-process escape instead of
/// the host call it names at the base witness. `Beep` is unchanged.
let private opaqueWitness: FullWitness<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect> =
    let opaqueView (action: ToyAction) : ActionView<ToyAction, ToyExpr> =
        match action with
        | Hush -> ActionView.Leaf(LeafDeclaration.opaque "in-process" "Hush")
        | other -> view other

    { witness with
        Dispatch =
            { witness.Dispatch with
                Action =
                    { witness.Dispatch.Action with
                        View = opaqueView } } }

let private hushLeaf = { Reason = "in-process"; Name = "Hush" }

let private noCall = fun () -> ()

[<Tests>]
let declaration =
    testList
        "Phase 2130 — a leaf can declare itself opaque"
        [ test "none declares nothing, and is analysable" {
              Expect.isEmpty LeafDeclaration.none.EffectKinds "no effect kind"
              Expect.isEmpty LeafDeclaration.none.HostCalls "no host call"
              Expect.isNone LeafDeclaration.none.Opaque "not opaque"
          }

          test "opaque names the act and its reason class, and demands nothing a walk can name" {
              let declared = LeafDeclaration.opaque "in-process" "Hush"
              Expect.equal declared.Opaque (Some hushLeaf) "the act and its reason"
              Expect.isEmpty declared.EffectKinds "no effect kind"
              Expect.isEmpty declared.HostCalls "no host call"
          } ]

[<Tests>]
let projection =
    testList
        "Phase 2130 — the demanded projection names every opaque leaf"
        [ test "a leaf that declares nothing and an opaque one are told apart" {
              let silent =
                  { witness with
                      Dispatch =
                          { witness.Dispatch with
                              Action =
                                  { witness.Dispatch.Action with
                                      View =
                                          fun a ->
                                              match a with
                                              | Hush -> ActionView.Leaf LeafDeclaration.none
                                              | other -> view other } } }

              let quiet = Demanded.ofAction silent Hush
              let opaque = Demanded.ofAction opaqueWitness Hush

              Expect.equal quiet Demanded.empty "a leaf declaring nothing projects to nothing"
              Expect.notEqual opaque Demanded.empty "an opaque leaf does not"
              Expect.equal opaque.OpaqueLeaves [ hushLeaf ] "it is named, with its reason"
              Expect.isEmpty opaque.HostCalls "and demands nothing else"
          }

          test "every composition shape carries it, distinct and sorted" {
              let action =
                  Seq
                      [ Beep 1
                        Pick(Const(JBool true), Hush, Seq [ Hush ], None)
                        Times(Bound.Literal 3, Hush)
                        ForEach([ JInt 1; JInt 2 ], "x", Hush) ]

              let projection = Demanded.ofAction opaqueWitness action
              Expect.equal projection.OpaqueLeaves [ hushLeaf ] "one entry per declared escape"
              Expect.equal projection.Effects [ "Sound" ] "beside the analysable demands"
          }

          test "the base witness declares no escape, so nothing moves for it" {
              let action = Seq [ Beep 1; Hush; Ring("/e", false, noCall) ]
              Expect.isEmpty (Demanded.ofAction witness action).OpaqueLeaves "no opaque leaf"
          }

          test "ofTree finds an opaque leaf in a child's handler" {
              let leaf =
                  { Id = "leaf"
                    Label = Const(JStr "leaf")
                    Handlers = [ "tap", Seq [ Hush; Beep 2 ] ]
                    Children = [] }

              let root =
                  { Id = "root"
                    Label = Const(JStr "root")
                    Handlers = []
                    Children = [ leaf ] }

              let projection = Demanded.ofTree opaqueWitness root
              Expect.equal projection.OpaqueLeaves [ hushLeaf ] "the escape is named"
              Expect.isEmpty projection.OpaqueHandlers "and is not an opaque HANDLER: its action survived"
          }

          test "union keeps every input's opaque leaves, once" {
              let a = Demanded.ofAction opaqueWitness Hush

              let b =
                  { Demanded.empty with
                      OpaqueLeaves =
                          [ { Reason = "in-process"
                              Name = "Another" } ] }

              Expect.equal
                  (Demanded.union [ a; b; a ]).OpaqueLeaves
                  [ { Reason = "in-process"
                      Name = "Another" }
                    hushLeaf ]
                  "sorted by reason then name, distinct"
          } ]

[<Tests>]
let document =
    testList
        "Phase 2130 — the demanded document carries opaqueLeaves at version 8"
        [ test "the version is 8, and it is the only one read" {
              Expect.equal Demanded.Version 8 "the version"
              Expect.equal Demanded.decodableVersions [ 8 ] "the versions read"
          }

          test "the member is encoded between iterations and server, and round-trips" {
              let projection = Demanded.ofAction opaqueWitness (Seq [ Hush; Beep 1 ])
              let bytes = Demanded.encode projection

              Expect.stringContains
                  bytes
                  "\"iterations\":[],\"opaqueLeaves\":[{\"reason\":\"in-process\",\"name\":\"Hush\"}],\"server\":null"
                  "the member and its place"

              Expect.equal (Demanded.decode bytes) (Ok projection) "decode inverts encode"
              Expect.equal (Demanded.decode bytes |> Result.map Demanded.encode) (Ok bytes) "and the bytes are stable"
          }

          test "a document without the member is refused, never defaulted" {
              let bytes =
                  Demanded.encode Demanded.empty |> fun s -> s.Replace(",\"opaqueLeaves\":[]", "")

              match Demanded.decode bytes with
              | Error failure ->
                  Expect.equal failure.Defect DemandedDefect.MissingMember "the class"
                  Expect.equal failure.Field "opaqueLeaves" "the member"
              | Ok _ -> failtest "a document without opaqueLeaves must not read as one with none"
          }

          test "a version-7 document is refused by version, not read through the version-8 lens" {
              let bytes =
                  Demanded.encode Demanded.empty
                  |> fun s -> s.Replace("\"version\":8", "\"version\":7")

              match Demanded.decode bytes with
              | Error failure -> Expect.equal failure.Defect DemandedDefect.UnknownVersion "the class"
              | Ok _ -> failtest "an earlier version must be refused"
          } ]

[<Tests>]
let coverage =
    testList
        "Phase 2130 — coverage refuses an opaque leaf unless its reason class is accepted"
        [ let projection = Demanded.ofAction opaqueWitness (Seq [ Hush; Beep 1 ])

          let permissive =
              HostCoverage.nothing
              |> HostCoverage.withEffects [ "Sound" ]
              |> HostCoverage.permissive

          test "the default accepts no reason class" {
              Expect.isEmpty HostCoverage.nothing.Opaque "nothing accepted by default"
          }

          test "a permissive effect policy does not accept an escape" {
              Expect.equal
                  (Demanded.checkProjection permissive projection)
                  [ CoverageFinding.UnacceptedOpaqueLeaf("in-process", "Hush") ]
                  "the escape is the one finding"
          }

          test "accepting the reason class clears it" {
              Expect.isEmpty
                  (Demanded.checkProjection (permissive |> HostCoverage.acceptingOpaque [ "in-process" ]) projection)
                  "covered"
          }

          test "accepting another class does not" {
              Expect.equal
                  (Demanded.checkProjection (permissive |> HostCoverage.acceptingOpaque [ "sandboxed" ]) projection)
                  [ CoverageFinding.UnacceptedOpaqueLeaf("in-process", "Hush") ]
                  "only the named class is accepted"
          }

          test "the finding is ordered after the namespaces and before the server tier, and says what it is" {
              let withNamespace =
                  { projection with
                      StateNamespaces =
                          [ { Namespace = "ns"
                              Written = true
                              Read = false } ] }

              let host = permissive |> HostCoverage.withStateNamespaces []

              let findings = Demanded.checkProjection host withNamespace

              Expect.equal
                  findings
                  [ CoverageFinding.UncoveredStateNamespace "ns"
                    CoverageFinding.UnacceptedOpaqueLeaf("in-process", "Hush") ]
                  "declaration order"

              let described =
                  Demanded.describe (CoverageFinding.UnacceptedOpaqueLeaf("in-process", "Hush"))

              Expect.stringContains described "opaque" "it says the leaf is opaque"
              Expect.stringContains described "in-process" "and names the reason class"
          } ]

[<Tests>]
let envelope =
    testList
        "Phase 2130 — the signed envelope's drift names an opaque leaf"
        [ test "an opaque leaf the signed bytes did not carry is excess, and nothing else is" {
              let action = Seq [ Hush; Beep 1 ]
              let signedOver = Demanded.ofAction witness action
              let recomputed = Demanded.ofAction opaqueWitness action

              // Drift is decided before the signature is consulted, so a
              // verifier that accepts nothing is enough to read it.
              let verifier: ClaimVerifier<string> =
                  { KeyId = id
                    Verify = fun _ _ _ -> async { return false } }

              let signed: SignedEnvelope =
                  { TreeHash = "address"
                    Envelope = Demanded.encode signedOver
                    KeyId = "k"
                    Signature = "" }

              match
                  SignedEnvelope.verifyAddressed verifier (Some "k") "address" recomputed signed
                  |> Async.RunSynchronously
              with
              | Error(VerifyRefusal.EnvelopeDrift drift) ->
                  Expect.equal drift.Excess.OpaqueLeaves [ hushLeaf ] "the escape is the excess"
                  Expect.isEmpty drift.Excess.Effects "no effect drifted"
                  Expect.isEmpty drift.Shortfall.OpaqueLeaves "nothing signed is missing an escape"
              | other -> failtestf "expected drift, got %A" other
          } ]
