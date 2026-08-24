namespace Fuaran.Program.Runtime

open Fuaran.Core
open Fuaran.UI.ServerDriven

// ============================================================================
//  The client host-effect seam — closed vocabulary, registered performers,
//  default-deny gate.
//
//  This is DECISIONS.md D3 as code, for the browser placement. Read it there
//  rather than re-deriving it here; what this file adds is the mechanism:
//
//    - The effect vocabulary is a CLOSED DU (`ClientEffect`). A program tree can
//      *name* only what that DU can express, and nothing on the wire widens it.
//    - Extensibility is a HOST act: an embedding host registers a named
//      performer. Until it does, the effect has nowhere to go.
//    - Every dispatch passes a policy gate FIRST. The gate defaults to refusing
//      everything, so a host that registers nothing performs nothing — the same
//      posture the dispatch gate and the server placement's services take.
//    - A refusal is RECORDED, never silently dropped. "Nothing happened" and
//      "the host refused that" are different facts, and a generated app being
//      debugged needs to tell them apart.
//
//  The server placement reaches the same vocabulary through a different
//  transport: it ships `ClientEffect`s to a browser shim that performs them.
//  One effect vocabulary, two transports — so a host registering performers
//  here should match the shim's behaviour arm for arm, and the parity family
//  asserts it where the behaviour is observable.
// ============================================================================

// ─── Destination policy ────────────────────────────────────────────────────
//
//  The gate above takes the effect's DISCRIMINATOR, and that is all it can
//  ever see. It answers "may this host navigate at all", which is a real
//  question and not the whole one: a host that allows `Navigate` allows
//  navigation to ANYWHERE, and `Download` of anything from anywhere. The
//  discriminator carries no destination, so no policy expressed over it can
//  distinguish "go to our own checkout page" from "go to an attacker's
//  collector with the session in the query string".
//
//  So a second, typed policy sits beside the gate and is consulted WITH the
//  payload's destination. Two gates, two questions, both positive lists: the
//  discriminator gate says WHAT the host does, the destination policy says
//  WHERE it may do it. Neither substitutes for the other, and the ordering is
//  load-bearing — an effect the host does not do at all is refused before its
//  destination is even parsed.
//
//  **Why the origin machinery is expressed here rather than imported.** The
//  scheme floor IS imported — `Fuaran.UI.Renderer.Sanitize.sanitizeUrl` decides
//  whether a URL is safe to have, and duplicating that would be a second
//  implementation of a security decision. What is local is the ORIGIN
//  extraction and the allowlist, because the UI tier's matching form arrived in
//  a version this repo does not pin (DECISIONS.md D4's deliberate lag: the pin
//  is a visible decision, not drift). The two are written to the same rules —
//  host only, no scheme and no path, label-boundary suffix matching, userinfo
//  discarded at the LAST `@` — and they collapse to a re-export the moment the
//  pin advances.

/// One allowed destination. Hosts only — no scheme, no port, no path.
///
/// Scheme is deliberately absent: the floor has already reduced it to the
/// allowlisted set, and every "scheme wildcard" spelling parses differently on
/// different hosts, which makes the wildcard itself the vulnerability. Path is
/// deliberately absent too: a path is not a security boundary, and a policy
/// that looks like it bounds one invites reliance on a bound it does not have.
type EgressOrigin =
    /// Exactly this host — `example.com` and nothing else, not `a.example.com`.
    | ExactHost of host: string
    /// This host and any subdomain of it. Matches at a LABEL BOUNDARY, so
    /// `example.com` never matches `notexample.com`.
    | HostSuffix of suffix: string

/// One rule: an origin, and the effect discriminators it is declared FOR.
type EgressRule =
    {
        Origin: EgressOrigin
        /// `ClientEffect.kind` names, or the single entry
        /// `EgressPolicy.anyEffect` for "every effect". An EMPTY list declares
        /// nothing — a rule that names no effect permits no effect, which is
        /// the only reading consistent with a positive list.
        ///
        /// The `anyEffect` sentinel is a wildcard over EFFECT NAMES, which is
        /// a closed set the host controls. It is emphatically not the kind of
        /// wildcard `EgressOrigin` refuses: no rule may wildcard a HOST, where
        /// the set is open and the parsing differences between hosts are
        /// themselves the vulnerability.
        Effects: string list
    }

/// The typed egress allowlist consulted beside the discriminator gate.
type EgressPolicy =
    {
        Rules: EgressRule list
        /// When `true`, every network origin is permitted and `Rules` is not
        /// consulted. A FIELD rather than the absence of rules on purpose: an
        /// empty allowlist must read as "nothing is declared", never as
        /// "everything is fine" — an empty list is what a half-built policy
        /// looks like, and conflating the two would make forgetting to declare
        /// anything indistinguishable from deciding not to.
        AllowAnyOrigin: bool
        /// Whether SAME-ORIGIN destinations (a relative route, a fragment) are
        /// permitted. `true` in both shipped policies: a destination on the
        /// host's own origin has not left.
        AllowLocal: bool
        /// Whether hostless schemes (`mailto:`, `tel:`) are permitted. `false`
        /// by default — `mailto:` is an egress channel with no host for a rule
        /// to name, so it can only be permitted wholesale, never allowlisted.
        AllowNonNetwork: bool
    }

/// What an effect's payload resolves to, once the scheme floor has spoken.
[<RequireQualifiedAccess>]
type EffectDestination =
    /// The effect names no destination at all (`WriteToClipboard`, `Focus`).
    /// The discriminator gate is the whole policy for these.
    | Absent
    /// Same-origin: a relative route, a fragment, an empty URL. Also where
    /// `ReadFileBody` lands — see `destinationOf`.
    | Local
    /// An absolute network destination at this host, normalised.
    | Remote of host: string
    /// A scheme with no network host for a rule to name.
    | NonNetwork of scheme: string
    /// The scheme floor rejected it, or it declares a network scheme with no
    /// extractable host.
    | Rejected

module EffectDestination =

    /// The log-safe name of a destination. `Remote` yields the HOST — never the
    /// path or query, which is exactly where an exfiltrated payload sits.
    let describe (d: EffectDestination) : string =
        match d with
        | EffectDestination.Absent -> "none"
        | EffectDestination.Local -> "local"
        | EffectDestination.Remote host -> host
        | EffectDestination.NonNetwork scheme -> scheme + ":"
        | EffectDestination.Rejected -> "unparseable"

module EgressPolicy =

    /// The `EgressRule.Effects` entry meaning "every effect". Not a valid
    /// `ClientEffect.kind`, so it can never collide with a real effect name.
    [<Literal>]
    let anyEffect = "*"

    let private networkSchemes = Set.ofList [ "http"; "https"; "ftp"; "sftp" ]

    /// Lowercase, trim, drop a single trailing root dot. `example.com.` and
    /// `example.com` are one host to a resolver, so they must be one host to a
    /// policy — otherwise the dotted spelling walks past an exact rule.
    let private normalizeHost (h: string) : string =
        if isNull (box h) then
            ""
        else
            let t = h.Trim().ToLowerInvariant()

            if t.EndsWith "." then t.Substring(0, t.Length - 1) else t

    /// The scheme of an absolute URL, lowercased; `None` for a relative one.
    let private schemeOf (url: string) : string option =
        let mutable colon = -1
        let mutable stop = -1
        let mutable i = 0

        while i < url.Length && colon < 0 && stop < 0 do
            match url[i] with
            | ':' -> colon <- i
            | '/'
            | '?'
            | '#' -> stop <- i
            | _ -> ()

            i <- i + 1

        if colon < 0 || (stop >= 0 && stop < colon) then
            None
        else
            Some(url.Substring(0, colon).Trim().ToLowerInvariant())

    /// The host of an absolute URL's authority. `\` counts as `/` when locating
    /// the authority; userinfo before the LAST `@` is discarded; a port is
    /// dropped; an IPv6 literal keeps its brackets.
    ///
    /// The LAST `@` is load-bearing rather than fussy:
    /// `https://ours.example@evil.example/x` is a request to `evil.example`,
    /// and a first-`@` split reads it as the opposite — the credential-
    /// confusion spelling an allowlist exists to refuse.
    let private authorityHost (url: string) : string option =
        match schemeOf url with
        | None -> None
        | Some scheme ->
            let mutable i = scheme.Length + 1
            let mutable slashes = 0

            while i < url.Length && (url[i] = '/' || url[i] = '\\') do
                slashes <- slashes + 1
                i <- i + 1

            if slashes < 2 then
                None
            else
                let start = i
                let mutable j = i

                let isEnd (c: char) =
                    c = '/' || c = '\\' || c = '?' || c = '#'

                while j < url.Length && not (isEnd url[j]) do
                    j <- j + 1

                let authority = url.Substring(start, j - start)

                let afterUserInfo =
                    let at = authority.LastIndexOf '@'
                    if at >= 0 then authority.Substring(at + 1) else authority

                if afterUserInfo = "" then
                    None
                elif afterUserInfo.StartsWith "[" then
                    let close = afterUserInfo.IndexOf ']'

                    if close < 0 then
                        None
                    else
                        Some(afterUserInfo.Substring(0, close + 1).ToLowerInvariant())
                else
                    let port = afterUserInfo.IndexOf ':'

                    let h =
                        if port >= 0 then
                            afterUserInfo.Substring(0, port)
                        else
                            afterUserInfo

                    let n = normalizeHost h
                    if n = "" then None else Some n

    /// Resolve a URL to the destination a policy reasons about. The scheme floor
    /// runs FIRST and is the UI tier's, not a second copy of it — there is
    /// nothing to say about where an unsafe URL points.
    let classify (url: string) : EffectDestination =
        match Fuaran.UI.Renderer.Sanitize.sanitizeUrl url with
        | None -> EffectDestination.Rejected
        | Some safe ->
            if safe = "" then
                EffectDestination.Local
            else
                match schemeOf safe with
                // A schemeless URL reaching here is same-origin: the floor has
                // already refused every protocol-relative spelling, which is the
                // one schemeless shape that leaves the origin.
                | None -> EffectDestination.Local
                | Some scheme when networkSchemes.Contains scheme ->
                    match authorityHost safe with
                    | Some h -> EffectDestination.Remote h
                    | None -> EffectDestination.Rejected
                | Some scheme -> EffectDestination.NonNetwork scheme

    let private originMatches (origin: EgressOrigin) (host: string) : bool =
        match origin with
        | ExactHost h ->
            let h = normalizeHost h
            h <> "" && h = host
        | HostSuffix s ->
            let s = normalizeHost s
            s <> "" && (host = s || host.EndsWith("." + s))

    /// Deny every destination that leaves the origin. THE DEFAULT — an emission
    /// cannot declare its own egress, so absent a host's declaration it gets
    /// none, the same inversion `denyAll` takes for the discriminator gate.
    let denyNonLocal: EgressPolicy =
        { Rules = []
          AllowAnyOrigin = false
          AllowLocal = true
          AllowNonNetwork = false }

    /// Permit every destination. Named rather than default, so reaching it is a
    /// deliberate and greppable act.
    let permissive: EgressPolicy =
        { Rules = []
          AllowAnyOrigin = true
          AllowLocal = true
          AllowNonNetwork = true }

    /// Declare an origin for a set of effect names. An empty list is taken as
    /// "every registered effect" — the ergonomic reading of a one-line
    /// `allowOrigin`, distinct from an `EgressRule` whose `Effects` is empty,
    /// which permits nothing. The record is data and says what it lists; the
    /// helper is a convenience and says what its caller meant.
    let allowOrigin (origin: EgressOrigin) (effects: string list) (policy: EgressPolicy) : EgressPolicy =
        { policy with
            Rules =
                policy.Rules
                @ [ { Origin = origin
                      Effects = if List.isEmpty effects then [ anyEffect ] else effects } ] }

    /// Does this policy permit this effect to reach this destination?
    let permits (policy: EgressPolicy) (effect: string) (destination: EffectDestination) : bool =
        match destination with
        // Nothing to say — the discriminator gate already decided, and
        // inventing a destination for an effect that has none would only make
        // the record dishonest.
        | EffectDestination.Absent -> true
        | EffectDestination.Rejected -> false
        | EffectDestination.Local -> policy.AllowLocal
        | EffectDestination.NonNetwork _ -> policy.AllowNonNetwork
        | EffectDestination.Remote host ->
            let host = normalizeHost host

            host <> ""
            && (policy.AllowAnyOrigin
                || policy.Rules
                   |> List.exists (fun r ->
                       (List.contains effect r.Effects || List.contains anyEffect r.Effects)
                       && originMatches r.Origin host))

/// Where each arm of the closed effect vocabulary sends its payload.
module ClientEffectDestination =

    /// The destination an effect reaches, by arm.
    ///
    /// `ReadFileBody` is `Local` rather than `Absent`, and the distinction is the
    /// honest one rather than a convenience: the arm carries a node id, not a URL,
    /// so there is no origin to allowlist — but the body it reads travels back to
    /// the host that is driving the loop, which is the local origin by
    /// construction. A policy denying local egress therefore denies it, and one
    /// permitting local egress leaves it to the discriminator gate, which is
    /// exactly where the decision belongs. What this seam does NOT claim is any
    /// bound on what the host does with the body once it has it.
    let destinationOf (effect: ClientEffect) : EffectDestination =
        match effect with
        | ClientEffect.Navigate route
        | ClientEffect.PushState route -> EgressPolicy.classify route
        | ClientEffect.Download(url, _) -> EgressPolicy.classify url
        | ClientEffect.ReadFileBody _ -> EffectDestination.Local
        | ClientEffect.WriteToClipboard _
        | ClientEffect.Focus _ -> EffectDestination.Absent

/// Why an effect did not run. Every arm is recorded through `OnDenied`; the
/// distinctions matter to whoever is debugging an emission, because they say
/// different things — "this host does not offer that capability", "this host
/// has it and refused this use of it", and "this host has it, permits it, and
/// does not send it THERE".
[<RequireQualifiedAccess>]
type EffectDenial =
    /// No performer is registered under this effect's name — the capability is
    /// absent from this host, so the program's reach never extended to it.
    | Unregistered of effect: string
    /// A performer exists, and the policy gate refused this dispatch.
    | GateRefused of effect: string
    /// A performer exists and the gate permitted the effect, but the
    /// destination is not one this host declared. Carries the ORIGIN — the
    /// host, or the class of destination where there is no host — and never the
    /// full URL, because a refusal record outlives the session and the query
    /// string of a refused exfiltration attempt IS the payload.
    | DestinationRefused of effect: string * origin: string

module EffectDenial =

    // ─── The wire projection ─────────────────────────────────────────────────
    //
    //  The program wire specification's §5.3 declares the denial as a document
    //  with TWO arms and an optional `origin`, and this host's DU has three
    //  arms. That is not a mismatch to reconcile away: the specification's
    //  `GateRefused` says *this host has the capability and refused this use of
    //  it*, which is exactly what both `GateRefused` and `DestinationRefused`
    //  say here — they differ in the GROUND, and the ground is what `origin`
    //  carries. So the projection is total in both directions, and the three
    //  local arms survive because the seam's own reader wants them apart.
    //
    //  The alternative — a third arm on the wire — was available and reads more
    //  naturally. It is a breaking change to a closed vocabulary, and the fact
    //  it would carry is one `GateRefused` already carries; §11.1 records the
    //  arithmetic.

    /// The specification's §5.3 document for this denial, canonically encoded.
    /// `$type` first, then `capability`, then `origin` where the refusal's
    /// ground was a destination — which is Ordinal order as well as the order
    /// the specification's table lists them.
    let encodeWire (d: EffectDenial) : string =
        let doc =
            match d with
            | EffectDenial.Unregistered name -> Canon.typed "Unregistered" [ "capability", JStr name ]
            | EffectDenial.GateRefused name -> Canon.typed "GateRefused" [ "capability", JStr name ]
            | EffectDenial.DestinationRefused(name, origin) ->
                Canon.typed "GateRefused" [ "capability", JStr name; "origin", JStr origin ]

        Canon.render doc

    /// Read a denial back from the three positions §5.3 declares — the arm, the
    /// capability, and the origin where one is present.
    ///
    /// **Reading rather than diffing bytes is the point.** A conformance harness
    /// comparing recorded denials could hold two opaque strings side by side and
    /// learn nothing about whether this host RECOGNISES the vocabulary; going
    /// through this function means an arm outside §5.3, or an `origin` on an arm
    /// that never consulted a destination, fails to load rather than passing
    /// through as a value somebody expected to be honoured.
    let ofWire (arm: string) (capability: string) (origin: string option) : Result<EffectDenial, string> =
        match arm, origin with
        | "Unregistered", None -> Ok(EffectDenial.Unregistered capability)
        | "Unregistered", Some _ ->
            Error
                "an Unregistered denial carries an origin: the capability was never reachable, so no destination was ever consulted (§5.3)"
        | "GateRefused", None -> Ok(EffectDenial.GateRefused capability)
        | "GateRefused", Some o -> Ok(EffectDenial.DestinationRefused(capability, o))
        | other, _ -> Error(sprintf "'%s' is not an arm of the denial vocabulary (§5.3)" other)

    /// Human-readable, log-safe description. Carries the effect's DISCRIMINATOR
    /// only, never its payload — a denied `Navigate` must not log the route it
    /// wanted, and a denied `WriteToClipboard` must not log the text. The
    /// destination arm adds the ORIGIN, which is the least that makes a refusal
    /// actionable and the most that can be recorded without recording the
    /// payload itself.
    let describe (d: EffectDenial) : string =
        match d with
        | EffectDenial.Unregistered name ->
            sprintf "effect '%s' was not performed: no performer is registered for it on this host" name
        | EffectDenial.GateRefused name -> sprintf "effect '%s' was not performed: the policy gate refused it" name
        | EffectDenial.DestinationRefused(name, origin) ->
            sprintf "effect '%s' was not performed: destination '%s' is not declared for it on this host" name origin

/// A closed, default-deny registry of host-performed effects.
type EffectRegistry =
    {
        /// Performers by effect discriminator (`ClientEffect.kind`). An effect
        /// whose name is absent is `Unregistered` — never performed, always
        /// recorded.
        Performers: Map<string, ClientEffect -> unit>
        /// The policy gate, consulted BEFORE any performer runs. Takes the
        /// effect's discriminator so a host can allow `Navigate` and refuse
        /// `WriteToClipboard` without inspecting payloads.
        Gate: string -> bool
        /// The DESTINATION policy, consulted after `Gate` and before the
        /// performer, WITH the payload's destination.
        ///
        /// Additive to `Gate` rather than a replacement for it, because the two
        /// answer different questions and a host wants both: `Gate` is "does
        /// this host navigate at all", `Egress` is "does it navigate THERE".
        /// Collapsing them into one `string -> string -> bool` would let a
        /// host accidentally express the second while believing it had
        /// expressed the first.
        Egress: EgressPolicy
        /// Denial sink. Fired for every refusal, so a denied dispatch is
        /// observable rather than a silent nothing.
        OnDenied: EffectDenial -> unit
    }

module EffectRegistry =

    /// The empty registry: no performers, gate refuses everything, denials
    /// dropped. This is the DEFAULT a host starts from — a host that wires
    /// nothing performs nothing, which is the only defensible default for a
    /// loop whose whole premise is that the tree is untrusted.
    let denyAll: EffectRegistry =
        { Performers = Map.empty
          Gate = fun _ -> false
          Egress = EgressPolicy.denyNonLocal
          OnDenied = ignore }

    /// Register a performer for one effect discriminator (`ClientEffect.kind`:
    /// `"Navigate"`, `"WriteToClipboard"`, `"PushState"`, `"Focus"`,
    /// `"Download"`, `"ReadFileBody"`). Registering does NOT permit — the gate
    /// still decides. The two are separate on purpose: a host can register its
    /// whole capability set once and vary the policy per session.
    let register (name: string) (performer: ClientEffect -> unit) (registry: EffectRegistry) : EffectRegistry =
        { registry with
            Performers = Map.add name performer registry.Performers }

    /// Replace the policy gate.
    let withGate (gate: string -> bool) (registry: EffectRegistry) : EffectRegistry = { registry with Gate = gate }

    /// Replace the destination policy. The counterpart of `withGate` for the
    /// second question.
    let withEgress (policy: EgressPolicy) (registry: EffectRegistry) : EffectRegistry =
        { registry with Egress = policy }

    /// Declare an origin for a set of effect names. The one-line way to say
    /// "this host downloads from our CDN and navigates within our own site".
    let allowOrigin (origin: EgressOrigin) (effects: string list) (registry: EffectRegistry) : EffectRegistry =
        { registry with
            Egress = EgressPolicy.allowOrigin origin effects registry.Egress }

    /// Allow every REGISTERED effect, to every destination. Named, not default
    /// — reaching permissive is a deliberate act, and it still cannot reach an
    /// unregistered effect, because the vocabulary is closed by registration
    /// rather than by policy.
    ///
    /// It opens BOTH gates, which is the honest reading of the name: a
    /// `permissive` that quietly kept a deny-non-local egress policy would be a
    /// host that believes it permitted everything and did not.
    let permissive (registry: EffectRegistry) : EffectRegistry =
        withGate
            (fun _ -> true)
            { registry with
                Egress = EgressPolicy.permissive }

    /// Set the denial sink.
    let onDenied (sink: EffectDenial -> unit) (registry: EffectRegistry) : EffectRegistry =
        { registry with OnDenied = sink }

    /// The registered effect names, for host introspection.
    let registered (registry: EffectRegistry) : string list =
        registry.Performers |> Map.toList |> List.map fst

    /// Decide one effect: `None` permits it, `Some denial` declines it and says
    /// why. Pure — it performs nothing and records nothing, which is what lets
    /// the same decision serve `perform` (which acts on it) and a conformance
    /// harness (which compares it).
    ///
    /// The order is load-bearing at three points, and each one is a decision
    /// rather than an accident:
    ///
    ///   1. registration, then policy, then the performer — no performer
    ///      side-effect can precede a policy decision, because the performer is
    ///      never even looked up as a callable until both gates have spoken;
    ///   2. the DISCRIMINATOR gate before the DESTINATION policy — an effect
    ///      this host does not perform at all is refused before its payload is
    ///      parsed, so a malformed URL in an effect nobody permits produces the
    ///      refusal the host would expect rather than a parse-shaped one;
    ///   3. exactly one denial per dispatch. A refusal records the FIRST reason
    ///      it met, not every reason that would have applied — "the gate
    ///      refused it" and "the destination was undeclared" are answers to
    ///      different questions, and emitting both would make a log read as two
    ///      attempts.
    let decide (registry: EffectRegistry) (effect: ClientEffect) : EffectDenial option =
        let name = ClientEffect.kind effect

        match Map.tryFind name registry.Performers with
        | None -> Some(EffectDenial.Unregistered name)
        | Some _ ->
            if not (registry.Gate name) then
                Some(EffectDenial.GateRefused name)
            else
                let destination = ClientEffectDestination.destinationOf effect

                if EgressPolicy.permits registry.Egress name destination then
                    None
                else
                    Some(EffectDenial.DestinationRefused(name, EffectDestination.describe destination))

    let perform (registry: EffectRegistry) (effect: ClientEffect) : unit =
        match decide registry effect with
        | Some denial -> registry.OnDenied denial
        | None ->
            match Map.tryFind (ClientEffect.kind effect) registry.Performers with
            | Some performer -> performer effect
            // Unreachable: `decide` returns `None` only where a performer was
            // found. Matched rather than asserted because the alternative is an
            // exception in a loop whose whole premise is totality.
            | None -> ()

    /// Perform a step's effects in order and return the denials, in order.
    ///
    /// The list is what a caller needs and `perform` alone cannot give it: the
    /// sink is a side-effect a host wires for logging, and a conformance harness
    /// comparing two placements needs the refusals as a VALUE. One decision per
    /// effect either way — this is `decide` and `perform` composed, not a second
    /// walk that could drift from the first.
    let performAll (registry: EffectRegistry) (effects: ClientEffect list) : EffectDenial list =
        effects
        |> List.choose (fun effect ->
            match decide registry effect with
            | Some denial ->
                registry.OnDenied denial
                Some denial
            | None ->
                match Map.tryFind (ClientEffect.kind effect) registry.Performers with
                | Some performer -> performer effect
                | None -> ()

                None)
