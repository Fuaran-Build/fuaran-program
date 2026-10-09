/// A performed write's receipt is its RETURN VALUE, typed (Phase 2197, D43),
/// pinned at the toy witness:
///
///   * **the receipt**: an op performer returns `OpReceipt` — the writes it
///     performed, each a target and a SHA-256 digest of the exact bytes
///     written — so a program performing two writes yields exactly two write
///     receipts naming both targets and both digests;
///   * **the union**: what an invocation changed is derived from its receipts
///     alone (`HandlerOutcome.changed`), and a commit stage accounts what it
///     would stage against them, refusing a write no receipt names, or one
///     whose content is not what the invocation wrote, as `foreign-write`;
///   * **the journal**: the durable tier journals each receipt, encoded, as
///     its stage's completed value, and a resumed run is SERVED the dead run's
///     receipts — the same writes, the same digests — without re-performing;
///     a journal value that is not an encoded receipt is refused, never read
///     as a receipt with no writes.
module Fuaran.Program.Tests.ReceiptTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain

// ─── fixtures ────────────────────────────────────────────────────────────────

exception private ProcessDied of string

let private crashing (f: unit -> 'T) : bool =
    try
        f () |> ignore
        false
    with ProcessDied _ ->
        true

let private leaf id label =
    { Id = id
      Label = Const(JStr label)
      Handlers = []
      Children = [] }

let private store: ServerStore<ToyNode, ToyStore> =
    { Tree =
        { Id = "root"
          Label = Const(JStr "root")
          Handlers = []
          Children = [ leaf "title" "Draft"; leaf "footer" "Plain"; leaf "commit" "-" ] }
      Bindings = Map.empty }

let private registry (audits: int ref) =
    ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.register "audit" (fun _ ->
        audits.Value <- audits.Value + 1
        Ok(JStr "audited"))
    |> ServerEffectRegistry.permissive

/// Two writes, then a host call — the stage the durable tests die in front of.
let private twoWrites: Handler<ToyAction, ToyOp> =
    { Name = "two-writes"
      Stages =
        [ Effect(ServerEffect.ApplyOps [ Relabel("title", "One"); Relabel("footer", "Two") ])
          Effect(ServerEffect.HostCall("audit", JStr "after", None)) ] }

/// The performer: each relabel WRITES its label at its node's id and returns
/// the receipt of that write — the receipt IS the return value.
let private writing (performed: int ref) : OpPerformance<ToyNode, ToyOp> =
    OpPerformance.performedBy (fun _ op ->
        performed.Value <- performed.Value + 1

        match op with
        | Relabel(id, label) -> Ok(OpReceipt.ofWrites [ WriteReceipt.ofText id label ]))

let private expectedReceipts =
    [ OpReceipt.ofWrites [ WriteReceipt.ofText "title" "One" ]
      OpReceipt.ofWrites [ WriteReceipt.ofText "footer" "Two" ] ]

let private freshJournal () =
    Journal.declaringDurable (Journal.inMemory ())

let private dyingBeforeAttempting (step: int) (inner: EffectJournal) : EffectJournal =
    { inner with
        Append =
            fun entry ->
                match entry.Phase with
                | JournalPhase.Attempted when entry.Step = step -> raise (ProcessDied "between steps")
                | _ -> inner.Append entry }

let private durably journal audits performance =
    Durable.runWith
        witness
        (DurableServices.create |> DurableServices.withJournal journal)
        "inv"
        (registry audits)
        performance
        Fuaran.Compute.DataFrame.noResolve
        "node"
        twoWrites
        store

// ─── the digest ──────────────────────────────────────────────────────────────

let private digest =
    testList
        "the content digest"
        [ test "is sha256 over the UTF-8 bytes, the estate's one rendering" {
              let text = "Résumé\n"

              Expect.equal
                  (ContentDigest.ofText text).Text
                  ("sha256:" + Hash.sha256Hex text)
                  "the content address's digest"

              Expect.equal
                  (ContentDigest.ofText text)
                  (ContentDigest.ofBytes (System.Text.Encoding.UTF8.GetBytes text))
                  "text is its UTF-8 bytes, no byte-order mark"

              Expect.notEqual
                  (ContentDigest.ofText "a\n")
                  (ContentDigest.ofText "a\r\n")
                  "no newline normalisation: the bytes, not a reading of them"
          }

          test "parses only its own rendering" {
              let good = (ContentDigest.ofText "x").Text
              Expect.equal (ContentDigest.parse good) (Ok(ContentDigest.ofText "x")) "round-trips"
              Expect.isError (ContentDigest.parse (good.ToUpperInvariant())) "uppercase is refused"
              Expect.isError (ContentDigest.parse (good.Substring(0, good.Length - 1))) "an abbreviation is refused"
              Expect.isError (ContentDigest.parse ("sha1:" + good.Substring 7)) "another algorithm is refused"
          }

          test "a receipt round-trips through its journaled form, and a 0.8.0 receipt does not decode" {
              let receipt =
                  { Writes = [ WriteReceipt.ofText "a" "1"; WriteReceipt.ofBytes "b" [| 0uy; 255uy |] ]
                    Detail = JObj [ "sha", JStr "abc" ] }

              Expect.equal (OpReceipt.decode (OpReceipt.encode receipt)) (Ok receipt) "round-trips, writes in order"

              match OpReceipt.decode (JStr "receipt") with
              | Error reason ->
                  Expect.stringStarts
                      reason
                      OpReceipt.MalformedCode
                      "a bare detail is malformed, not a write-less receipt"
              | Ok _ -> failtest "a bare value decoded"

              Expect.isError
                  (OpReceipt.decode (JObj [ "writes", JArr [ JObj [ "target", JStr "a" ] ]; "detail", JObj [] ]))
                  "a write with no digest is refused"
          } ]

// ─── the receipts and the union ──────────────────────────────────────────────

let private union =
    testList
        "an invocation's changed set is the union of its receipts"
        [ test "a program performing two writes yields exactly two receipts naming both targets and digests" {
              let performed = ref 0

              let outcome =
                  Handler.runWith
                      witness
                      (registry (ref 0))
                      (writing performed)
                      Fuaran.Compute.DataFrame.noResolve
                      "node"
                      twoWrites
                      store

              Expect.isTrue outcome.Committed "committed"
              Expect.equal outcome.Receipts expectedReceipts "exactly the two receipts, in perform order"

              Expect.equal
                  (Receipts.writes outcome.Receipts |> List.map (fun w -> w.Target, w.Digest.Text))
                  [ "title", (ContentDigest.ofText "One").Text
                    "footer", (ContentDigest.ofText "Two").Text ]
                  "each names its target and the digest of what it wrote"

              Expect.equal
                  (HandlerOutcome.changed outcome)
                  (Set.ofList [ "title"; "footer" ])
                  "the union names exactly those"
          }

          test "a write no receipt names is refused, and so is one whose content is not what was written" {
              let outcome =
                  Handler.runWith
                      witness
                      (registry (ref 0))
                      (writing (ref 0))
                      Fuaran.Compute.DataFrame.noResolve
                      "node"
                      twoWrites
                      store

              Expect.equal
                  (HandlerOutcome.account
                      outcome
                      [ WriteReceipt.ofText "title" "One"; WriteReceipt.ofText "footer" "Two" ])
                  (Ok())
                  "its own writes are accounted for"

              Expect.equal
                  (HandlerOutcome.account
                      outcome
                      [ WriteReceipt.ofText "title" "One"
                        WriteReceipt.ofText "stray" "x"
                        WriteReceipt.ofText "footer" "Tampered" ])
                  (Error
                      [ { Target = "stray"
                          Digest = ContentDigest.ofText "x"
                          Cause = ForeignCause.Unreceipted }
                        { Target = "footer"
                          Digest = ContentDigest.ofText "Tampered"
                          Cause = ForeignCause.DigestDiffers(ContentDigest.ofText "Two") } ])
                  "every foreign write, in the order staged"

              Expect.equal
                  (Receipts.final
                      [ OpReceipt.ofWrites [ WriteReceipt.ofText "t" "1" ]
                        OpReceipt.ofWrites [ WriteReceipt.ofText "t" "2" ] ]
                   |> Map.toList)
                  [ "t", ContentDigest.ofText "2" ]
                  "a target written twice holds the LAST content"
          }

          test "a commit stage reads the receipts before it from its prefix and refuses a foreign write" {
              let committing (staged: WriteReceipt list) =
                  OpPerformance.performedWithPrefix (fun prefix _ op ->
                      match op with
                      | Relabel("commit", _) ->
                          match Receipts.accountAll prefix.Receipts staged with
                          | Ok() -> Ok(OpReceipt.ofDetail (JStr "committed"))
                          | Error foreign -> Error(ForeignWrite.refusal foreign)
                      | Relabel(id, label) -> Ok(OpReceipt.ofWrites [ WriteReceipt.ofText id label ]))

              let handler =
                  { Name = "write-then-commit"
                    Stages =
                      [ Effect(
                            ServerEffect.ApplyOps
                                [ Relabel("title", "One"); Relabel("footer", "Two"); Relabel("commit", "now") ]
                        ) ] }

              let run staged =
                  Handler.runWith
                      witness
                      (registry (ref 0))
                      (committing staged)
                      Fuaran.Compute.DataFrame.noResolve
                      "node"
                      handler
                      store

              let honest =
                  run [ WriteReceipt.ofText "footer" "Two"; WriteReceipt.ofText "title" "One" ]

              Expect.isTrue honest.Committed "a commit of exactly its own writes commits"

              let foreign =
                  run [ WriteReceipt.ofText "title" "One"; WriteReceipt.ofText "elsewhere" "x" ]

              Expect.isFalse foreign.Committed "a commit staging a foreign write is refused"

              Expect.equal
                  (List.tryLast foreign.Diagnostics)
                  (Some(ServerDiagnostic.PerformFailed("ApplyOps", "foreign-write: elsewhere (unreceipted)")))
                  "naming the target and the cause, never the content"

              Expect.equal
                  foreign.Receipts
                  expectedReceipts
                  "the writes that performed before the refusal are still reported"
          } ]

// ─── the journal ─────────────────────────────────────────────────────────────

let private journal =
    testList
        "receipts are journaled with their stage and served on resume"
        [ test "a resumed run is served the dead run's receipts without re-performing" {
              let journal = freshJournal ()
              let performed = ref 0
              let audits = ref 0

              let died =
                  crashing (fun () -> durably (dyingBeforeAttempting 2 journal) audits (writing performed))

              Expect.isTrue died "died in front of the host call, after both writes"
              Expect.equal performed.Value 2 "both writes performed"

              let recorded =
                  journal.Read "inv"
                  |> List.choose (fun entry ->
                      match entry.Capability, entry.Phase with
                      | "ApplyOps", JournalPhase.Completed value -> Some(entry.Step, OpReceipt.decode value)
                      | _ -> None)

              Expect.equal
                  recorded
                  [ 0, Ok expectedReceipts.[0]; 1, Ok expectedReceipts.[1] ]
                  "each receipt is journaled, typed, at its stage's ordinal"

              let resumed = durably journal audits (writing performed)

              Expect.isTrue resumed.Outcome.Committed "committed"
              Expect.equal resumed.Replayed [ 0; 1 ] "both op stages were served"
              Expect.equal performed.Value 2 "neither write was performed again"
              Expect.equal audits.Value 1 "the host call ran once"
              Expect.equal resumed.Outcome.Receipts expectedReceipts "the served receipts ARE the run's receipts"

              Expect.equal
                  (HandlerOutcome.changed resumed.Outcome)
                  (Set.ofList [ "title"; "footer" ])
                  "the union on a resume is the union of a fresh run"
          }

          test "a journaled op stage that is not an encoded receipt is refused on resume, never read as no writes" {
              let journal = freshJournal ()
              let subject = Durable.opSubject witness.State (Relabel("title", "One"))

              for phase in [ JournalPhase.Attempted; JournalPhase.Completed(JStr "a 0.8.0 receipt") ] do
                  journal.Append
                      { Invocation = "inv"
                        Step = 0
                        Capability = "ApplyOps"
                        Subject = Some subject
                        Phase = phase }

              let performed = ref 0
              let resumed = durably journal (ref 0) (writing performed)

              Expect.isFalse resumed.Outcome.Committed "refused"
              Expect.equal performed.Value 0 "the served stage was not re-performed to paper over it"

              match List.tryLast resumed.Outcome.Diagnostics with
              | Some(ServerDiagnostic.PerformFailed("ApplyOps", reason)) ->
                  Expect.stringStarts reason OpReceipt.MalformedCode "named as a malformed receipt"
              | other -> failtestf "expected a PerformFailed naming the malformed receipt, got %A" other
          } ]

[<Tests>]
let receiptTests =
    testList "Phase 2197 - a performed write's receipt is its return value" [ digest; union; journal ]
