/// The toy scenario family's SERVER-LOGIC placement (fuaran#2011): the generic
/// `ServerSession` loop at the toy witness, behind the toy transport.
///
/// Two readings of one corpus, as the UI family has. With NO handler set, this
/// placement must agree with the client placement on every bounded-loop
/// scenario — a call naming an endpoint no host registered reaches nothing.
/// With the handler set a scenario NAMES (§10.6's `hostHandlers`), it runs the
/// handler-loop scenarios, whose traces record what the UI family's never
/// could: the argument-policy gate refusing a plan, a staged perform failing
/// and the handler rolling back, and the op channel committing an edit to the
/// tree.
///
/// .NET-only: a server placement has no browser leg, so the Fable parity leg
/// compiles `ToyScenarios.fs` and not this.
module Fuaran.Program.Tests.ToyServerPlacement

open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.ToyDomain
open Fuaran.Program.Tests.ToyScenarios

type ToyServices = ServerServices<ToyNode, ToyAction, ToyExpr, ToyStore, ToyOp, ToyEffect>

/// The endpoints `toy-relabel` registers, as §10.6 tabulates them.
[<Literal>]
let RelabelEndpoint = "/handlers/relabel"

[<Literal>]
let OffListEndpoint = "/handlers/off-list"

[<Literal>]
let DeclinedEndpoint = "/handlers/declined"

/// Construct what the name `toy-relabel` denotes (§10.6). Every capability is
/// admitted by the gate; `ApplyOps` carries an argument policy admitting the
/// target `title` and nothing else; two host functions are registered, one
/// that answers and one that declines; and three handlers are registered under
/// the endpoints above.
let toyRelabel: ToyServices =
    let registry =
        ServerEffectRegistry.denyAll
        |> ServerEffectRegistry.register "audit" (fun _ -> Ok(JStr "recorded"))
        |> ServerEffectRegistry.register "decline" (fun _ -> Error "declined by the host")
        |> ServerEffectRegistry.permissive
        |> ServerEffectRegistry.constrain "ApplyOps" [ ServerConstraintClause.AllowList("target", [ "title" ]) ]

    let relabel: Handler<ToyAction, ToyOp> =
        { Name = "relabel"
          Stages =
            [ Compute(Seq [ Put("status", Some(JStr "relabelled"), None); Beep 2 ])
              Effect(ServerEffect.ApplyOps [ Relabel("title", "Relabelled") ])
              Effect(ServerEffect.HostCall("audit", JObj [ "note", JStr "relabel" ], None)) ] }

    let offList: Handler<ToyAction, ToyOp> =
        { Name = "off-list"
          Stages =
            [ Compute(Put("status", Some(JStr "attempted"), None))
              Effect(ServerEffect.ApplyOps [ Relabel("footer", "Moved") ]) ] }

    let declined: Handler<ToyAction, ToyOp> =
        { Name = "declined"
          Stages =
            [ Compute(Put("status", Some(JStr "staged"), None))
              Effect(ServerEffect.ApplyOps [ Relabel("title", "Never") ])
              Effect(ServerEffect.HostCall("decline", JObj [], None)) ] }

    { ServerServices.createPermissive witness with
        Effects = registry }
    |> ServerServices.withHandler RelabelEndpoint relabel
    |> ServerServices.withHandler OffListEndpoint offList
    |> ServerServices.withHandler DeclinedEndpoint declined

/// The services a scenario's declared handler set denotes. An unregistered
/// name FAILS rather than falling back to no handlers, which would report a
/// scenario this host could not evaluate as one it ran.
let servicesFor (hostHandlers: string option) : Result<ToyServices, string> =
    match hostHandlers with
    | None -> Ok(ServerServices.createPermissive witness)
    | Some name when name = ToyRelabel -> Ok toyRelabel
    | Some name -> Error(sprintf "hostHandlers '%s' is not a name this host can construct" name)

/// Drive a scenario through `ServerSession` with the given services. The gate
/// is the toy transport's (K8); everything after it — the budget, the fold, the
/// handler's plan, perform and commit, re-resolution — is the core's.
let run (services: ToyServices) (scenario: ToyScenario) : ToyStep list =
    let session = ServerSession.init services Map.empty scenario.Tree

    let initial =
        { Tree = ToyWire.renderNode session.Resolved
          Effects = []
          Refused = false }

    let steps, _ =
        scenario.Events
        |> List.mapFold
            (fun (session: ServerSession<_, _, _, _, _, _>, previous: ToyStep) event ->
                let unchanged refused =
                    { previous with
                        Effects = []
                        Refused = refused },
                    (session, previous)

                match dispatch session.BaseTree event with
                | NoSuchNode -> unchanged true
                | NoAction -> unchanged false
                | Run action ->
                    let next, (out: ServerStepOutput<ToyNode, ToyOp, ToyEffect, string>) =
                        ServerSession.dispatch session event.NodeId action

                    let step =
                        { Tree = ToyWire.renderNode out.Resolved
                          Effects = out.ClientEffects |> List.map ToyWire.encodeEffect
                          Refused = out.Rejected.IsSome }

                    step, (next, step))
            (session, initial)

    initial :: steps

/// Drive a scenario through this placement under the handler set it names.
let runNamed (scenario: ToyScenario) : Result<ToyStep list, string> =
    servicesFor scenario.HostHandlers
    |> Result.map (fun services -> run services scenario)
    |> Result.mapError (fun e -> scenario.Name + ": " + e)
