namespace Fuaran.Program

/// Domain identity. The substantive surfaces — the bounded, total logic-tree
/// algebra, its interpreters, and the program loop — land here package by
/// package; this module exists so the skeleton builds, tests, and packs from
/// the first commit.
module About =

    [<Literal>]
    let Name = "Fuaran.Program"

    /// The standing design commitments every surface in this domain honours,
    /// fixed at charter time — see DECISIONS.md D1–D4.
    let commitments =
        [ "pipeline core: sequencing + typed branching + named effects; richer control structure is vocabulary atop, never a new evaluator"
          "total: structural recursion over finite data and bounded iteration only — no general recursion"
          "closed effects: effect vocabularies are closed DUs, extended only via registered, policy-gated host performers"
          "no foreign code: a program tree carries data only — no closure survives the wire" ]
