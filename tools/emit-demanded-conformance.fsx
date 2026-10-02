// SPDX-License-Identifier: Apache-2.0
//
// Emit the conformance corpus for the demanded-effect projection document.
//
//   dotnet build Fuaran.Program.slnx
//   dotnet fsi tools/emit-demanded-conformance.fsx conformance/demanded-effect-projection.json
//
// ── What this is ─────────────────────────────────────────────────────────────
//
// A thin wrapper. The corpus — every vector, and what this tier's own pinned
// reader makes of each — is `DemandedCorpus.emit` in the server test project
// (tests/Fuaran.Program.Server.Tests/DemandedCorpus.fs), which carries the full
// account of why the corpus exists. This script only writes it to a file.
//
// The split is Phase 1978's. The corpus used to be computed HERE, in a script
// nothing ran, and it fell two document versions and one policy clause behind
// the document it describes without anything noticing. Now the server suite's
// `DemandedCorpusTests` compares `emit` with the committed file on every gate
// run, so a document change that does not regenerate the vectors is red there,
// and this script and that test cannot disagree about what the corpus is.
//
// ── Where the vectors go ─────────────────────────────────────────────────────
//
// The committed corpus is `conformance/demanded-effect-projection.json` in THIS
// repository, beside the codec that is its authority. It is NOT part of the
// program wire specification's corpus: that specification does not spell the
// demanded document (`docs/generic-tier.md` §6), so the specification's
// forward coupling does not reach it and regenerating it is a change to this
// repository alone.
//
// Nothing here names any consumer.

#r "../tests/Fuaran.Program.Server.Tests/bin/Debug/net10.0/Fuaran.Program.Server.Tests.dll"

let target =
    match fsi.CommandLineArgs |> Array.toList with
    | _ :: path :: _ -> path
    | _ -> failwith "usage: dotnet fsi tools/emit-demanded-conformance.fsx <outputFile>"

let out = Fuaran.Program.Server.Tests.DemandedCorpus.emit ()
System.IO.File.WriteAllText(target, out)

printfn "wrote %d vectors to %s" (List.length Fuaran.Program.Server.Tests.DemandedCorpus.vectors) target
