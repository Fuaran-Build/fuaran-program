module Staging
type opt<'a> =
| ONone
| OSome of 'a


let uu___is_ONone = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| ONone -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_OSome = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| OSome (item) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__OSome__item__item = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| OSome (item) -> begin
     item
     end))

type res<'a> =
| ROk of 'a
| RErr of Prims.string


let uu___is_ROk = (fun ( projectee  :  res<'a> ) -> (match (projectee) with
| ROk (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ROk__item__value = (fun ( projectee  :  res<'a> ) -> (match (projectee) with
| ROk (value) -> begin
     value
     end))


let uu___is_RErr = (fun ( projectee  :  res<'a> ) -> (match (projectee) with
| RErr (reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RErr__item__reason = (fun ( projectee  :  res<'a> ) -> (match (projectee) with
| RErr (reason) -> begin
     reason
     end))


let rec app = (fun ( xs  :  Prims.list<'a> ) ( ys  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     ys
     end
| (x)::rest -> begin
     (x)::(app rest ys)
     end))


let rec rev = (fun ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     []
     end
| (x)::rest -> begin
     (app (rev rest) ((x)::[]))
     end))

type store<'t, 'b> = {st_tree : 't; st_bindings : 'b}


let __proj__Mkstore__item__st_tree = (fun ( projectee  :  store<'t, 'b> ) -> (match (projectee) with
| {st_tree = st_tree; st_bindings = st_bindings} -> begin
     st_tree
     end))


let __proj__Mkstore__item__st_bindings = (fun ( projectee  :  store<'t, 'b> ) -> (match (projectee) with
| {st_tree = st_tree; st_bindings = st_bindings} -> begin
     st_bindings
     end))

type server_effect<'v, 'o, 'q> =
| RunQuery of Prims.string * 'q
| ApplyOps of Prims.list<'o>
| HostCall of Prims.string * 'v * opt<Prims.string>
| EmitPatch of Prims.list<'o>
| Notify of Prims.string * 'v


let uu___is_RunQuery = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| RunQuery (name, query) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RunQuery__item__name = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| RunQuery (name, query) -> begin
     name
     end))


let __proj__RunQuery__item__query = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| RunQuery (name, query) -> begin
     query
     end))


let uu___is_ApplyOps = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| ApplyOps (ops) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ApplyOps__item__ops = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| ApplyOps (ops) -> begin
     ops
     end))


let uu___is_HostCall = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| HostCall (fn, args, into) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__HostCall__item__fn = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| HostCall (fn, args, into) -> begin
     fn
     end))


let __proj__HostCall__item__args = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| HostCall (fn, args, into) -> begin
     args
     end))


let __proj__HostCall__item__into = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| HostCall (fn, args, into) -> begin
     into
     end))


let uu___is_EmitPatch = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| EmitPatch (patch) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__EmitPatch__item__patch = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| EmitPatch (patch) -> begin
     patch
     end))


let uu___is_Notify = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| Notify (channel, payload) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Notify__item__channel = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| Notify (channel, payload) -> begin
     channel
     end))


let __proj__Notify__item__payload = (fun ( projectee  :  server_effect<'v, 'o, 'q> ) -> (match (projectee) with
| Notify (channel, payload) -> begin
     payload
     end))

type stage<'a, 'v, 'o, 'q> =
| SCompute of 'a
| SEffect of server_effect<'v, 'o, 'q>


let uu___is_SCompute = (fun ( projectee  :  stage<'a, 'v, 'o, 'q> ) -> (match (projectee) with
| SCompute (action) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__SCompute__item__action = (fun ( projectee  :  stage<'a, 'v, 'o, 'q> ) -> (match (projectee) with
| SCompute (action) -> begin
     action
     end))


let uu___is_SEffect = (fun ( projectee  :  stage<'a, 'v, 'o, 'q> ) -> (match (projectee) with
| SEffect (eff) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__SEffect__item__eff = (fun ( projectee  :  stage<'a, 'v, 'o, 'q> ) -> (match (projectee) with
| SEffect (eff) -> begin
     eff
     end))

type denial =
| Unregistered of Prims.string
| GateRefused of Prims.string


let uu___is_Unregistered : denial  ->  Prims.bool = (fun ( projectee  :  denial ) -> (match (projectee) with
| Unregistered (capability) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Unregistered__item__capability : denial  ->  Prims.string = (fun ( projectee  :  denial ) -> (match (projectee) with
| Unregistered (capability) -> begin
     capability
     end))


let uu___is_GateRefused : denial  ->  Prims.bool = (fun ( projectee  :  denial ) -> (match (projectee) with
| GateRefused (capability) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__GateRefused__item__capability : denial  ->  Prims.string = (fun ( projectee  :  denial ) -> (match (projectee) with
| GateRefused (capability) -> begin
     capability
     end))

type diagnostic<'d> =
| Bounded of 'd
| Denied of denial
| Failed of Prims.string * Prims.string
| PerformFailed of Prims.string * Prims.string


let uu___is_Bounded = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Bounded (inner) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Bounded__item__inner = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Bounded (inner) -> begin
     inner
     end))


let uu___is_Denied = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Denied (why) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Denied__item__why = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Denied (why) -> begin
     why
     end))


let uu___is_Failed = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Failed (capability, reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Failed__item__capability = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Failed (capability, reason) -> begin
     capability
     end))


let __proj__Failed__item__reason = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| Failed (capability, reason) -> begin
     reason
     end))


let uu___is_PerformFailed = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| PerformFailed (capability, reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__PerformFailed__item__capability = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| PerformFailed (capability, reason) -> begin
     capability
     end))


let __proj__PerformFailed__item__reason = (fun ( projectee  :  diagnostic<'d> ) -> (match (projectee) with
| PerformFailed (capability, reason) -> begin
     reason
     end))


let capability = (fun ( e  :  server_effect<'v, 'o, 'q> ) -> (match (e) with
| RunQuery (uu___, uu___1) -> begin
     "RunQuery"
     end
| ApplyOps (uu___) -> begin
     "ApplyOps"
     end
| HostCall (fn, uu___, uu___1) -> begin
     (Prims.strcat "host:" fn)
     end
| EmitPatch (uu___) -> begin
     "EmitPatch"
     end
| Notify (uu___, uu___1) -> begin
     "Notify"
     end))

type bounded_outcome<'b, 'eff, 'd> = {bo_store : 'b; bo_effects : Prims.list<'eff>; bo_diagnostics : Prims.list<'d>}


let __proj__Mkbounded_outcome__item__bo_store = (fun ( projectee  :  bounded_outcome<'b, 'eff, 'd> ) -> (match (projectee) with
| {bo_store = bo_store; bo_effects = bo_effects; bo_diagnostics = bo_diagnostics} -> begin
     bo_store
     end))


let __proj__Mkbounded_outcome__item__bo_effects = (fun ( projectee  :  bounded_outcome<'b, 'eff, 'd> ) -> (match (projectee) with
| {bo_store = bo_store; bo_effects = bo_effects; bo_diagnostics = bo_diagnostics} -> begin
     bo_effects
     end))


let __proj__Mkbounded_outcome__item__bo_diagnostics = (fun ( projectee  :  bounded_outcome<'b, 'eff, 'd> ) -> (match (projectee) with
| {bo_store = bo_store; bo_effects = bo_effects; bo_diagnostics = bo_diagnostics} -> begin
     bo_diagnostics
     end))

type witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> = {w_compute : Prims.string  ->  'a  ->  'b  ->  bounded_outcome<'b, 'eff, 'd>; w_query : Prims.string  ->  'q  ->  'b  ->  res<'b>; w_apply : 'o  ->  't  ->  res<'t>; w_assign : Prims.string  ->  'v  ->  'b  ->  'b; w_is_reserved : Prims.string  ->  Prims.bool; w_reserved_prefix : Prims.string}


let __proj__Mkwitness__item__w_compute = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_compute
     end))


let __proj__Mkwitness__item__w_query = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_query
     end))


let __proj__Mkwitness__item__w_apply = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_apply
     end))


let __proj__Mkwitness__item__w_assign = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_assign
     end))


let __proj__Mkwitness__item__w_is_reserved = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_is_reserved
     end))


let __proj__Mkwitness__item__w_reserved_prefix = (fun ( projectee  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) -> (match (projectee) with
| {w_compute = w_compute; w_query = w_query; w_apply = w_apply; w_assign = w_assign; w_is_reserved = w_is_reserved; w_reserved_prefix = w_reserved_prefix} -> begin
     w_reserved_prefix
     end))

type registry<'v, 'o, 'q, 'p> = {r_gate : Prims.string  ->  Prims.bool; r_policy : server_effect<'v, 'o, 'q>  ->  opt<Prims.string>; r_lookup : Prims.string  ->  opt<'p>; r_perf : 'p  ->  'v  ->  res<'v>; r_op_perform : opt<('o  ->  ('p * 'v))>}


let __proj__Mkregistry__item__r_gate = (fun ( projectee  :  registry<'v, 'o, 'q, 'p> ) -> (match (projectee) with
| {r_gate = r_gate; r_policy = r_policy; r_lookup = r_lookup; r_perf = r_perf; r_op_perform = r_op_perform} -> begin
     r_gate
     end))


let __proj__Mkregistry__item__r_policy = (fun ( projectee  :  registry<'v, 'o, 'q, 'p> ) -> (match (projectee) with
| {r_gate = r_gate; r_policy = r_policy; r_lookup = r_lookup; r_perf = r_perf; r_op_perform = r_op_perform} -> begin
     r_policy
     end))


let __proj__Mkregistry__item__r_lookup = (fun ( projectee  :  registry<'v, 'o, 'q, 'p> ) -> (match (projectee) with
| {r_gate = r_gate; r_policy = r_policy; r_lookup = r_lookup; r_perf = r_perf; r_op_perform = r_op_perform} -> begin
     r_lookup
     end))


let __proj__Mkregistry__item__r_perf = (fun ( projectee  :  registry<'v, 'o, 'q, 'p> ) -> (match (projectee) with
| {r_gate = r_gate; r_policy = r_policy; r_lookup = r_lookup; r_perf = r_perf; r_op_perform = r_op_perform} -> begin
     r_perf
     end))


let __proj__Mkregistry__item__r_op_perform = (fun ( projectee  :  registry<'v, 'o, 'q, 'p> ) -> (match (projectee) with
| {r_gate = r_gate; r_policy = r_policy; r_lookup = r_lookup; r_perf = r_perf; r_op_perform = r_op_perform} -> begin
     r_op_perform
     end))

type staged_call<'v, 'p> = {sc_capability : Prims.string; sc_performer : 'p; sc_args : 'v; sc_into : opt<Prims.string>}


let __proj__Mkstaged_call__item__sc_capability = (fun ( projectee  :  staged_call<'v, 'p> ) -> (match (projectee) with
| {sc_capability = sc_capability; sc_performer = sc_performer; sc_args = sc_args; sc_into = sc_into} -> begin
     sc_capability
     end))


let __proj__Mkstaged_call__item__sc_performer = (fun ( projectee  :  staged_call<'v, 'p> ) -> (match (projectee) with
| {sc_capability = sc_capability; sc_performer = sc_performer; sc_args = sc_args; sc_into = sc_into} -> begin
     sc_performer
     end))


let __proj__Mkstaged_call__item__sc_args = (fun ( projectee  :  staged_call<'v, 'p> ) -> (match (projectee) with
| {sc_capability = sc_capability; sc_performer = sc_performer; sc_args = sc_args; sc_into = sc_into} -> begin
     sc_args
     end))


let __proj__Mkstaged_call__item__sc_into = (fun ( projectee  :  staged_call<'v, 'p> ) -> (match (projectee) with
| {sc_capability = sc_capability; sc_performer = sc_performer; sc_args = sc_args; sc_into = sc_into} -> begin
     sc_into
     end))

type accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> = {ac_store : store<'t, 'b>; ac_halted : Prims.bool; ac_performed : Prims.list<Prims.string>; ac_externally : Prims.list<Prims.string>; ac_staged : Prims.list<staged_call<'v, 'p>>; ac_patches : Prims.list<'o>; ac_notifications : Prims.list<(Prims.string * 'v)>; ac_client_effects : Prims.list<'eff>; ac_diagnostics : Prims.list<diagnostic<'d>>}


let __proj__Mkaccumulator__item__ac_store = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_store
     end))


let __proj__Mkaccumulator__item__ac_halted = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_halted
     end))


let __proj__Mkaccumulator__item__ac_performed = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_performed
     end))


let __proj__Mkaccumulator__item__ac_externally = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_externally
     end))


let __proj__Mkaccumulator__item__ac_staged = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_staged
     end))


let __proj__Mkaccumulator__item__ac_patches = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_patches
     end))


let __proj__Mkaccumulator__item__ac_notifications = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_notifications
     end))


let __proj__Mkaccumulator__item__ac_client_effects = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_client_effects
     end))


let __proj__Mkaccumulator__item__ac_diagnostics = (fun ( projectee  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (projectee) with
| {ac_store = ac_store; ac_halted = ac_halted; ac_performed = ac_performed; ac_externally = ac_externally; ac_staged = ac_staged; ac_patches = ac_patches; ac_notifications = ac_notifications; ac_client_effects = ac_client_effects; ac_diagnostics = ac_diagnostics} -> begin
     ac_diagnostics
     end))

type outcome<'t, 'b, 'v, 'o, 'eff, 'd> = {oc_store : store<'t, 'b>; oc_committed : Prims.bool; oc_performed : Prims.list<Prims.string>; oc_patches : Prims.list<'o>; oc_notifications : Prims.list<(Prims.string * 'v)>; oc_client_effects : Prims.list<'eff>; oc_diagnostics : Prims.list<diagnostic<'d>>}


let __proj__Mkoutcome__item__oc_store = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_store
     end))


let __proj__Mkoutcome__item__oc_committed = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_committed
     end))


let __proj__Mkoutcome__item__oc_performed = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_performed
     end))


let __proj__Mkoutcome__item__oc_patches = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_patches
     end))


let __proj__Mkoutcome__item__oc_notifications = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_notifications
     end))


let __proj__Mkoutcome__item__oc_client_effects = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_client_effects
     end))


let __proj__Mkoutcome__item__oc_diagnostics = (fun ( projectee  :  outcome<'t, 'b, 'v, 'o, 'eff, 'd> ) -> (match (projectee) with
| {oc_store = oc_store; oc_committed = oc_committed; oc_performed = oc_performed; oc_patches = oc_patches; oc_notifications = oc_notifications; oc_client_effects = oc_client_effects; oc_diagnostics = oc_diagnostics} -> begin
     oc_diagnostics
     end))


let halt = (fun ( cap  :  Prims.string ) ( reason  :  Prims.string ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> {ac_store = acc.ac_store; ac_halted = true; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = (Failed (cap, reason))::acc.ac_diagnostics})


let deny = (fun ( why  :  denial ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> {ac_store = acc.ac_store; ac_halted = true; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = (Denied (why))::acc.ac_diagnostics})


let rec apply_all = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( ops  :  Prims.list<'o> ) ( tree  :  't ) -> (match (ops) with
| [] -> begin
     ROk (tree)
     end
| (op)::rest -> begin
     (match ((w.w_apply op tree)) with
| RErr (code) -> begin
     RErr (code)
     end
| ROk (tree') -> begin
     (apply_all w rest tree')
     end)
     end))


let rec map_bounded = (fun ( ds  :  Prims.list<'d> ) -> (match (ds) with
| [] -> begin
     []
     end
| (x)::rest -> begin
     (Bounded (x))::(map_bounded rest)
     end))


let reserved_slot = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( into  :  opt<Prims.string> ) -> (match (into) with
| OSome (key) -> begin
     (w.w_is_reserved key)
     end
| ONone -> begin
     false
     end))


let rec stage_ops = (fun ( cap  :  Prims.string ) ( stage1  :  'o  ->  ('p * 'v) ) ( ops  :  Prims.list<'o> ) ( staged  :  Prims.list<staged_call<'v, 'p>> ) -> (match (ops) with
| [] -> begin
     staged
     end
| (op)::rest -> begin
     (

let uu___ = (stage1 op)
in (match (uu___) with
| (tok, args) -> begin
     (stage_ops cap stage1 rest (({sc_capability = cap; sc_performer = tok; sc_args = args; sc_into = ONone})::staged))
     end))
     end))


let plan_effect = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( reg  :  registry<'v, 'o, 'q, 'p> ) ( e  :  server_effect<'v, 'o, 'q> ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (

let cap = (capability e)
in  
if (not ((reg.r_gate cap))) then begin
     (deny (GateRefused (cap)) acc)
     end else begin
     (match ((reg.r_policy e)) with
| OSome (defect) -> begin
     (halt cap defect acc)
     end
| ONone -> begin
     (

let performed = {ac_store = acc.ac_store; ac_halted = acc.ac_halted; ac_performed = (cap)::acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = acc.ac_diagnostics}
in (match (e) with
| RunQuery (name, query) -> begin
     (match ((w.w_query name query performed.ac_store.st_bindings)) with
| RErr (kind) -> begin
     (halt cap kind acc)
     end
| ROk (bindings) -> begin
     {ac_store = (

let uu___ = performed.ac_store
in {st_tree = uu___.st_tree; st_bindings = bindings}); ac_halted = performed.ac_halted; ac_performed = performed.ac_performed; ac_externally = performed.ac_externally; ac_staged = performed.ac_staged; ac_patches = performed.ac_patches; ac_notifications = performed.ac_notifications; ac_client_effects = performed.ac_client_effects; ac_diagnostics = performed.ac_diagnostics}
     end)
     end
| ApplyOps (ops) -> begin
     (match ((apply_all w ops performed.ac_store.st_tree)) with
| RErr (code) -> begin
     (halt cap code acc)
     end
| ROk (tree) -> begin
     (match (reg.r_op_perform) with
| ONone -> begin
     {ac_store = (

let uu___ = performed.ac_store
in {st_tree = tree; st_bindings = uu___.st_bindings}); ac_halted = performed.ac_halted; ac_performed = performed.ac_performed; ac_externally = performed.ac_externally; ac_staged = performed.ac_staged; ac_patches = performed.ac_patches; ac_notifications = performed.ac_notifications; ac_client_effects = performed.ac_client_effects; ac_diagnostics = performed.ac_diagnostics}
     end
| OSome (stage1) -> begin
     {ac_store = (

let uu___ = acc.ac_store
in {st_tree = tree; st_bindings = uu___.st_bindings}); ac_halted = acc.ac_halted; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = (stage_ops cap stage1 ops acc.ac_staged); ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = acc.ac_diagnostics}
     end)
     end)
     end
| HostCall (fn, args, into) -> begin
     (match ((reg.r_lookup fn)) with
| ONone -> begin
     (deny (Unregistered (cap)) acc)
     end
| OSome (performer) -> begin
      
if (reserved_slot w into) then begin
     (halt cap (Prims.strcat "landing slot is under the host-reserved \'" (Prims.strcat w.w_reserved_prefix "\' namespace")) acc)
     end else begin
     {ac_store = acc.ac_store; ac_halted = acc.ac_halted; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = ({sc_capability = cap; sc_performer = performer; sc_args = args; sc_into = into})::acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = acc.ac_diagnostics}
     end
     end)
     end
| EmitPatch (ops) -> begin
     {ac_store = performed.ac_store; ac_halted = performed.ac_halted; ac_performed = performed.ac_performed; ac_externally = performed.ac_externally; ac_staged = performed.ac_staged; ac_patches = (app (rev ops) performed.ac_patches); ac_notifications = performed.ac_notifications; ac_client_effects = performed.ac_client_effects; ac_diagnostics = performed.ac_diagnostics}
     end
| Notify (channel, payload) -> begin
     {ac_store = performed.ac_store; ac_halted = performed.ac_halted; ac_performed = performed.ac_performed; ac_externally = performed.ac_externally; ac_staged = performed.ac_staged; ac_patches = performed.ac_patches; ac_notifications = (((channel), (payload)))::performed.ac_notifications; ac_client_effects = performed.ac_client_effects; ac_diagnostics = performed.ac_diagnostics}
     end))
     end)
     end))


let plan_stage = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( reg  :  registry<'v, 'o, 'q, 'p> ) ( node_id  :  Prims.string ) ( s  :  stage<'a, 'v, 'o, 'q> ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (s) with
| SCompute (action) -> begin
     (

let out = (w.w_compute node_id action acc.ac_store.st_bindings)
in {ac_store = (

let uu___ = acc.ac_store
in {st_tree = uu___.st_tree; st_bindings = out.bo_store}); ac_halted = acc.ac_halted; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = (app (rev out.bo_effects) acc.ac_client_effects); ac_diagnostics = (app (map_bounded (rev out.bo_diagnostics)) acc.ac_diagnostics)})
     end
| SEffect (e) -> begin
     (plan_effect w reg e acc)
     end))


let rec plan = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( reg  :  registry<'v, 'o, 'q, 'p> ) ( node_id  :  Prims.string ) ( stages  :  Prims.list<stage<'a, 'v, 'o, 'q>> ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (stages) with
| [] -> begin
     acc
     end
| (s)::rest -> begin
     (plan w reg node_id rest ( 
if acc.ac_halted then begin
     acc
     end else begin
     (plan_stage w reg node_id s acc)
     end))
     end))


let rec perform = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( reg  :  registry<'v, 'o, 'q, 'p> ) ( staged  :  Prims.list<staged_call<'v, 'p>> ) ( acc  :  accumulator<'t, 'b, 'v, 'o, 'eff, 'd, 'p> ) -> (match (staged) with
| [] -> begin
     acc
     end
| (call)::rest -> begin
     (match ((reg.r_perf call.sc_performer call.sc_args)) with
| RErr (reason) -> begin
     {ac_store = acc.ac_store; ac_halted = true; ac_performed = acc.ac_performed; ac_externally = acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = (PerformFailed (call.sc_capability, reason))::acc.ac_diagnostics}
     end
| ROk (result) -> begin
     (

let recorded = {ac_store = acc.ac_store; ac_halted = acc.ac_halted; ac_performed = acc.ac_performed; ac_externally = (call.sc_capability)::acc.ac_externally; ac_staged = acc.ac_staged; ac_patches = acc.ac_patches; ac_notifications = acc.ac_notifications; ac_client_effects = acc.ac_client_effects; ac_diagnostics = acc.ac_diagnostics}
in (

let landed = (match (call.sc_into) with
| ONone -> begin
     recorded
     end
| OSome (key) -> begin
     {ac_store = (

let uu___ = recorded.ac_store
in {st_tree = uu___.st_tree; st_bindings = (w.w_assign key result recorded.ac_store.st_bindings)}); ac_halted = recorded.ac_halted; ac_performed = recorded.ac_performed; ac_externally = recorded.ac_externally; ac_staged = recorded.ac_staged; ac_patches = recorded.ac_patches; ac_notifications = recorded.ac_notifications; ac_client_effects = recorded.ac_client_effects; ac_diagnostics = recorded.ac_diagnostics}
     end)
in (perform w reg rest landed)))
     end)
     end))


let start = (fun ( s  :  store<'t, 'b> ) -> {ac_store = s; ac_halted = false; ac_performed = []; ac_externally = []; ac_staged = []; ac_patches = []; ac_notifications = []; ac_client_effects = []; ac_diagnostics = []})


let run = (fun ( w  :  witness<'t, 'b, 'v, 'o, 'q, 'a, 'eff, 'd> ) ( reg  :  registry<'v, 'o, 'q, 'p> ) ( node_id  :  Prims.string ) ( stages  :  Prims.list<stage<'a, 'v, 'o, 'q>> ) ( s  :  store<'t, 'b> ) -> (

let planned = (plan w reg node_id stages (start s))
in (

let final =  
if planned.ac_halted then begin
     planned
     end else begin
     (perform w reg (rev planned.ac_staged) planned)
     end
in  
if final.ac_halted then begin
     {oc_store = s; oc_committed = false; oc_performed = (rev final.ac_externally); oc_patches = []; oc_notifications = []; oc_client_effects = []; oc_diagnostics = (rev final.ac_diagnostics)}
     end else begin
     {oc_store = final.ac_store; oc_committed = true; oc_performed = (app (rev final.ac_performed) (rev final.ac_externally)); oc_patches = (rev final.ac_patches); oc_notifications = (rev final.ac_notifications); oc_client_effects = (rev final.ac_client_effects); oc_diagnostics = (rev final.ac_diagnostics)}
     end)))




