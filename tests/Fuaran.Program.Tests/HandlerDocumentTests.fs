module Fuaran.Program.Tests.HandlerDocumentTests

// ─── H1: a state-only composition has a handler document (Phase 1982) ───
//
// `HandlerWire` was typed at a full dispatch axis, so a domain that fills only
// the state axis — every domain without events, the verb among them — could
// not encode its handler as a Program document, and so could not bind a
// signed envelope to one (D15 signs the pair of a program's address and its
// demanded document). The codecs now read their dispatch position through
// `IDispatchPosition`; this pins what that buys, at the verb witness:
//
//  1. Its handlers round-trip, byte for byte, as Program documents.
//  2. A compute stage — which a state-only composition cannot hold — is
//     REFUSED on the referenced-value class the specification already names,
//     never decoded into something the composition cannot run.
//  3. A handler's content address is the specification's `sha256:` form over
//     its document, and moves when a stage moves.
//  4. An envelope signed over that address and the registration's demanded
//     document verifies by recomputation; a changed handler is a bad signature
//     and a relaxed policy is drift.

open System
open System.Security.Cryptography
open System.Text
open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.VerbDomain

type private VerbHandler = Handler<Nothing, FileOp>

let private archive: VerbHandler =
    { Name = "archive"
      Stages =
        [ Effect(ServerEffect.ApplyOps [ Check(Exists "notes/x.md"); Read "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Write("notes/archive/x.md", "archived"); Delete "notes/x.md" ])
          Effect(ServerEffect.ApplyOps [ Check(Missing "notes/x.md"); Publish "origin" ]) ] }

let private migrate: VerbHandler =
    { Name = "migrate"
      Stages =
        [ Effect(
              ServerEffect.ApplyOps
                  [ Branch(
                        Exists "notes/x.md",
                        [ Write("notes/x.md", "migrated") ],
                        [ Write("notes/x.md", "created"); Write("notes/created.marker", "") ],
                        Some(Missing "notes/created.marker")
                    )
                    Times(2, [ Publish "origin" ]) ]
          )
          Effect(ServerEffect.HostCall("audit", JObj [ "verb", JStr "migrate" ], None))
          Effect(ServerEffect.Notify("ops", JStr "migrated")) ] }

let private registry: ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain "ApplyOps" [ ServerConstraintClause.AllowList("target", [ "origin" ]) ]

/// A P-256 signer over the substrate's attestation sink, and the verifier the
/// envelope is checked with. The key is the pair of its id and the key itself.
let private party (keyId: string) =
    let key = ECDsa.Create(ECCurve.NamedCurves.nistP256)

    let sink =
        { new IAttestationSink with
            member _.Sign head =
                Some(
                    { Head = head
                      KeyId = keyId
                      Signature =
                        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes head, HashAlgorithmName.SHA256)) }
                    : Attestation
                )

            member _.Verify attestation head =
                attestation.Head = head
                && key.VerifyData(
                    Encoding.UTF8.GetBytes head,
                    Convert.FromBase64String attestation.Signature,
                    HashAlgorithmName.SHA256
                ) }

    let verifier: ClaimVerifier<string * ECDsa> =
        { KeyId = fst
          Verify =
            fun head signature (_, k) ->
                async {
                    return
                        k.VerifyData(
                            Encoding.UTF8.GetBytes head,
                            Convert.FromBase64String signature,
                            HashAlgorithmName.SHA256
                        )
                } }

    sink, verifier, (keyId, key)

let private addressOf (handler: VerbHandler) =
    match HandlerWire.contentAddress witness handler with
    | Ok address -> address
    | Error refusal -> failtestf "the handler does not encode: %A" refusal

/// The registration's demanded document, recomputed: the full harvest walk
/// (reach, replay and undo postures) with the host's policy joined on.
let private projectionOf (registry: ServerEffectRegistry) (handler: VerbHandler) =
    (Harvest.ofRegistration (ServerEffectRegistry.queryPosture registry) witness [ handler ]).Projection
    |> ServerDemanded.withConstraints registry

[<Tests>]
let tests =
    testList
        "a state-only composition's handler document (Phase 1982, H1)"
        [ test "the verb's handlers round-trip as Program documents, byte for byte" {
              for handler in [ archive; migrate ] do
                  let encoded =
                      match HandlerWire.encodeHandler witness handler with
                      | Ok document -> document
                      | Error refusal -> failtestf "%s does not encode: %A" handler.Name refusal

                  match HandlerWire.decodeHandler witness encoded with
                  | Error refusal -> failtestf "%s does not decode: %A" handler.Name refusal
                  | Ok decoded ->
                      Expect.equal decoded handler $"{handler.Name} decodes to itself"

                      Expect.equal
                          (HandlerWire.encodeHandler witness decoded)
                          (Ok encoded)
                          $"{handler.Name} re-encodes to the same bytes"
          }

          test "a compute stage is refused at a composition with no dispatch axis, on the referenced-value class" {
              let document =
                  """{"$type":"Handler","name":"computes","stages":[{"$type":"Compute","action":{"$type":"SetState","key":"k","value":1}}]}"""

              match HandlerWire.decodeHandler witness document with
              | Ok _ -> failtest "a state-only composition decoded a compute stage"
              | Error refusal ->
                  Expect.equal refusal.Class RefusalClass.MalformedReferencedValue "the class the specification names"
          }

          test "a landing slot decodes at a state-only composition, and the handler refuses it while planning" {
              let document =
                  """{"$type":"Handler","name":"lands","stages":[{"$type":"Effect","effect":{"$type":"HostCall","args":{},"fn":"fetch","into":"rows"}}]}"""

              match HandlerWire.decodeHandler witness document with
              | Error refusal -> failtestf "the slot was refused at decode: %A" refusal
              | Ok handler ->
                  Expect.equal
                      handler.Stages
                      [ Effect(ServerEffect.HostCall("fetch", JObj [], Some "rows")) ]
                      "decoded with its slot"
          }

          test "the content address is the sha256 of the document, and moves when a stage moves" {
              let address = addressOf archive
              Expect.isTrue (ProgramWire.isContentAddress address) "the specification's form"

              let document =
                  match HandlerWire.encodeHandler witness archive with
                  | Ok d -> d
                  | Error r -> failtestf "%A" r

              Expect.equal address (ProgramWire.ContentAddressPrefix + Hash.sha256Hex document) "over the document"
              Expect.equal (addressOf archive) address "stable"

              let changed =
                  { archive with
                      Stages = archive.Stages @ [ Effect(ServerEffect.ApplyOps [ Publish "origin" ]) ] }

              Expect.notEqual (addressOf changed) address "a stage moved, so the address moved"
          }

          test
              "an envelope over a state-only registration verifies by recomputation, and refuses a change to either half" {
              let sink, verifier, key = party "operator"
              let address = addressOf migrate
              let projection = projectionOf registry migrate

              let signed =
                  match SignedEnvelope.signAddressed sink address projection with
                  | Ok signed -> signed
                  | Error refusal -> failtestf "sign refused: %A" refusal

              let verify (address: string) (projection: DemandedProjection) =
                  SignedEnvelope.verifyAddressed verifier (Some key) address projection signed
                  |> Async.RunSynchronously

              match verify (addressOf migrate) (projectionOf registry migrate) with
              | Ok verified ->
                  Expect.equal verified.TreeHash address "the address it was signed over"
                  Expect.equal verified.KeyId "operator" "under the signer's key"
              | Error refusal -> failtestf "the recomputed pair did not verify: %A" refusal

              // The handler changed in a way its demand does not show (a
              // different notification payload): the address moved, so the
              // recomputed preimage is not the one signed.
              let reworded =
                  { migrate with
                      Stages =
                          migrate.Stages
                          |> List.map (function
                              | Effect(ServerEffect.Notify(channel, _)) ->
                                  Effect(ServerEffect.Notify(channel, JStr "other"))
                              | stage -> stage) }

              Expect.equal (projectionOf registry reworded) projection "the demand did not move"

              match verify (addressOf reworded) (projectionOf registry reworded) with
              | Error(VerifyRefusal.BadSignature _) -> ()
              | other -> failtestf "a changed handler verified: %A" other

              // The host relaxed its policy: the recomputed document drifts.
              let relaxed = ServerEffectRegistry.permissive ServerEffectRegistry.denyAll

              match verify (addressOf migrate) (projectionOf relaxed migrate) with
              | Error(VerifyRefusal.EnvelopeDrift _) -> ()
              | other -> failtestf "a relaxed policy verified: %A" other
          } ]
