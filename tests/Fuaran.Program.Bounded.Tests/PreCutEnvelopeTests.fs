/// K7 (docs/generic-tier.md §3.7), pinned BEFORE the hash swap it licenses.
///
/// Phase 1896 replaced the UI tier's `Hashing.sha256Hex` with Core's
/// `Hash.sha256Hex` in the signed envelope's tree hash, on the assumption that
/// the two are byte-identical (SHA-256 over the UTF-8 bytes, lowercase hex).
/// The falsifier the design note names is an envelope signed before the cut
/// that fails to verify after it. This is one: signed by the pre-cut code, its
/// bytes and public key committed verbatim below, and verified here by the
/// post-cut code. If the hash — or the canonical encoding, or the projection's
/// bytes — ever drifts, the recomputed preimage stops matching the signature
/// and this goes red.
module Fuaran.Program.Bounded.Tests.PreCutEnvelopeTests

open System
open System.Security.Cryptography
open Expecto
open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.UI.OpStream.Abstractions
open Fuaran.Program.Bounded
open Fuaran.Program.UI

/// The tree the pre-cut code signed: a static caption beside a button that
/// navigates.
let private tree: Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.markdown "m" "static"
                  Fuaran.button
                      "go"
                      { Defaults.button<obj> with
                          OnClick = Action.Navigate(TextSource.Literal "/next", NavigateTarget.Self) } ] }

/// The tree hash the pre-cut code computed, with the UI tier's hash.
[<Literal>]
let private PreCutTreeHash =
    "sha256:2d221262c858f3488cb0a61e17e3e5472fc17399fb057c2332c0f42a9a16a542"

/// The signer's PUBLIC key (SubjectPublicKeyInfo, P-256), as the pre-cut run
/// exported it. The private half was never kept.
[<Literal>]
let private PublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEH0cqPzYIBq+fM2NQLININQJQjOs4ySYsP2a0KZmJWF01lm3bsUa202ktuXRASwAOhLGswHWBb7jfcSr1z/qAaA=="

/// The signed envelope the pre-cut code produced, byte for byte.
[<Literal>]
let private PreCutEnvelope =
    """{"kind":"signed-demanded","version":1,"treeHash":"sha256:2d221262c858f3488cb0a61e17e3e5472fc17399fb057c2332c0f42a9a16a542","envelope":"{\"kind\":\"demanded\",\"version\":4,\"effects\":[\"Navigate\"],\"hostCalls\":[],\"stateNamespaces\":[],\"opaqueHandlers\":[],\"server\":null}","keyId":"k7-pre-cut","signature":"RikaGRTXMcrjUOp1p4ZLuH5/kNg807fOwks8xOlUDLs2t5k0UwKfWSB0vepCHZqXxPlb4qVh4NmHcF7E/mfO6Q=="}"""

let private publicEntry () : KeyDirectoryEntry =
    let key = ECDsa.Create()
    key.ImportSubjectPublicKeyInfo(Convert.FromBase64String PublicKey) |> ignore
    EcdsaP256.keyEntry "k7-pre-cut" key

[<Tests>]
let tests =
    testList
        "K7 — an envelope signed before the hash swap still verifies after it"
        [ test "Core's tree hash is the pre-cut hash, byte for byte" {
              Expect.equal (SignedEnvelope.treeHash tree) PreCutTreeHash "the content address is unchanged"
          }

          test "the pre-cut signed envelope verifies under the post-cut code" {
              let signed =
                  match SignedEnvelope.decode PreCutEnvelope with
                  | Ok signed -> signed
                  | Error failure -> failtestf "the committed envelope does not decode: %A" failure

              match
                  SignedEnvelope.verify EcdsaP256.claimVerifier (Some(publicEntry ())) Demanded.ofTree tree signed
                  |> Async.RunSynchronously
              with
              | Ok verified -> Expect.equal verified.TreeHash PreCutTreeHash "verified over the same content address"
              | Error refusal -> failtestf "the pre-cut envelope no longer verifies: %A" refusal
          }

          test "and the check bites: a one-byte edit to the tree is refused" {
              let edited =
                  Fuaran.dashboard
                      "root"
                      { Defaults.dashboard<obj> with
                          Children = [ Fuaran.markdown "m" "Static" ] }

              let signed =
                  match SignedEnvelope.decode PreCutEnvelope with
                  | Ok signed -> signed
                  | Error failure -> failtestf "the committed envelope does not decode: %A" failure

              let result =
                  SignedEnvelope.verify EcdsaP256.claimVerifier (Some(publicEntry ())) Demanded.ofTree edited signed
                  |> Async.RunSynchronously

              Expect.isError result "a tree other than the signed one does not verify"
          } ]
