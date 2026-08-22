// SPDX-License-Identifier: Apache-2.0
//
// Emit a conformance corpus for the demanded-effect projection document.
//
//   dotnet fsi tools/emit-demanded-conformance.fsx <outputFile>
//
// ── Why this exists ──────────────────────────────────────────────────────────
//
// `Demanded.encode` writes the document and `Demanded.decode` is the pinned
// reader for it. A consumer in another repository — one that must not take a
// package dependency on this tier — cannot call either, and has historically
// written its own envelope reader instead. Such a reader cannot be wrong in a
// way anything downstream notices: it produces a projection that LOOKS like a
// projection, and whatever is computed from it is silently wrong.
//
// So the pin travels as DATA. Each vector below is a document paired with what
// THIS tier's own decoder read it as — the accepted ones with the server tier's
// contents, the refused ones with the defect class. A consumer's reader is
// conformant exactly when it produces the same answers.
//
// Two things it is honest about. The vectors are a snapshot: regenerating them
// is a deliberate act, recorded where they land. And the drift check is the
// document's VERSION — a change to the contract advances it, and a conformant
// consumer refuses a version it does not know — so producing-side drift can
// only ever cost a consumer a refusal, never a wrong read.
//
// Nothing here names any consumer. The corpus describes this document and is
// useful to anyone reading one.

#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Wire.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Tree.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Ops.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Validator.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Column.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.DataFrame.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Core.Function.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.Ops.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.Ops.Abstractions.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.Renderer.Core.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.OpStream.Abstractions.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.OpStream.Replay.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.UI.ServerDriven.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Program.Bounded.dll"
#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Program.Server.dll"

open Fuaran.UI
open Fuaran.UI.Types
open Fuaran.Program.Bounded
open Fuaran.Program.Server

let jstr (s: string) = Fuaran.Core.JStr s

let treeCalling (target: string) : Node<obj> =
    Fuaran.dashboard
        "root"
        { Defaults.dashboard<obj> with
            Children =
                [ Fuaran.button
                      "call"
                      { Defaults.button<obj> with
                          Label = TextSource.Literal "call"
                          OnClick = Action.Call(target, None, None) } ] }

let busy: Handler =
    { Name = "orders.settle"
      Stages =
        [ HandlerStage.Effect(ServerEffect.HostCall("sendMail", jstr "x", None))
          HandlerStage.Effect(ServerEffect.HostCall("charge", jstr "y", None))
          HandlerStage.Effect(ServerEffect.Notify("overdue", jstr "n"))
          HandlerStage.Effect(ServerEffect.ApplyOps []) ] }

let registration =
    Map.ofList [ "/handlers/settle", { busy with Name = "/handlers/settle" } ]

// ── the documents ────────────────────────────────────────────────────────────

/// A real harvest: the whole pipeline, so the richest vector in the corpus is a
/// document this tier actually produces rather than one hand-written to look
/// like one.
let harvested =
    (Harvest.ofProgram registration (treeCalling "/handlers/settle")).Document

let tierLess =
    (Harvest.ofProgram registration (treeCalling "/handlers/nobody")).Document

let emptyTier = (Harvest.ofRegistration []).Document

/// A document whose server tier names an arm no consumer's vocabulary contains.
/// Kept by the decoder, never dropped — a vocabulary that grew on this side must
/// make a program look MORE effectful to a consumer, not less.
let unknownArm =
    """{"kind":"demanded","version":3,"effects":[],"hostCalls":[],"stateNamespaces":[],"opaqueHandlers":[],"server":{"effects":["RunQuery","TeleportSomewhere"],"capabilities":[],"functions":[],"channels":[],"replay":[]}}"""

let canonical =
    """{"kind":"demanded","version":3,"effects":[],"hostCalls":[],"stateNamespaces":[],"opaqueHandlers":[],"server":{"effects":[],"capabilities":[],"functions":[],"channels":[],"replay":[]}}"""

let vectors: (string * string * string) list =
    [ "harvest-full",
      harvested,
      "A real harvest: a program, the handler registration behind it, and every reachable handler's replay posture."
      "tier-less",
      tierLess,
      "A program reaching no registered handler. The tier is present because a walk ran and demands nothing — a different fact from not having been asked."
      "empty-registration", emptyTier, "A registration with no handlers. A walk ran; there was nothing to find."
      "unknown-effect-arm",
      unknownArm,
      "A server tier naming an arm outside a consumer's vocabulary. Carried, never dropped: dropping it would make a newer program look safer than an older one."
      "unknown-version",
      canonical.Replace("\"version\":3", "\"version\":2"),
      "A version this reader does not read. Refused rather than read through another version's lens."
      "wrong-kind",
      canonical.Replace("\"demanded\"", "\"manifest\""),
      "A document of another kind. The kind is a claim about what the document IS."
      "undeclared-root-member",
      canonical.Replace("\"kind\":\"demanded\"", "\"kind\":\"demanded\",\"extra\":1"),
      "A root member this version does not declare. Refused rather than ignored: ignoring it is reading the document through the wrong lens with the version agreeing all the way."
      "undeclared-server-member",
      canonical.Replace("\"server\":{\"effects\"", "\"server\":{\"extra\":1,\"effects\""),
      "The same rule, one level down."
      "missing-server",
      canonical.Replace(
          ",\"server\":{\"effects\":[],\"capabilities\":[],\"functions\":[],\"channels\":[],\"replay\":[]}",
          ""
      ),
      "The tier omitted entirely. This version carries it on every document, null where no walk ran — so its absence is a document this version does not describe."
      "null-server",
      canonical.Replace(
          "\"server\":{\"effects\":[],\"capabilities\":[],\"functions\":[],\"channels\":[],\"replay\":[]}",
          "\"server\":null"
      ),
      "The tier spelled null: no walk was performed. A member spelled null IS an absent member."
      "missing-root-member",
      canonical.Replace(",\"opaqueHandlers\":[]", ""),
      "A required root member absent. Nothing is defaulted — a reader that supplied an empty list would report a demand set the document does not state."
      "wrong-member-type",
      canonical.Replace("\"effects\":[],\"hostCalls\"", "\"effects\":\"none\",\"hostCalls\""),
      "A member carrying the wrong JSON type. Distinct from an absent one: only this means the producer and the reader disagree about what the member IS."
      "non-canonical-effects",
      """{"kind":"demanded","version":3,"effects":["b","a"],"hostCalls":[],"stateNamespaces":[],"opaqueHandlers":[],"server":null}""",
      "A client-tier list that is not distinct and sorted. Two such documents would not compare by value, which is the property the encoding exists to provide."
      "non-canonical-server-functions",
      """{"kind":"demanded","version":3,"effects":[],"hostCalls":[],"stateNamespaces":[],"opaqueHandlers":[],"server":{"effects":[],"capabilities":[],"functions":[{"function":"b","capability":"host:b"},{"function":"a","capability":"host:a"}],"channels":[],"replay":[]}}""",
      "The same rule in the server tier."
      "not-an-object", "[]", "A document whose root is not an object."
      "not-json", "{", "Bytes that are not readable JSON." ]

// ── what the pinned reader makes of each ─────────────────────────────────────

let esc (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

let q (s: string) = "\"" + esc s + "\""
let arr (xs: string list) = "[" + String.concat "," xs + "]"

/// The tier as the corpus carries it.
///
/// A walk that did NOT run is `"serverWalked":false` with no `server` member, rather than
/// `"server":null`. The corpus is read by consumers whose JSON model has no null — the wire this
/// whole family lives on has none either — so emitting one would make the corpus unreadable to
/// exactly the readers it exists to certify. The two facts stay distinguishable because the boolean
/// is always present.
let renderTier (tier: ServerDemand option) =
    match tier with
    | None -> "\"serverWalked\":false"
    | Some t ->
        let fns =
            t.Functions
            |> List.map (fun f -> "{\"function\":" + q f.Function + ",\"capability\":" + q f.Capability + "}")
            |> arr

        let chans =
            t.Channels
            |> List.map (fun c -> "{\"channel\":" + q c.Channel + ",\"name\":" + q c.Name + "}")
            |> arr

        let replay =
            t.Replay
            |> List.map (fun p -> "{\"handler\":" + q p.Handler + ",\"safety\":" + q p.Safety + "}")
            |> arr

        "\"serverWalked\":true,\"server\":{\"effects\":"
        + arr (t.Effects |> List.map q)
        + ",\"capabilities\":"
        + arr (t.Capabilities |> List.map q)
        + ",\"functions\":"
        + fns
        + ",\"channels\":"
        + chans
        + ",\"replay\":"
        + replay
        + "}"

let entries =
    vectors
    |> List.map (fun (id, document, description) ->
        let verdict =
            match Demanded.decode document with
            | Ok projection -> "{\"verdict\":\"ok\"," + renderTier projection.Server + "}"
            | Error failure ->
                "{\"verdict\":\"refused\",\"defect\":"
                + q (string failure.Defect)
                + ",\"field\":"
                + q failure.Field
                + "}"

        "{\"id\":"
        + q id
        + ",\"description\":"
        + q description
        + ",\"document\":"
        + q document
        + ",\"read\":"
        + verdict
        + "}")

let out =
    "{\n  \"corpus\": \"demanded-effect-projection\",\n  \"documentKind\": \""
    + Demanded.Kind
    + "\",\n  \"decodableVersions\": "
    + arr (Demanded.decodableVersions |> List.map string)
    + ",\n  \"generator\": \"tools/emit-demanded-conformance.fsx\",\n  \"vectors\": [\n    "
    + String.concat ",\n    " entries
    + "\n  ]\n}\n"

let target =
    match fsi.CommandLineArgs |> Array.toList with
    | _ :: path :: _ -> path
    | _ -> failwith "usage: dotnet fsi tools/emit-demanded-conformance.fsx <outputFile>"

System.IO.File.WriteAllText(target, out)
printfn "wrote %d vectors to %s" (List.length entries) target
