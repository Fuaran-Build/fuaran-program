module Fuaran.Program.Bounded.Resolve

// ============================================================================
//  Binding re-resolution — the pass that makes a state change VISIBLE.
//
//  Placement-neutral, like the interpreter beside it: both loops (a server
//  session, a browser client) re-resolve the same way, because both diff or
//  re-render from the same fixed base tree plus a store.
//
//  ── Why re-resolve into the tree (the binding-blind-diff problem) ───────────
//  A structural diff compares trees by their canonical encoding, and a bound
//  field canonical-encodes IDENTICALLY regardless of the store value (it
//  encodes the binding, not the resolved value). So diffing the raw decoded
//  tree before/after a state write yields NO ops — the state change is
//  invisible. The fix: `resolveTree` substitutes every resolvable binding with
//  its resolved value BEFORE the diff, so a state change shows up as a real
//  drift the diff can see and patch. A hand-authored loop sidesteps this by
//  baking state into the tree in its `view`; the bounded path has no `view`,
//  so it resolves.
//
//  WHICH fields of a node are bound, and what substituting one means, is the
//  domain's: the witness's `Tree.Resolve` re-resolves ONE node's own fields.
//  The walk is this module's, over the witness's STRUCTURAL child surface
//  (`Tree.Nodes`), so structure is never lost and the per-node coverage floor
//  §10.5 asks a host to declare is declared beside the witness that has it.
//
//  ── The base tree is FIXED (specified — §10.5) ──────────────────────────────
//  `resolveTree` is applied to the tree the program started from, never to the
//  previous step's output. Folding a step's substitutions into the next step's
//  input makes an earlier resolution unrecoverable, and the store stops being
//  the only thing carrying state. The corpus pins it with a multi-event
//  scenario (`fixed-base-reresolution`) — the one shape that tells the two
//  readings apart, since every one-event scenario passes under both.
// ============================================================================

/// Re-resolve a whole tree's state-reactive bindings against the store,
/// producing a tree whose changed values a (binding-blind) structural diff can
/// see. Structure is preserved (no node added / removed / re-id'd); only leaf
/// binding / text values change.
let rec resolveTree
    (witness: ProgramWitness<'Node, 'Action, 'Expr, 'Store, 'Op, 'Effect>)
    (store: 'Store)
    (node: 'Node)
    : 'Node =
    let node = witness.Tree.Resolve store node

    match witness.Tree.Nodes.Children node with
    | [] -> node
    | kids -> witness.Tree.Nodes.ReplaceChildren node (kids |> List.map (resolveTree witness store))
