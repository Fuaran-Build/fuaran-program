# Fuaran.Program

**Programs as data.** The Fuaran program domain models application behaviour the way the Fuaran UI
tier models interfaces: as a typed tree with a canonical wire form, validated before execution,
applied through an op engine, and journaled for replay. The algebra is deliberately **bounded and
total** — sequencing, typed branching, and named effects; no general recursion, no closures over
the wire, no foreign code — so a program emitted by a machine can be checked, run, diffed, and
audited like data, because it is data.

**Status: skeleton.** This repo currently ships the domain identity and design commitments
([DECISIONS.md](DECISIONS.md)) with a buildable package shell. The first substantive surfaces —
the placement-neutral bounded interpreter and the client program loop — are in active development.

## Build

```powershell
pwsh ./run.ps1
```

Requires the .NET 10 SDK (pinned in `global.json`).

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
