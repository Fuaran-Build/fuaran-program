module Fuaran.Program.Tests.AtMostTests

// ─── R2: a ceiling clause bounds a number (Phase 1982) ───────────────
//
// "At most 90 polls" used to be an allow-list of the literals `0`..`90`.
// `ServerConstraintClause.AtMost(argument, limit)` says it directly: every
// value under the argument must read as an integer, in its canonical decimal
// spelling, no greater than the limit; anything else is refused under the
// allow-list's token, naming the argument and never the value.

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded
open Fuaran.Program.Server
open Fuaran.Program.Tests.VerbDomain

let private bounded (clauses: ServerConstraintClause list) (capability: string) : ServerEffectRegistry =
    ServerEffectRegistry.permissive ServerEffectRegistry.denyAll
    |> ServerEffectRegistry.constrain capability clauses

let private polling (count: int) : Handler<Nothing, FileOp> =
    { Name = "poll"
      Stages = [ Effect(ServerEffect.ApplyOps [ Times(count, [ Publish "origin" ]) ]) ] }

let private runIn (registry: ServerEffectRegistry) (world: World) (handler: Handler<Nothing, FileOp>) =
    Handler.runWith
        witness
        registry
        (OpPerformance.performedBy (world.Performer None))
        DataFrame.noResolve
        "verb"
        handler
        { Tree = { Files = world.Files; Published = [] }
          Bindings = () }

let private checkArgs (registry: ServerEffectRegistry) (args: (string * JVal) list) =
    ServerArgumentPolicy.check witness.State registry (ServerEffect.HostCall("fetch", JObj args, None))

[<Tests>]
let tests =
    let ninety = bounded [ ServerConstraintClause.AtMost("count", 90) ] "ApplyOps"

    testList
        "a ceiling clause on a number (Phase 1982, R2)"
        [ test "AtMost(count, 90) admits a repeat of 90 and refuses 91 before anything performs" {
              let world = World()
              let admitted = runIn ninety world (polling 90)
              Expect.isTrue admitted.Committed "90 is at the limit"
              Expect.equal world.Published.Length 90 "and all ninety ran"

              let world' = World()
              let refused = runIn ninety world' (polling 91)
              Expect.isFalse refused.Committed "91 is over it"
              Expect.isEmpty world'.Invocations "the performer was never asked"

              Expect.equal
                  refused.Diagnostics
                  [ ServerDiagnostic.Failed("ApplyOps", "argument-not-allowed:count") ]
                  "refused while planning, naming the argument and never the value"
          }

          test "a host call's integer argument is read as a number, and a string one only in its canonical spelling" {
              let five = bounded [ ServerConstraintClause.AtMost("n", 5) ] "host:fetch"

              for args, expected in
                  [ [ "n", JInt 5 ], true
                    [ "n", JInt 6 ], false
                    [ "n", JInt -3 ], true
                    [ "n", JStr "5" ], true
                    [ "n", JStr "-2" ], true
                    [ "n", JStr "05" ], false
                    [ "n", JStr "5.0" ], false
                    [ "n", JStr " 5" ], false
                    [ "n", JStr "-0" ], false
                    [ "n", JStr "+5" ], false
                    [ "n", JStr "five" ], false
                    [ "n", JStr "9999999999999999999" ], false
                    [ "other", JInt 100 ], true
                    [], true ] do
                  let verdict = checkArgs five args
                  Expect.equal (Result.isOk verdict) expected $"%A{args}"

                  if not expected then
                      Expect.equal verdict (Error(ServerConstraintDefect.OffList "n")) "the allow-list's token"
          }

          test "an integer member now binds an allow-list on its name, where before it passed vacuously" {
              let one = bounded [ ServerConstraintClause.AllowList("n", [ "1" ]) ] "host:fetch"
              Expect.isOk (checkArgs one [ "n", JInt 1 ]) "the permitted number"
              Expect.isError (checkArgs one [ "n", JInt 2 ]) "an unpermitted number is now seen, and refused"
          }

          test "the clause rides the demanded document, and reads back clause for clause" {
              let projection =
                  ServerDemanded.ofHandler witness (polling 3)
                  |> ServerDemanded.withConstraints ninety

              let document = Demanded.encode projection

              Expect.stringContains
                  document
                  "{\"clause\":\"atMost\",\"argument\":\"count\",\"limit\":90}"
                  "the clause, as declared"

              match Demanded.decode document with
              | Ok read -> Expect.equal read projection "read back to the same projection"
              | Error failure -> failtestf "the document does not read back: %A" failure

              let malformed =
                  document.Replace(
                      "{\"clause\":\"atMost\",\"argument\":\"count\",\"limit\":90}",
                      "{\"clause\":\"atMost\",\"argument\":\"count\",\"limit\":\"90\"}"
                  )

              match Demanded.decode malformed with
              | Ok _ -> failtest "a limit that is not an integer was read"
              | Error failure -> Expect.stringContains failure.Field "limit" "refused at the limit"
          } ]
