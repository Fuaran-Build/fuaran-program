/// A plan's read of the world is TYPED (Phase 2175, D41), pinned at the toy
/// witness under the durable tier:
///
///   * **a typed unavailable read**: a read that could not answer reaches the
///     plan as `Read.Unavailable` naming the read and why — never a default
///     standing in for a read that did not happen;
///   * **through the store's own codec**: a record the codec accepts and a
///     generic reader refuses (a `null` member, which the wire value model
///     cannot represent) is READ; a record the codec refuses is unavailable,
///     naming the codec and its own reason;
///   * **degraded reads before the first op**: the plan's step that precedes
///     its first performed op enumerates the run's unavailable reads and
///     refuses on them, so nothing is performed; the unavailability is
///     journaled like any answer, so a resume meets it again;
///   * **content-keyed memoisation**: a run decodes a content once, keyed by a
///     digest of what it read, and a same-length rewrite inside one tick — which
///     a (length, write-time) key reads as unchanged — decodes the NEW content;
///     a resume decodes the journaled content, never the moved world's.
module Fuaran.Program.Tests.TypedReadTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Runtime
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

let private emptyStore: ServerStore<ToyNode, ToyStore> =
    { Tree =
        { Id = "root"
          Label = Const(JStr "root")
          Handlers = []
          Children = [ leaf "title" "Draft"; leaf "footer" "Plain" ] }
      Bindings = Map.empty }

let private labelOf (id: string) (root: ToyNode) : string option =
    let rec go (n: ToyNode) =
        if n.Id = id then
            match n.Label with
            | Const(JStr label) -> Some label
            | _ -> None
        else
            n.Children |> List.tryPick go

    go root

let private relabel (id: string) (label: string) (root: ToyNode) : ToyNode =
    let rec go (n: ToyNode) =
        if n.Id = id then
            { n with Label = Const(JStr label) }
        else
            { n with
                Children = n.Children |> List.map go }

    go root

let private registryOf (performers: (string * (JVal -> Result<JVal, string>)) list) =
    performers
    |> List.fold (fun r (fn, p) -> ServerEffectRegistry.register fn p r) ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.permissive

let private counting (count: int ref) =
    fun (_: JVal) ->
        count.Value <- count.Value + 1
        Ok(JStr "recorded")

let private freshJournal () =
    Journal.declaringDurable (Journal.inMemory ())

let private dyingBeforeAttempting (step: int) (inner: EffectJournal) : EffectJournal =
    { inner with
        Append =
            fun entry ->
                match entry.Phase with
                | JournalPhase.Attempted when entry.Step = step -> raise (ProcessDied "between steps")
                | _ -> inner.Append entry }

/// A store of text records with a COARSE clock: `Tick` advances it, a rewrite
/// does not — so a same-length rewrite inside one tick leaves the metadata a
/// stat-keyed memo would read, (length, write-time), exactly as it was.
type private Store() =
    let records = System.Collections.Generic.Dictionary<string, string * int>()
    let mutable clock = 0
    let mutable reachable = true
    member _.Tick() = clock <- clock + 1
    member _.Write(key: string, content: string) = records[key] <- (content, clock)
    member _.Unreachable() = reachable <- false
    member _.Reachable() = reachable <- true

    /// The metadata a (path, length, write-time) memo key would read.
    member _.Metadata(key: string) : int * int =
        let content, written = records[key]
        content.Length, written

    /// The raw read: the content, or the host's reason there is none.
    member _.Read(key: string) : Result<string, string> =
        if not reachable then
            Error "the store is unreachable"
        else
            match records.TryGetValue key with
            | true, (content, _) -> Ok content
            | _ -> Error("no record " + key)

/// The STORE's own codec for a record: it knows a record's `note` may be
/// null, so it reads the record null-tolerantly, and it requires the node.
let private recordCodec: ReadCodec<string> =
    { Name = "record-v1"
      Decode =
        fun text ->
            match Json.parseTolerantOfNull text with
            | Error reason -> Error("not a record: " + string reason)
            | Ok(JObj members) ->
                match List.tryFind (fun (name, _) -> name = "node") members with
                | Some(_, JStr node) -> Ok node
                | Some _ -> Error "node is not a string"
                | None -> Error "a record names its node"
            | Ok _ -> Error "a record is an object" }

/// A GENERIC reader of the same record, with the guard the finding describes:
/// a load error read as "no finding".
let private genericReader (text: string) : string option =
    match Json.parse text with
    | Ok(JObj members) ->
        match List.tryFind (fun (name, _) -> name = "node") members with
        | Some(_, JStr node) -> Some node
        | _ -> None
    | _ -> None

let private recordOne = """{"node":"n-1","note":null}"""
let private recordTwo = """{"node":"n-2","note":null}"""

let private readMarker = "@read:"
let private commitMarker = "@commit"

/// The toy witness whose `Apply` reads through the run's `Reads`:
/// `Relabel(id, "@read:<key>")` relabels `id` with the record's node, and
/// on an unavailable read DEGRADES — leaves the tree as it is and carries on —
/// so the refusal is the commit step's to make; `Relabel(id, "@commit")` is
/// that step, the plan's last before anything is performed, and refuses on the
/// run's unavailable reads.
let private readingWitness (store: Store) (seen: ResizeArray<Read<string>>) (reads: Reads) =
    { witness with
        State =
            { witness.State with
                Stream =
                    { witness.State.Stream with
                        Apply =
                            fun op tree ->
                                match op with
                                | Relabel(id, label) when label = commitMarker ->
                                    match reads.Refusal() with
                                    | Some refusal -> Error refusal
                                    | None -> Ok(relabel id "Committed" tree)
                                | Relabel(id, label) when label.StartsWith readMarker ->
                                    let key = label.Substring readMarker.Length

                                    match reads.Read(recordCodec, key, (fun () -> store.Read key)) with
                                    | Error tier -> Error tier
                                    | Ok read ->
                                        seen.Add read

                                        match read with
                                        | Read.Available node -> Ok(relabel id node tree)
                                        | Read.Unavailable _ -> Ok tree
                                | Relabel(id, label) -> Ok(relabel id label tree) } } }

/// Build the witness over this run's reader, keeping the run's `Reads` so a
/// test can read its ledger and its decode count afterwards.
let private over (store: Store) (seen: ResizeArray<Read<string>>) (last: Reads option ref) =
    fun (reader: EntryReader) ->
        let reads = Reads.Over reader
        last.Value <- Some reads
        readingWitness store seen reads

/// Read the record, commit on what was read, then audit.
let private readThenCommitThenAudit: Handler<ToyAction, ToyOp> =
    { Name = "commit"
      Stages =
        [ Effect(ServerEffect.ApplyOps [ Relabel("title", readMarker + "bundle") ])
          Effect(ServerEffect.ApplyOps [ Relabel("footer", commitMarker) ])
          Effect(ServerEffect.HostCall("audit", JStr "committed", None)) ] }

/// The op performer: counts, and runs `sideEffect` per op it performs.
let private performing (performed: int ref) (sideEffect: unit -> unit) : OpPerformance<ToyNode, ToyOp> =
    OpPerformance.performedBy (fun _ _ ->
        performed.Value <- performed.Value + 1
        sideEffect ()
        Ok(JObj []))

let private run witnessOf journal invocation audits performance =
    Durable.runReading
        witnessOf
        (DurableServices.create |> DurableServices.withJournal journal)
        invocation
        (registryOf [ "audit", counting audits ])
        performance
        Fuaran.Compute.DataFrame.noResolve
        "node"
        readThenCommitThenAudit
        emptyStore

let private stepZero (journal: EffectJournal) =
    journal.Read "inv" |> List.filter (fun e -> e.Step = 0) |> List.map _.Phase

// ─── through the store's own codec ───────────────────────────────────────────

let private codec =
    testList
        "a read decodes through the store's own codec"
        [ test "a record the codec accepts and a generic reader drops is READ" {
              Expect.isNone
                  (genericReader recordOne)
                  "premise: the generic reader refuses the null member and its guard reads 'no finding'"

              Expect.isError (Json.parse recordOne) "premise: the wire value model cannot represent the null"

              let reads = Reads.Over EntryReader.live

              Expect.equal
                  (reads.Read(recordCodec, "entry", (fun () -> Ok recordOne)))
                  (Ok(Read.Available "n-1"))
                  "the codec's decode reaches the plan"

              Expect.isEmpty reads.Degraded "nothing was unavailable"
              Expect.isNone (reads.Refusal()) "and nothing refuses"
          }

          test "a record the codec refuses is Unavailable, naming the codec and its own reason" {
              let reads = Reads.Over EntryReader.live

              let unavailable =
                  { Subject = "entry"
                    Cause = ReadCause.Undecodable("record-v1", "node is not a string") }

              Expect.equal
                  (reads.Read(recordCodec, "entry", (fun () -> Ok """{"node":42}""")))
                  (Ok(Read.Unavailable unavailable))
                  "never 'parsed some JSON and found nothing'"

              Expect.equal reads.Degraded [ unavailable ] "the ledger holds it"

              Expect.equal
                  (reads.Refusal())
                  (Some "read-unavailable: entry (codec record-v1: node is not a string)")
                  "the refusal names the read, the codec and the codec's reason"
          }

          test "a store that could not be read is Unavailable with the host's reason, distinct from a codec refusal" {
              let reads = Reads.Over EntryReader.live

              Expect.equal
                  (reads.Read(recordCodec, "entry", (fun () -> Error "the store is unreachable")))
                  (Ok(
                      Read.Unavailable
                          { Subject = "entry"
                            Cause = ReadCause.NotRead "the store is unreachable" }
                  ))
                  "the cause says the store was not reached"

              Expect.equal (reads.Decodes) 0 "no codec ran over a content that was never read"
          } ]

// ─── degraded reads before the first op ──────────────────────────────────────

let private degraded =
    testList
        "an unavailable read surfaces to the plan, and the plan refuses before its first performed op"
        [ test "the plan meets Unavailable as a value, naming the read and why" {
              let store = Store()
              store.Write("bundle", recordOne)
              store.Unreachable()
              let seen = ResizeArray()

              run (over store seen (ref None)) (freshJournal ()) "inv" (ref 0) (performing (ref 0) ignore)
              |> ignore

              Expect.equal
                  (List.ofSeq seen)
                  [ Read.Unavailable
                        { Subject = "bundle"
                          Cause = ReadCause.NotRead "the store is unreachable" } ]
                  "the plan's read answered Unavailable — no default stood in for it"
          }

          test
              "the commit step refuses on the run's unavailable reads: nothing is performed, and the refusal names the read" {
              let store = Store()
              store.Write("bundle", recordOne)
              store.Unreachable()
              let journal = freshJournal ()
              let audits = ref 0
              let performed = ref 0

              let outcome =
                  run (over store (ResizeArray()) (ref None)) journal "inv" audits (performing performed ignore)

              Expect.isFalse outcome.Outcome.Committed "the run did not commit"

              Expect.equal
                  outcome.Outcome.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "read-unavailable: bundle (not-read: the store is unreachable)") ]
                  "the plan's own refusal, naming the read it could not make"

              Expect.equal performed.Value 0 "no op was performed"
              Expect.equal audits.Value 0 "the host call never ran"
              Expect.equal outcome.Invoked [ 0 ] "the read was the only step that reached outside"

              Expect.equal
                  (stepZero journal)
                  [ JournalPhase.Attempted
                    JournalPhase.Completed(JObj [ ReadRecord.UnavailableMember, JStr "the store is unreachable" ]) ]
                  "the unavailability is journaled like any answer — completed, not refused"
          }

          test
              "a resume serves the journaled unavailability: the store came back and the plan refuses as the dead run did" {
              let store = Store()
              store.Write("bundle", recordOne)
              store.Unreachable()
              let journal = freshJournal ()

              run (over store (ResizeArray()) (ref None)) journal "inv" (ref 0) (performing (ref 0) ignore)
              |> ignore

              store.Reachable()
              let performed = ref 0
              let seen = ResizeArray()

              let resumed =
                  run (over store seen (ref None)) journal "inv" (ref 0) (performing performed ignore)

              Expect.isFalse resumed.Outcome.Committed "the replay refuses as the recorded run did"
              Expect.equal resumed.Replayed [ 0 ] "the read was served"
              Expect.isEmpty resumed.Invoked "the live store was not consulted"
              Expect.equal performed.Value 0 "nothing was performed"

              Expect.equal
                  (List.ofSeq seen)
                  [ Read.Unavailable
                        { Subject = "bundle"
                          Cause = ReadCause.NotRead "the store is unreachable" } ]
                  "the plan met what the dead run met"
          }

          test "with every read available the commit step commits and the stages run" {
              let store = Store()
              store.Write("bundle", recordOne)
              let audits = ref 0
              let performed = ref 0

              let outcome =
                  run
                      (over store (ResizeArray()) (ref None))
                      (freshJournal ())
                      "inv"
                      audits
                      (performing performed ignore)

              Expect.isTrue outcome.Outcome.Committed "the run committed"
              Expect.equal (labelOf "title" outcome.Outcome.Store.Tree) (Some "n-1") "the plan read the record"
              Expect.equal (labelOf "footer" outcome.Outcome.Store.Tree) (Some "Committed") "and committed"
              Expect.equal performed.Value 2 "both ops were performed"
              Expect.equal audits.Value 1 "the host call ran"
          } ]

// ─── content-keyed memoisation ───────────────────────────────────────────────

let private memo =
    testList
        "a read's decode is memoised by the content it read"
        [ test
              "falsifier: a same-length rewrite inside one tick moves the content and not the metadata; the read decodes the NEW content" {
              let store = Store()
              store.Write("bundle", recordOne)
              let before = store.Metadata "bundle"
              let reads = Reads.Over EntryReader.live

              let read () =
                  reads.Read(recordCodec, "bundle", (fun () -> store.Read "bundle"))

              let first = read ()
              store.Write("bundle", recordTwo)

              Expect.equal
                  (store.Metadata "bundle")
                  before
                  "premise: (length, write-time) is unchanged — a metadata key would serve the first decode"

              let second = read ()
              store.Write("bundle", recordOne)
              let third = read ()

              Expect.equal first (Ok(Read.Available "n-1")) "the first content"
              Expect.equal second (Ok(Read.Available "n-2")) "the rewritten content, not a memo of the first"
              Expect.equal third (Ok(Read.Available "n-1")) "the first content again"
              Expect.equal reads.Decodes 2 "two distinct contents, two decodes: the third read decoded nothing"
          }

          test "a resume decodes the JOURNALED content, never the moved world's or a memo of it" {
              let store = Store()
              store.Write("bundle", recordOne)
              let journal = freshJournal ()
              let performed = ref 0

              // The first op performed rewrites the record in place — same length,
              // same tick — and the process dies before the host call.
              let died =
                  crashing (fun () ->
                      run
                          (over store (ResizeArray()) (ref None))
                          (dyingBeforeAttempting 3 journal)
                          "inv"
                          (ref 0)
                          (performing performed (fun () -> store.Write("bundle", recordTwo))))

              Expect.isTrue died "the fixture's process died at the host call's attempt"
              Expect.equal performed.Value 2 "both ops performed before the death"

              let audits = ref 0
              let seen = ResizeArray()
              let last = ref None

              let resumed =
                  run (over store seen last) journal "inv" audits (performing (ref 0) ignore)

              Expect.isTrue resumed.Outcome.Committed "the resumed run committed"
              Expect.equal resumed.Replayed [ 0; 1; 2 ] "the read and both op stages were served"
              Expect.equal resumed.Invoked [ 3 ] "only the host call reached the world"
              Expect.equal audits.Value 1 "exactly once"
              Expect.equal (List.ofSeq seen) [ Read.Available "n-1" ] "the plan decoded what the dead run READ"

              Expect.equal (labelOf "title" resumed.Outcome.Store.Tree) (Some "n-1") "not the moved world's n-2"

              Expect.equal
                  (last.Value |> Option.map _.Decodes)
                  (Some 1)
                  "the resumed run decoded the journaled content once"
          } ]

[<Tests>]
let tests =
    testList
        "Phase 2175 - a read is typed Unavailable, decoded by the store's codec, memoised by content, and visible to the commit step"
        [ codec; degraded; memo ]
