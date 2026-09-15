namespace Fuaran.Program.Bounded

open Fuaran.UI.Types
open Fuaran.UI.OpStream.Abstractions

// ============================================================================
//  The signed effect envelope — proof-carrying data an operator verifies before
//  load (Phase 1744).
//
//  `Demanded.ofTree` answers "what can this program EVER ask for" as a document
//  that can be stored where the tree never travels. Nothing signed it, so a
//  deployer handed a tree and an envelope held a claim, not evidence. This file
//  adds the binding: the PAIR (canonical tree hash, canonical envelope bytes) is
//  signed, and verification is by RECOMPUTATION.
//
//  ── Why recomputation, and what the signature therefore attests ────────────
//  A verifier does not read the envelope and believe it. It decodes the tree,
//  re-derives the envelope through the same total walk, compares, and only then
//  checks the signature — over the preimage it just RECOMPUTED, never over the
//  bytes it was handed. So the envelope is proof-carrying data: its contents
//  need no trust in whoever wrote them, because the verifier can produce them
//  itself. What the signature adds is exactly the thing recomputation cannot
//  give — that a named key vouched for THIS tree paired with THIS envelope —
//  and nothing else. It does not say the effects are safe; it says the pairing
//  is the signer's. Whether the demanded effects are acceptable is
//  `Demanded.check`'s question, asked of a host's coverage, afterwards.
//
//  ── Three failures, named distinctly ────────────────────────────────────────
//  A bare boolean would collapse three facts that demand different responses:
//
//    EnvelopeDrift   the recomputed envelope is not the signed one. A tree
//                    whose effects EXCEED its signed envelope is this, with the
//                    excess enumerated — the case verify-before-load exists for.
//    BadSignature    the envelope describes the tree exactly, but the signature
//                    does not verify over the recomputed pair under the offered
//                    key: the tree is not the one the signer paired (its hash
//                    moved without moving a demand), or the bytes are forged.
//    ForeignKey      the signature names a key the verifier was not offered. A
//                    signature cannot be checked under a key it was not made
//                    with, so this is refused before anything is recomputed.
//
//  And one refusal that is not a tamper case: NoKey. A verify with no public key
//  REFUSES rather than skipping the signature check — a check that quietly
//  degrades to "the envelope matches" when no key is supplied is the check an
//  operator believes they ran and did not.
//
//  ── The signer is the host's, by shape ──────────────────────────────────────
//  Signing goes through `Fuaran.Core.IAttestationSink`, the synchronous
//  attestation seam the substrate already carries: it signs an opaque string
//  and answers with a key id and a signature, or `None` for the no-op posture.
//  The key never crosses it. Verification takes the PUBLIC key as a
//  `KeyDirectoryEntry` and the crypto as `IClaimSignatureVerifier`, both from
//  the UI tier's attestation seam — a host supplies the pair, exactly as it
//  supplies an effect performer. This package holds no cryptography and takes
//  no new dependency: the tree hash is the tier's Fable-clean SHA-256 over the
//  tier's canonical encoding, so the same preimage is computed on every runtime
//  the interpreter runs on.
//
//  ── What this deliberately is not ───────────────────────────────────────────
//  A new envelope shape. `Demanded.encode` / `decode` are unchanged and the
//  signed record carries the demanded document's bytes VERBATIM beside the
//  signature, so a consumer that ignores the signature reads what it read
//  before. Nor is it a trust decision about the key: whether the offered key is
//  one to believe — its lifecycle, its revocation, who it belongs to — is the
//  host's key directory's question, answered before the key is handed here.
// ============================================================================

/// The signed pair, with the signature beside it.
///
/// `Envelope` is the demanded document's bytes exactly as `Demanded.encode`
/// produced them — a consumer that wants the projection and nothing else calls
/// `Demanded.decode` on it and is none the wiser. `TreeHash` is the content
/// address of the tree's canonical encoding, carried so a reader can tell WHICH
/// tree the record is about without holding it; it is diagnostic, never
/// trusted — `verify` recomputes it.
type SignedEnvelope =
    { TreeHash: string
      Envelope: string
      KeyId: string
      Signature: string }

/// Why a sign produced no record.
[<RequireQualifiedAccess>]
type SignRefusal =
    /// The sink answered `None` — the named unattested posture. Nothing was
    /// signed, and this says so rather than handing back an empty signature.
    | SinkDeclined
    /// The sink answered, but for a head other than the preimage it was given.
    /// A signature over something else is not a signature over this pair, and
    /// recording it would be recording a claim the bytes do not cover.
    | SinkAnsweredForAnotherHead

/// What the tree demands beyond its signed envelope, and what the envelope
/// names that the tree does not — both as projections, so a reader sees the
/// EXCESS in the vocabulary the envelope itself uses.
///
/// The server tier's `None`-versus-empty distinction is carried through:
/// `Excess.Server = Some` empty-tier means the recomputation walked a server
/// tier the signed document says was never walked, and `Shortfall.Server` says
/// the converse. Neither is silently read as "nothing".
type EnvelopeDrift =
    {
        /// Demanded by the tree, absent from the signed envelope. The dangerous
        /// direction: an effect the envelope's reader was never told about.
        Excess: DemandedProjection
        /// Named by the signed envelope, not demanded by the tree. Over-declared
        /// rather than dangerous, and drift all the same — the document does not
        /// describe this tree.
        Shortfall: DemandedProjection
        /// `Some` when the signed bytes are not a demanded document at all. The
        /// excess is then the WHOLE recomputation — everything the tree demands
        /// is beyond an envelope that says nothing readable — and the shortfall
        /// is empty, since nothing could be read to fall short.
        Unreadable: DemandedDecodeFailure option
    }

/// Why a signed pair did not verify. Checked in the order declared, and the
/// first refusal is the answer: a key must be offered before anything is
/// checked, the key must be the signer's before the signature can mean
/// anything, the envelope must describe the tree before the pairing is worth
/// attesting, and only then is the signature itself examined.
[<RequireQualifiedAccess>]
type VerifyRefusal =
    /// No public key was offered. REFUSED, never skipped — see the header.
    | NoKey
    /// The record was signed under one key id and the verifier was offered
    /// another. Nothing was recomputed: a signature cannot be checked under a
    /// key it was not made with, and reporting drift here would tell the
    /// operator something about a document nobody has vouched for.
    | ForeignKey of signedBy: string * offered: string
    /// The recomputed envelope is not the signed one; the difference is
    /// enumerated. A tree whose effects exceed its envelope is this case.
    | EnvelopeDrift of drift: EnvelopeDrift
    /// The envelope describes the tree exactly, but the signature does not
    /// verify over the RECOMPUTED preimage under the offered key. Both tree
    /// hashes are carried so a reader can tell "the tree moved without moving a
    /// demand" (they differ) from "the bytes are forged" (they agree).
    | BadSignature of carriedTreeHash: string * recomputedTreeHash: string

/// A pair that verified: the RECOMPUTED facts, never the carried ones. A
/// consumer that goes on to check coverage does so against `Projection`, which
/// the verifier derived from the tree itself.
type VerifiedEnvelope =
    { TreeHash: string
      Projection: DemandedProjection
      KeyId: string }

module SignedEnvelope =

    /// The signed record's own kind and version — carried IN the record, on the
    /// same argument `Demanded.encode` makes: a consumer that finds one on disk
    /// can tell what it is without knowing who wrote it.
    [<Literal>]
    let Kind = "signed-demanded"

    [<Literal>]
    let Version = 1

    /// The content address of a tree: the tier's SHA-256 over the tier's
    /// canonical encoding, rendered in the `sha256:` form `ProgramWire` pins.
    /// Fable-clean on both counts, so the preimage is the same bytes on every
    /// runtime the interpreter runs on.
    let treeHash (root: Node<obj>) : string =
        ProgramWire.ContentAddressPrefix
        + Fuaran.UI.Hashing.sha256Hex (CanonicalJson.encodeNode root)

    /// The members the signature covers, rendered once so the preimage and the
    /// record it sits in cannot spell them differently.
    let private signedMembers (treeHash: string) (envelope: string) : string =
        "\"kind\":"
        + Demanded.q Kind
        + ",\"version\":"
        + string Version
        + ",\"treeHash\":"
        + Demanded.q treeHash
        + ",\"envelope\":"
        + Demanded.q envelope

    /// The canonical preimage of a pair — the string the sink signs and the
    /// verifier recomputes. Self-describing (kind and version ride in it), so
    /// the bytes cannot be mistaken for any other attestation this seam is
    /// asked to sign.
    let preimage (treeHash: string) (envelope: string) : string =
        "{" + signedMembers treeHash envelope + "}"

    /// Sign a tree paired with its demanded envelope, through a host-supplied
    /// sink. `project` is the walk that produces the envelope — `Demanded.ofTree`
    /// for the client tier, or a placement's own two-tier walk — so the same
    /// function serves every placement and the walk is named at the call site.
    let sign
        (sink: Fuaran.Core.IAttestationSink)
        (project: Node<obj> -> DemandedProjection)
        (root: Node<obj>)
        : Result<SignedEnvelope, SignRefusal> =
        let hash = treeHash root
        let envelope = Demanded.encode (project root)
        let head = preimage hash envelope

        match sink.Sign head with
        | None -> Error SignRefusal.SinkDeclined
        | Some attestation when attestation.Head <> head -> Error SignRefusal.SinkAnsweredForAnotherHead
        | Some attestation ->
            Ok
                { TreeHash = hash
                  Envelope = envelope
                  KeyId = attestation.KeyId
                  Signature = attestation.Signature }

    // ─── drift ───────────────────────────────────────────────────────────────

    let private except (xs: 'a list) (ys: 'a list) : 'a list =
        xs |> List.filter (fun x -> not (List.contains x ys))

    let private serverExcept (a: ServerDemand) (b: ServerDemand) : ServerDemand =
        { Effects = except a.Effects b.Effects
          Capabilities = except a.Capabilities b.Capabilities
          Functions = except a.Functions b.Functions
          Channels = except a.Channels b.Channels
          Replay = except a.Replay b.Replay }

    /// Everything `a` demands that `b` does not. On the server tier the option
    /// is preserved: `a` carrying a tier `b` does not is reported as that whole
    /// tier — an empty one included, because "walked and found nothing" beyond
    /// "never walked" is a difference in facts, not in demands.
    let private projectionExcept (a: DemandedProjection) (b: DemandedProjection) : DemandedProjection =
        { Effects = except a.Effects b.Effects
          HostCalls = except a.HostCalls b.HostCalls
          StateNamespaces = except a.StateNamespaces b.StateNamespaces
          OpaqueHandlers = except a.OpaqueHandlers b.OpaqueHandlers
          Server =
            match a.Server, b.Server with
            | None, _ -> None
            | Some tier, None -> Some tier
            | Some tier, Some other -> Some(serverExcept tier other) }

    /// The drift between the recomputed projection and the signed bytes, or
    /// `None` when the signed bytes read back as exactly the recomputation.
    ///
    /// Compared as DOCUMENTS, not as bytes: the signature is over the
    /// recomputed bytes regardless, so a re-serialised copy that still says the
    /// same thing is not drift — it is the same document, and the signature
    /// check decides whether the pair was signed.
    let private driftOf (recomputed: DemandedProjection) (signed: string) : EnvelopeDrift option =
        match Demanded.decode signed with
        | Ok carried when carried = recomputed -> None
        | Ok carried ->
            Some
                { Excess = projectionExcept recomputed carried
                  Shortfall = projectionExcept carried recomputed
                  Unreadable = None }
        | Error failure ->
            Some
                { Excess = recomputed
                  Shortfall = Demanded.empty
                  Unreadable = Some failure }

    /// Verify a signed pair by recomputation, under a public key.
    ///
    /// COLD: no host, no performer, no model — the tree, the walk, the key and
    /// the crypto are everything it reads. `project` must be the walk the
    /// signer used; a client-tier walk over a server-signed record reports the
    /// server tier as shortfall, which is the honest answer rather than a
    /// defect. `key` is optional only so that its absence can be REFUSED: there
    /// is no path through this function that checks the envelope and not the
    /// signature. Asynchronous because the crypto seam is — browser crypto is —
    /// and a synchronous host adapts trivially.
    let verify
        (crypto: IClaimSignatureVerifier)
        (key: KeyDirectoryEntry option)
        (project: Node<obj> -> DemandedProjection)
        (root: Node<obj>)
        (signed: SignedEnvelope)
        : Async<Result<VerifiedEnvelope, VerifyRefusal>> =
        async {
            match key with
            | None -> return Error VerifyRefusal.NoKey
            | Some key when key.KeyId <> signed.KeyId -> return Error(VerifyRefusal.ForeignKey(signed.KeyId, key.KeyId))
            | Some key ->
                let hash = treeHash root
                let projection = project root

                match driftOf projection signed.Envelope with
                | Some drift -> return Error(VerifyRefusal.EnvelopeDrift drift)
                | None ->
                    // Over the RECOMPUTED preimage — the carried tree hash and
                    // envelope bytes have no say in what is checked.
                    let head = preimage hash (Demanded.encode projection)
                    let! verified = crypto.VerifyClaim head signed.Signature key

                    if verified then
                        return
                            Ok
                                { TreeHash = hash
                                  Projection = projection
                                  KeyId = key.KeyId }
                    else
                        return Error(VerifyRefusal.BadSignature(signed.TreeHash, hash))
        }

    // ─── the record as a wire document ───────────────────────────────────────

    /// Encode the signed record as a self-describing JSON document: the four
    /// signed members exactly as the preimage renders them, then the key id and
    /// the signature beside them. Deterministic, and the demanded document rides
    /// inside as a string so its bytes survive verbatim.
    let encode (signed: SignedEnvelope) : string =
        "{"
        + signedMembers signed.TreeHash signed.Envelope
        + ",\"keyId\":"
        + Demanded.q signed.KeyId
        + ",\"signature\":"
        + Demanded.q signed.Signature
        + "}"

    let private declared =
        [ "kind"; "version"; "treeHash"; "envelope"; "keyId"; "signature" ]

    /// Read a signed record back. Total over its input, never partial, one
    /// version, every undeclared member refused — the discipline
    /// `Demanded.decode` sets, under its failure vocabulary, because this record
    /// is the same class of untrusted document.
    ///
    /// The demanded document inside is NOT decoded here: the wire is unchanged,
    /// and reading it is `Demanded.decode`'s job — `verify` does so, and reports
    /// an unreadable one as drift rather than refusing the record.
    let decode (json: string) : Result<SignedEnvelope, DemandedDecodeFailure> =
        Demanded.parseDocument json
        |> Result.bind (fun (root, erased) ->
            // `kind` and `version` are answered FIRST, exactly as the demanded
            // reader answers them: a document of another kind is told that,
            // rather than being refused for a member shape it was never
            // required to have.
            Demanded.requireString None "kind" "kind" root
            |> Result.bind (fun kind ->
                if kind <> Kind then
                    Demanded.failWith
                        DemandedDefect.UnknownKind
                        None
                        "kind"
                        ("this reader reads '" + Kind + "' documents; this one is '" + kind + "'")
                else
                    Demanded.requireInt None "version" "version" root)
            |> Result.bind (fun version ->
                if version <> Version then
                    Demanded.failWith
                        DemandedDefect.UnknownVersion
                        (Some version)
                        "version"
                        ("this reader reads version "
                         + string Version
                         + "; the document declares "
                         + string version)
                else
                    // The parser erases a member spelled `null` so the demanded
                    // document's absent tier can be read; this record declares
                    // no such member, so an erased one is a member of the wrong
                    // shape rather than a document this reader can read whole.
                    match erased with
                    | [] -> Ok()
                    | key :: _ ->
                        Demanded.failWith
                            DemandedDefect.WrongType
                            (Some Version)
                            key
                            ("member '" + key + "' is null; this document declares no nullable member"))
            |> Result.bind (fun () -> Demanded.declaredOnly (Some Version) "" declared root)
            |> Result.bind (fun () -> Demanded.requireString (Some Version) "treeHash" "treeHash" root)
            |> Result.bind (fun hash ->
                if ProgramWire.isContentAddress hash then
                    Ok hash
                else
                    Demanded.failWith
                        DemandedDefect.WrongType
                        (Some Version)
                        "treeHash"
                        ("member 'treeHash' is not a '"
                         + ProgramWire.ContentAddressPrefix
                         + "' content address"))
            |> Result.bind (fun hash ->
                Demanded.requireString (Some Version) "envelope" "envelope" root
                |> Result.bind (fun envelope ->
                    Demanded.requireString (Some Version) "keyId" "keyId" root
                    |> Result.bind (fun keyId ->
                        Demanded.requireString (Some Version) "signature" "signature" root
                        |> Result.map (fun signature ->
                            { TreeHash = hash
                              Envelope = envelope
                              KeyId = keyId
                              Signature = signature })))))
