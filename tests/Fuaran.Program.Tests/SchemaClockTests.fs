/// The evaluator's clock in the static query-schema walk (Phase 1896, raising
/// the substrate to Core-Compute 0.34.0, which added `ColExpr.Now`).
module Fuaran.Program.Tests.SchemaClockTests

open Expecto
open Fuaran.Core
open Fuaran.Program.Bounded

[<Tests>]
let tests =
    testList
        "the query-schema walk: the clock reads no column"
        [ test "Now names no column of the input row, at either grain" {
              Expect.isEmpty (Schema.readsOfExpr (ColExpr.Now NowGrain.Date)) "a date clock"
              Expect.isEmpty (Schema.readsOfExpr (ColExpr.Now NowGrain.Timestamp)) "a timestamp clock"
          }

          test "an expression comparing a column with the clock reads exactly that column" {
              Expect.equal
                  (Schema.readsOfExpr (ColExpr.Binary(Lt, ColExpr.Col "due", ColExpr.Now NowGrain.Date)))
                  [ "due" ]
                  "the column, and nothing for the clock"
          } ]
