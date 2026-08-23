module Fuaran.Program.Bounded.Resolve

open Fuaran.UI.Types
open Fuaran.UI.Ops.Introspect
open Fuaran.UI.Renderer
open Fuaran.UI.Renderer.BindingResolver

// ============================================================================
//  Binding re-resolution — the pass that makes a state change VISIBLE.
//
//  Placement-neutral, like the interpreter beside it: both loops (a server
//  session, a browser client) re-resolve the same way, because both diff or
//  re-render from the same fixed base tree plus a store.
//
//  ── Why re-resolve into the tree (the binding-blind-diff problem) ───────────
//  A structural diff compares `Node` trees via canonical JSON, and a
//  `Binding.State(key, default)` canonical-encodes IDENTICALLY regardless of the
//  store value (it encodes the key + default, not the resolved value). So
//  diffing the raw decoded tree before/after a `SetState` yields NO ops — the
//  state change is invisible. The fix: `resolveTree` substitutes every
//  resolvable binding with `Binding.Static (resolved value)` (and every bound
//  `TextSource` with its resolved `Literal`) BEFORE the diff, so a state change
//  shows up as a real `Binding.Static old → Binding.Static new` drift the diff
//  can see and patch. A hand-authored loop sidesteps this by baking state into
//  the tree in its `view`; the bounded path has no `view`, so it resolves.
//
//  ── The base tree is FIXED (specified — §10.5) ──────────────────────────────
//  `resolveTree` is applied to the tree the program started from, never to the
//  previous step's output. Folding a step's substitutions into the next step's
//  input makes an earlier resolution unrecoverable, and the store stops being
//  the only thing carrying state. The corpus pins it with a multi-event
//  scenario (`fixed-base-reresolution`) — the one shape that tells the two
//  readings apart, since every one-event scenario passes under both.
//
//  ── Coverage floor (documented) ─────────────────────────────────────────────
//  `resolveTree` covers the state-reactive Display / Input / Layout kinds
//  generators actually use for live content (Heading / Markdown / Badge / Metric
//  / Callout / Progress / LabelValueRow / Link / Sparkline text+bindings;
//  Button / Select; Tabs / Stepper / Disclosure / Card / SummaryList). Kinds
//  with reactive bindings NOT yet covered (Visualisation Grid/Chart/Map,
//  Form/Filters/FileUpload, per-tab `TabHeaders`) PASS THROUGH unresolved — a
//  state change inside them won't patch yet (the documented coarse floor; the
//  follow-on extends the per-kind coverage). Containers recurse generically via
//  `Introspect.getChildren` / `withChildren`, so structure is never lost.
//
//  §10.5 rules that arrangement normative: a floor EXISTS and is neither
//  everything nor nothing — which is the part a host cannot derive from the text
//  — while its membership is enumerated by the corpus's driver-semantics
//  scenarios rather than tabulated in a document that does not own the tree
//  vocabulary. A scenario recording a PASS-THROUGH is as binding as one
//  recording a resolution, so widening this floor moves a recorded expectation
//  in the same change-set. For a kind no scenario reaches, the floor above is
//  this host's own and this paragraph is the declaration §10.5 asks for.
// ============================================================================

/// Substitute a binding with `Binding.Static (resolved value)` when it resolves;
/// leave it untouched otherwise (NotResolved / Errored — the renderer's
/// loading / error states still apply). Generic over the binding's `'T`.
let private substB (sources: BindingSources) (b: Binding<'T>) : Binding<'T> =
    match BindingResolver.resolve sources b with
    | Resolved v -> Binding.Static(Some v)
    | NotResolved
    | Errored _
    | I18nUnresolved _ -> b

let private substBOpt (sources: BindingSources) (bo: Binding<'T> option) : Binding<'T> option =
    bo |> Option.map (substB sources)

/// Resolve a bound `TextSource` to its `Literal`; pass `Literal` / `I18n`
/// through (stable under `SetState`).
let private resolveText (sources: BindingSources) (t: TextSource) : TextSource =
    match t with
    | TextSource.Bound b ->
        match BindingResolver.resolve sources b with
        | Resolved s -> TextSource.Literal s
        | NotResolved
        | Errored _
        | I18nUnresolved _ -> t
    | TextSource.Literal _
    | TextSource.I18n _ -> t

let private resolveTextOpt (sources: BindingSources) (t: TextSource option) : TextSource option =
    t |> Option.map (resolveText sources)

/// Resolve this node's OWN state-reactive leaf fields (text + bindings) against
/// the store. Children are untouched here (the generic recursion in
/// `resolveTree` handles them). Uncovered kinds pass through (the floor).
let private resolveOwnFields (sources: BindingSources) (node: Node<obj>) : Node<obj> =
    let kind =
        // One flat match with one catch-all. Uncovered kinds pass through (the
        // floor).
        match node.Kind with
        | NodeKind.Heading s ->
            NodeKind.Heading
                { s with
                    Text = resolveText sources s.Text }
        | NodeKind.Markdown s ->
            NodeKind.Markdown
                { s with
                    Text = resolveText sources s.Text }
        | NodeKind.Badge s ->
            NodeKind.Badge
                { s with
                    Label = resolveText sources s.Label }
        | NodeKind.Metric s ->
            NodeKind.Metric
                { s with
                    Label = resolveText sources s.Label
                    Value = substB sources s.Value
                    Trend = substBOpt sources s.Trend
                    Subtext = resolveTextOpt sources s.Subtext }
        | NodeKind.Callout s ->
            NodeKind.Callout
                { s with
                    Heading = resolveTextOpt sources s.Heading
                    Body = resolveText sources s.Body }
        | NodeKind.Progress s ->
            NodeKind.Progress
                { s with
                    Fraction = substB sources s.Fraction
                    Label = resolveTextOpt sources s.Label
                    Caveat = resolveTextOpt sources s.Caveat }
        | NodeKind.LabelValueRow s ->
            NodeKind.LabelValueRow
                { s with
                    Label = resolveText sources s.Label
                    Value = substB sources s.Value
                    Help = resolveTextOpt sources s.Help }
        | NodeKind.Link s ->
            NodeKind.Link
                { s with
                    Href = substB sources s.Href
                    Label = resolveText sources s.Label }
        | NodeKind.Sparkline s ->
            NodeKind.Sparkline
                { s with
                    Source = substB sources s.Source }

        | NodeKind.Button s ->
            NodeKind.Button
                { s with
                    Label = resolveText sources s.Label
                    Tooltip = resolveTextOpt sources s.Tooltip
                    Disabled = substBOpt sources s.Disabled }
        | NodeKind.Select s ->
            NodeKind.Select
                { s with
                    Label = resolveText sources s.Label
                    Source = substB sources s.Source
                    Value = substB sources s.Value
                    Placeholder = resolveTextOpt sources s.Placeholder }

        | NodeKind.Tabs s ->
            NodeKind.Tabs
                { s with
                    ActiveIndex = substB sources s.ActiveIndex
                    ActiveTag = substBOpt sources s.ActiveTag }
        | NodeKind.Stepper s ->
            NodeKind.Stepper
                { s with
                    ActiveStep = substB sources s.ActiveStep }
        | NodeKind.Disclosure s ->
            NodeKind.Disclosure
                { s with
                    Open = substB sources s.Open
                    Heading = resolveText sources s.Heading }
        | NodeKind.Box s ->
            NodeKind.Box
                { s with
                    Heading = resolveTextOpt sources s.Heading }
        | NodeKind.SummaryList s ->
            NodeKind.SummaryList
                { s with
                    Heading = resolveTextOpt sources s.Heading }

        | other -> other

    { node with Kind = kind }

/// Re-resolve a whole tree's state-reactive bindings against the store,
/// producing a tree whose changed values a (binding-blind) structural diff can
/// see. Structure is preserved (no node added / removed / re-id'd); only leaf
/// binding / text values change.
let rec resolveTree (sources: BindingSources) (node: Node<obj>) : Node<obj> =
    let node = resolveOwnFields sources node

    match getChildren node.Kind with
    | Some kids ->
        let kids' = kids |> List.map (resolveTree sources)

        match withChildren node.Kind kids' with
        | Some k -> { node with Kind = k }
        | None -> node
    | None -> node
