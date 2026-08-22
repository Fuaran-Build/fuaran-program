namespace Fuaran.Program.Server

open Fuaran.UI.Types
open Fuaran.Program.Bounded

// ============================================================================
//  The harvest: a program's demand projection, as the document a composition
//  surface can hold.
//
//  Everything this file calls already existed. What did not exist was ONE call
//  that goes from a program and the registration behind it to the bytes, and
//  the absence had a cost that is easy to underrate: a consumer wanting the
//  document assembled the pipeline itself — walk the tree, walk the reachable
//  handlers, join the postures, encode — and every consumer that assembles a
//  pipeline is a consumer that can assemble a slightly different one. Two
//  callers that disagreed about whether postures were joined would publish two
//  documents for the same program, both well-formed, and nothing downstream
//  could tell which was which.
//
//  ── Why the bytes, and not the projection ───────────────────────────────────
//  A projection is a value in this process. A DOCUMENT is what travels: it is
//  self-describing, carries its own kind and version, names no consumer, and
//  can be stored, shipped and checked somewhere the program never goes. A
//  consumer that holds the document holds everything it needs; one that holds
//  the projection holds a value it can only compare against another value
//  produced by this same build.
//
//  The projection is returned ALONGSIDE rather than instead, so a caller that
//  wants to look at what it just published does not have to decode its own
//  output to do it. The two are the same fact in two forms and the document is
//  derived from the projection here, in one place, which is the property that
//  makes them impossible to disagree.
//
//  ── Why determinism is the whole point ──────────────────────────────────────
//  `Demanded.normalise` puts every list into a distinct, sorted order and
//  `Demanded.encode` renders that order, so the same program over the same
//  registration produces byte-identical output on every run and on every
//  machine. That was already true and was already worth having — two documents
//  comparing by value is what the encoding exists to provide.
//
//  What it BUYS, once a document can be addressed by its content, is different
//  in kind: a composition surface can pin WHICH projection it was composed
//  against, and a reference whose published document has since moved becomes a
//  defect somebody can be shown rather than a divergence that only surfaces
//  when the running host refuses an effect. A non-deterministic encoder would
//  make that address change for reasons that are not changes, which is worse
//  than having no address at all — an alarm that fires without a cause is one
//  people learn to silence.
//
//  ── What this file deliberately does NOT do ─────────────────────────────────
//  It does not COMPUTE the address. An address a producer asserts about its own
//  output is worth nothing; the check is the consumer recomputing it over the
//  bytes it actually holds, and that is where the digest belongs. What a
//  producer owes is bytes that are worth addressing, which is exactly what this
//  returns. The algorithm and the rendering are specified in the wire
//  specification's cross-layer section, so any side can compute one without
//  agreeing with this file about anything.
//
//  It also names no CONSUMER and carries no reference id. The document
//  describes a program; binding it to an identifier is the act of whatever
//  composes the program, and doing it here would put a consumer's id space into
//  a producer's output.
// ============================================================================

/// A harvested publication: the bytes that travel, and what they say.
///
/// Both are returned because they are one fact in two forms, and deriving one
/// from the other anywhere but here is how they come to disagree.
type HarvestedDemand =
    {
        /// The canonical projection document — deterministic, self-describing,
        /// and the whole of what a consumer needs.
        Document: string
        /// The same fact as a value, for a caller inspecting what it published
        /// without decoding its own output.
        Projection: DemandedProjection
    }

module Harvest =

    /// Publish an already-computed projection.
    ///
    /// The one place a projection becomes bytes, so every entry point below is
    /// the same encoding by construction rather than by three of them calling
    /// the same function and being expected to keep doing so.
    ///
    /// It NORMALISES first, through the same public combinator the walks
    /// already go through, and that is load-bearing rather than defensive. The
    /// encoder renders the lists it is given; the reader refuses a document
    /// whose lists are not distinct and sorted. So a projection assembled by a
    /// caller rather than produced by a walk would encode to bytes no
    /// conformant consumer can read — and the failure would land on the
    /// consumer, which is the worst place for it. The walks' output is already
    /// normalised, so this costs them nothing and cannot change what they
    /// publish.
    let publish (projection: DemandedProjection) : HarvestedDemand =
        let normalised = Demanded.union [ projection ]

        { Document = Demanded.encode normalised
          Projection = normalised }

    /// **The one call**: the document for a program and the handler
    /// registration behind it.
    ///
    /// The tree's own demands, the demands of every handler it can NAME, and
    /// each of those handlers' replay postures — the complete two-tier
    /// document, computed through the single reachability rule so the
    /// capabilities and the postures cannot describe different handler sets.
    let ofProgram (handlers: Map<string, Handler>) (root: Node<obj>) : HarvestedDemand =
        publish (Replay.ofTreeAndHandlers handlers root)

    /// The document for a REGISTRATION alone, with no program in hand.
    ///
    /// A different question from `ofProgram` and not a degenerate case of it: it
    /// asks what this registration could ever be asked for by ANY program that
    /// reaches all of it, which is the ceiling a host publishes when it wants to
    /// describe its own surface rather than one program's use of it. The client
    /// tier is therefore empty — there is no tree — and the server tier is
    /// present, because a walk did run.
    let ofRegistration (handlers: Handler seq) : HarvestedDemand =
        publish (ServerDemanded.ofHandlers handlers |> Replay.withPostures handlers)
