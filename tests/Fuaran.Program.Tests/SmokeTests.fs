module Fuaran.Program.Tests.SmokeTests

open Expecto
open Fuaran.Program

[<Tests>]
let smoke =
    testList
        "skeleton"
        [ test "package identity" { Expect.equal About.Name "Fuaran.Program" "domain identity constant" }
          test "charter commitments are stated" {
              Expect.equal (List.length About.commitments) 4 "the four charter commitments (DECISIONS.md D1–D4)"
          } ]
