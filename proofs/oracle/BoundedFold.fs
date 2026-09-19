module BoundedFold
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


let rec app = (fun ( xs  :  Prims.list<'a> ) ( ys  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     ys
     end
| (x)::rest -> begin
     (x)::(app rest ys)
     end))


type key = Prims.string


type store<'v> = Prims.list<(key * 'v)>


let rec lookup = (fun ( s  :  store<'v> ) ( k  :  key ) -> (match (s) with
| [] -> begin
     ONone
     end
| ((k', x))::rest -> begin
      
if (Prims.op_Equals k' k) then begin
     OSome (x)
     end else begin
     (lookup rest k)
     end
     end))


let rec write = (fun ( s  :  store<'v> ) ( k  :  key ) ( x  :  'v ) -> (match (s) with
| [] -> begin
     (((k), (x)))::[]
     end
| ((k', y))::rest -> begin
      
if (Prims.op_Equals k' k) then begin
     (((k), (x)))::rest
     end else begin
     (((k'), (y)))::(write rest k x)
     end
     end))

type res<'v> =
| JResolved of 'v
| JNotResolved
| JErrored of Prims.string
| JI18nUnresolved of Prims.string


let uu___is_JResolved = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JResolved (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__JResolved__item__value = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JResolved (value) -> begin
     value
     end))


let uu___is_JNotResolved = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JNotResolved -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_JErrored = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JErrored (message) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__JErrored__item__message = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JErrored (message) -> begin
     message
     end))


let uu___is_JI18nUnresolved = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JI18nUnresolved (i18n_key) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__JI18nUnresolved__item__i18n_key = (fun ( projectee  :  res<'v> ) -> (match (projectee) with
| JI18nUnresolved (i18n_key) -> begin
     i18n_key
     end))

type res_text =
| SResolved of opt<Prims.string>
| SNotResolved
| SErrored of Prims.string
| SI18nUnresolved of Prims.string


let uu___is_SResolved : res_text  ->  Prims.bool = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SResolved (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__SResolved__item__value : res_text  ->  opt<Prims.string> = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SResolved (value) -> begin
     value
     end))


let uu___is_SNotResolved : res_text  ->  Prims.bool = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SNotResolved -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_SErrored : res_text  ->  Prims.bool = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SErrored (message) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__SErrored__item__message : res_text  ->  Prims.string = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SErrored (message) -> begin
     message
     end))


let uu___is_SI18nUnresolved : res_text  ->  Prims.bool = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SI18nUnresolved (i18n_key) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__SI18nUnresolved__item__i18n_key : res_text  ->  Prims.string = (fun ( projectee  :  res_text ) -> (match (projectee) with
| SI18nUnresolved (i18n_key) -> begin
     i18n_key
     end))

type text_source<'b> =
| TLiteral of Prims.string
| TBound of 'b
| TI18n of Prims.string * 'b


let uu___is_TLiteral = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TLiteral (text) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__TLiteral__item__text = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TLiteral (text) -> begin
     text
     end))


let uu___is_TBound = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TBound (binding) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__TBound__item__binding = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TBound (binding) -> begin
     binding
     end))


let uu___is_TI18n = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TI18n (i18n_key, args) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__TI18n__item__i18n_key = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TI18n (i18n_key, args) -> begin
     i18n_key
     end))


let __proj__TI18n__item__args = (fun ( projectee  :  text_source<'b> ) -> (match (projectee) with
| TI18n (i18n_key, args) -> begin
     args
     end))

type nav_target =
| NSelf
| NBlank


let uu___is_NSelf : nav_target  ->  Prims.bool = (fun ( projectee  :  nav_target ) -> (match (projectee) with
| NSelf -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_NBlank : nav_target  ->  Prims.bool = (fun ( projectee  :  nav_target ) -> (match (projectee) with
| NBlank -> begin
     true
     end
| uu___ -> begin
     false
     end))

type file_encoding =
| FText
| FBase64
| FDataUrl


let uu___is_FText : file_encoding  ->  Prims.bool = (fun ( projectee  :  file_encoding ) -> (match (projectee) with
| FText -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_FBase64 : file_encoding  ->  Prims.bool = (fun ( projectee  :  file_encoding ) -> (match (projectee) with
| FBase64 -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_FDataUrl : file_encoding  ->  Prims.bool = (fun ( projectee  :  file_encoding ) -> (match (projectee) with
| FDataUrl -> begin
     true
     end
| uu___ -> begin
     false
     end))

type call_target =
| CTState of Prims.string
| CTQuery of Prims.string


let uu___is_CTState : call_target  ->  Prims.bool = (fun ( projectee  :  call_target ) -> (match (projectee) with
| CTState (state_key) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__CTState__item__state_key : call_target  ->  Prims.string = (fun ( projectee  :  call_target ) -> (match (projectee) with
| CTState (state_key) -> begin
     state_key
     end))


let uu___is_CTQuery : call_target  ->  Prims.bool = (fun ( projectee  :  call_target ) -> (match (projectee) with
| CTQuery (query_name) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__CTQuery__item__query_name : call_target  ->  Prims.string = (fun ( projectee  :  call_target ) -> (match (projectee) with
| CTQuery (query_name) -> begin
     query_name
     end))

type client_effect =
| ENavigate of Prims.string * nav_target
| EClipboard of Prims.string
| EPrint
| EFocus of Prims.string
| EReadFileBody of Prims.string * Prims.string


let uu___is_ENavigate : client_effect  ->  Prims.bool = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| ENavigate (route, target) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ENavigate__item__route : client_effect  ->  Prims.string = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| ENavigate (route, target) -> begin
     route
     end))


let __proj__ENavigate__item__target : client_effect  ->  nav_target = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| ENavigate (route, target) -> begin
     target
     end))


let uu___is_EClipboard : client_effect  ->  Prims.bool = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EClipboard (text) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__EClipboard__item__text : client_effect  ->  Prims.string = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EClipboard (text) -> begin
     text
     end))


let uu___is_EPrint : client_effect  ->  Prims.bool = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EPrint -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_EFocus : client_effect  ->  Prims.bool = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EFocus (node_id) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__EFocus__item__node_id : client_effect  ->  Prims.string = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EFocus (node_id) -> begin
     node_id
     end))


let uu___is_EReadFileBody : client_effect  ->  Prims.bool = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EReadFileBody (node_id, encoding) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__EReadFileBody__item__node_id : client_effect  ->  Prims.string = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EReadFileBody (node_id, encoding) -> begin
     node_id
     end))


let __proj__EReadFileBody__item__encoding : client_effect  ->  Prims.string = (fun ( projectee  :  client_effect ) -> (match (projectee) with
| EReadFileBody (node_id, encoding) -> begin
     encoding
     end))

type diagnostic =
| DUnsupported of Prims.string * Prims.string
| DRefused of Prims.string * Prims.string * Prims.string


let uu___is_DUnsupported : diagnostic  ->  Prims.bool = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DUnsupported (node_id, action_name) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__DUnsupported__item__node_id : diagnostic  ->  Prims.string = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DUnsupported (node_id, action_name) -> begin
     node_id
     end))


let __proj__DUnsupported__item__action_name : diagnostic  ->  Prims.string = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DUnsupported (node_id, action_name) -> begin
     action_name
     end))


let uu___is_DRefused : diagnostic  ->  Prims.bool = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DRefused (node_id, action_name, reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__DRefused__item__node_id : diagnostic  ->  Prims.string = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DRefused (node_id, action_name, reason) -> begin
     node_id
     end))


let __proj__DRefused__item__action_name : diagnostic  ->  Prims.string = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DRefused (node_id, action_name, reason) -> begin
     action_name
     end))


let __proj__DRefused__item__reason : diagnostic  ->  Prims.string = (fun ( projectee  :  diagnostic ) -> (match (projectee) with
| DRefused (node_id, action_name, reason) -> begin
     reason
     end))

type action<'v, 'b, 'k> =
| AChain of Prims.list<action<'v, 'b, 'k>>
| AWriteToClipboard of text_source<'b>
| ADispatch of 'k
| AInvoke of Prims.string * 'b
| AReadFileBody of Prims.string * 'k * file_encoding * 'k
| ACall of Prims.string * 'k * opt<call_target>
| ANavigate of text_source<'b> * nav_target
| ACommitLocal of Prims.string
| ANotify of Prims.string * 'b
| ASetState of key * opt<'v> * opt<'b>
| AAiTool of Prims.string * 'b
| APrint
| AConfirm of text_source<'b> * action<'v, 'b, 'k> * opt<action<'v, 'b, 'k>>
| AFocus of Prims.string


let uu___is_AChain = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AChain (ops) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AChain__item__ops = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AChain (ops) -> begin
     ops
     end))


let uu___is_AWriteToClipboard = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AWriteToClipboard (text) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AWriteToClipboard__item__text = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AWriteToClipboard (text) -> begin
     text
     end))


let uu___is_ADispatch = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ADispatch (msg) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ADispatch__item__msg = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ADispatch (msg) -> begin
     msg
     end))


let uu___is_AInvoke = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AInvoke (capability_id, args) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AInvoke__item__capability_id = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AInvoke (capability_id, args) -> begin
     capability_id
     end))


let __proj__AInvoke__item__args = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AInvoke (capability_id, args) -> begin
     args
     end))


let uu___is_AReadFileBody = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AReadFileBody (file_ref, file_handle, encoding, on_read) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AReadFileBody__item__file_ref = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AReadFileBody (file_ref, file_handle, encoding, on_read) -> begin
     file_ref
     end))


let __proj__AReadFileBody__item__file_handle = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AReadFileBody (file_ref, file_handle, encoding, on_read) -> begin
     file_handle
     end))


let __proj__AReadFileBody__item__encoding = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AReadFileBody (file_ref, file_handle, encoding, on_read) -> begin
     encoding
     end))


let __proj__AReadFileBody__item__on_read = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AReadFileBody (file_ref, file_handle, encoding, on_read) -> begin
     on_read
     end))


let uu___is_ACall = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACall (endpoint, on_result, into) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ACall__item__endpoint = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACall (endpoint, on_result, into) -> begin
     endpoint
     end))


let __proj__ACall__item__on_result = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACall (endpoint, on_result, into) -> begin
     on_result
     end))


let __proj__ACall__item__into = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACall (endpoint, on_result, into) -> begin
     into
     end))


let uu___is_ANavigate = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANavigate (route, target) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ANavigate__item__route = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANavigate (route, target) -> begin
     route
     end))


let __proj__ANavigate__item__target = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANavigate (route, target) -> begin
     target
     end))


let uu___is_ACommitLocal = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACommitLocal (node_id) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ACommitLocal__item__node_id = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ACommitLocal (node_id) -> begin
     node_id
     end))


let uu___is_ANotify = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANotify (channel, payload) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ANotify__item__channel = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANotify (channel, payload) -> begin
     channel
     end))


let __proj__ANotify__item__payload = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ANotify (channel, payload) -> begin
     payload
     end))


let uu___is_ASetState = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ASetState (state_key, value, value_from) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ASetState__item__state_key = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ASetState (state_key, value, value_from) -> begin
     state_key
     end))


let __proj__ASetState__item__value = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ASetState (state_key, value, value_from) -> begin
     value
     end))


let __proj__ASetState__item__value_from = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| ASetState (state_key, value, value_from) -> begin
     value_from
     end))


let uu___is_AAiTool = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AAiTool (tool_name, args) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AAiTool__item__tool_name = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AAiTool (tool_name, args) -> begin
     tool_name
     end))


let __proj__AAiTool__item__args = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AAiTool (tool_name, args) -> begin
     args
     end))


let uu___is_APrint = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| APrint -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_AConfirm = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AConfirm (prompt, on_confirm, on_cancel) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AConfirm__item__prompt = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AConfirm (prompt, on_confirm, on_cancel) -> begin
     prompt
     end))


let __proj__AConfirm__item__on_confirm = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AConfirm (prompt, on_confirm, on_cancel) -> begin
     on_confirm
     end))


let __proj__AConfirm__item__on_cancel = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AConfirm (prompt, on_confirm, on_cancel) -> begin
     on_cancel
     end))


let uu___is_AFocus = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AFocus (node_id) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__AFocus__item__node_id = (fun ( projectee  :  action<'v, 'b, 'k> ) -> (match (projectee) with
| AFocus (node_id) -> begin
     node_id
     end))

type outcome<'v> = {o_store : store<'v>; o_effects : Prims.list<client_effect>; o_diagnostics : Prims.list<diagnostic>}


let __proj__Mkoutcome__item__o_store = (fun ( projectee  :  outcome<'v> ) -> (match (projectee) with
| {o_store = o_store; o_effects = o_effects; o_diagnostics = o_diagnostics} -> begin
     o_store
     end))


let __proj__Mkoutcome__item__o_effects = (fun ( projectee  :  outcome<'v> ) -> (match (projectee) with
| {o_store = o_store; o_effects = o_effects; o_diagnostics = o_diagnostics} -> begin
     o_effects
     end))


let __proj__Mkoutcome__item__o_diagnostics = (fun ( projectee  :  outcome<'v> ) -> (match (projectee) with
| {o_store = o_store; o_effects = o_effects; o_diagnostics = o_diagnostics} -> begin
     o_diagnostics
     end))

type handler_answer<'v, 'p> = {h_store : store<'v>; h_effects : Prims.list<client_effect>; h_diagnostics : Prims.list<diagnostic>; h_placement : 'p}


let __proj__Mkhandler_answer__item__h_store = (fun ( projectee  :  handler_answer<'v, 'p> ) -> (match (projectee) with
| {h_store = h_store; h_effects = h_effects; h_diagnostics = h_diagnostics; h_placement = h_placement} -> begin
     h_store
     end))


let __proj__Mkhandler_answer__item__h_effects = (fun ( projectee  :  handler_answer<'v, 'p> ) -> (match (projectee) with
| {h_store = h_store; h_effects = h_effects; h_diagnostics = h_diagnostics; h_placement = h_placement} -> begin
     h_effects
     end))


let __proj__Mkhandler_answer__item__h_diagnostics = (fun ( projectee  :  handler_answer<'v, 'p> ) -> (match (projectee) with
| {h_store = h_store; h_effects = h_effects; h_diagnostics = h_diagnostics; h_placement = h_placement} -> begin
     h_diagnostics
     end))


let __proj__Mkhandler_answer__item__h_placement = (fun ( projectee  :  handler_answer<'v, 'p> ) -> (match (projectee) with
| {h_store = h_store; h_effects = h_effects; h_diagnostics = h_diagnostics; h_placement = h_placement} -> begin
     h_placement
     end))

type arm<'v, 'p> = {answer : Prims.string  ->  Prims.string  ->  store<'v>  ->  'p  ->  opt<handler_answer<'v, 'p>>}


let __proj__Mkarm__item__answer = (fun ( projectee  :  arm<'v, 'p> ) -> (match (projectee) with
| {answer = answer} -> begin
     answer
     end))


let inert_arm = (fun ( uu___  :  unit ) -> {answer = (fun ( uu___1  :  Prims.string ) ( uu___2  :  Prims.string ) ( uu___3  :  store<'v> ) ( uu___4  :  'p ) -> ONone)})

type axioms<'v, 'b> = {is_reserved : key  ->  Prims.bool; reserved_prefix : Prims.string; resolve_jval : store<'v>  ->  'b  ->  res<'v>; resolve_scalar : store<'v>  ->  'b  ->  res_text; i18n_has : store<'v>  ->  Prims.string  ->  Prims.bool; resolve_text : store<'v>  ->  text_source<'b>  ->  Prims.string; sanitize_url : Prims.string  ->  opt<Prims.string>; route_path : Prims.string  ->  Prims.string}


let __proj__Mkaxioms__item__is_reserved = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     is_reserved
     end))


let __proj__Mkaxioms__item__reserved_prefix = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     reserved_prefix
     end))


let __proj__Mkaxioms__item__resolve_jval = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     resolve_jval
     end))


let __proj__Mkaxioms__item__resolve_scalar = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     resolve_scalar
     end))


let __proj__Mkaxioms__item__i18n_has = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     i18n_has
     end))


let __proj__Mkaxioms__item__resolve_text = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     resolve_text
     end))


let __proj__Mkaxioms__item__sanitize_url = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     sanitize_url
     end))


let __proj__Mkaxioms__item__route_path = (fun ( projectee  :  axioms<'v, 'b> ) -> (match (projectee) with
| {is_reserved = is_reserved; reserved_prefix = reserved_prefix; resolve_jval = resolve_jval; resolve_scalar = resolve_scalar; i18n_has = i18n_has; resolve_text = resolve_text; sanitize_url = sanitize_url; route_path = route_path} -> begin
     route_path
     end))


let describe = (fun ( ax  :  axioms<'v, 'b> ) ( a  :  action<'v, 'b, 'k> ) -> (match (a) with
| ADispatch (uu___) -> begin
     "Dispatch"
     end
| ACall (endpoint, uu___, uu___1) -> begin
     (Prims.strcat "Call(" (Prims.strcat endpoint ")"))
     end
| ANotify (channel, uu___) -> begin
     (Prims.strcat "Notify(" (Prims.strcat channel ")"))
     end
| ANavigate (route, uu___) -> begin
     (match (route) with
| TLiteral (literal) -> begin
     (Prims.strcat "Navigate(" (Prims.strcat (ax.route_path literal) ")"))
     end
| uu___1 -> begin
     "Navigate(<bound>)"
     end)
     end
| ASetState (k1, uu___, uu___1) -> begin
     (Prims.strcat "SetState(" (Prims.strcat k1 ")"))
     end
| AAiTool (tool_name, uu___) -> begin
     (Prims.strcat "AiTool(" (Prims.strcat tool_name ")"))
     end
| AChain (uu___) -> begin
     "Chain"
     end
| ACommitLocal (node_id) -> begin
     (Prims.strcat "CommitLocal(" (Prims.strcat node_id ")"))
     end
| AWriteToClipboard (uu___) -> begin
     "WriteToClipboard"
     end
| APrint -> begin
     "Print"
     end
| AConfirm (uu___, uu___1, uu___2) -> begin
     "Confirm"
     end
| AFocus (node_id) -> begin
     (Prims.strcat "Focus(" (Prims.strcat node_id ")"))
     end
| AReadFileBody (uu___, uu___1, uu___2, uu___3) -> begin
     "ReadFileBody"
     end
| AInvoke (capability_id, uu___) -> begin
     (Prims.strcat "Invoke(" (Prims.strcat capability_id ")"))
     end))


let store_only = (fun ( s  :  store<'v> ) -> {o_store = s; o_effects = []; o_diagnostics = []})


let no_op = (fun ( ax  :  axioms<'v, 'b> ) ( node_id  :  Prims.string ) ( a  :  action<'v, 'b, 'k> ) ( s  :  store<'v> ) -> {o_store = s; o_effects = []; o_diagnostics = (DUnsupported (node_id, (describe ax a)))::[]})


let refused = (fun ( ax  :  axioms<'v, 'b> ) ( node_id  :  Prims.string ) ( a  :  action<'v, 'b, 'k> ) ( reason  :  Prims.string ) ( s  :  store<'v> ) -> {o_store = s; o_effects = []; o_diagnostics = (DRefused (node_id, (describe ax a), reason))::[]})

type jval_payload<'v> =
| POk of opt<'v>
| PErr of Prims.string


let uu___is_POk = (fun ( projectee  :  jval_payload<'v> ) -> (match (projectee) with
| POk (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__POk__item__value = (fun ( projectee  :  jval_payload<'v> ) -> (match (projectee) with
| POk (value) -> begin
     value
     end))


let uu___is_PErr = (fun ( projectee  :  jval_payload<'v> ) -> (match (projectee) with
| PErr (message) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__PErr__item__message = (fun ( projectee  :  jval_payload<'v> ) -> (match (projectee) with
| PErr (message) -> begin
     message
     end))

type text_result =
| ROk of Prims.string
| RErr of Prims.string


let uu___is_ROk : text_result  ->  Prims.bool = (fun ( projectee  :  text_result ) -> (match (projectee) with
| ROk (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ROk__item__value : text_result  ->  Prims.string = (fun ( projectee  :  text_result ) -> (match (projectee) with
| ROk (value) -> begin
     value
     end))


let uu___is_RErr : text_result  ->  Prims.bool = (fun ( projectee  :  text_result ) -> (match (projectee) with
| RErr (message) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RErr__item__message : text_result  ->  Prims.string = (fun ( projectee  :  text_result ) -> (match (projectee) with
| RErr (message) -> begin
     message
     end))


let unresolved_i18n : Prims.string  ->  Prims.string = (fun ( k  :  Prims.string ) -> (Prims.strcat "unresolved i18n key \'" (Prims.strcat k "\'")))


let rec run = (fun ( ax  :  axioms<'v, 'b> ) ( ar  :  arm<'v, 'p> ) ( node_id  :  Prims.string ) ( a  :  action<'v, 'b, 'k> ) ( s  :  store<'v> ) ( pl  :  'p ) -> (match (a) with
| ASetState (state_key, value, value_from) -> begin
     (( 
if (ax.is_reserved state_key) then begin
     (refused ax node_id a (Prims.strcat "State key \'" (Prims.strcat state_key (Prims.strcat "\' is under the host-reserved \'" (Prims.strcat ax.reserved_prefix "\' namespace")))) s)
     end else begin
     (

let payload = (match (value_from) with
| OSome (binding) -> begin
     (match ((ax.resolve_jval s binding)) with
| JResolved (jv) -> begin
     POk (OSome (jv))
     end
| JNotResolved -> begin
     POk (ONone)
     end
| JErrored (m) -> begin
     PErr (m)
     end
| JI18nUnresolved (kk) -> begin
     PErr ((unresolved_i18n kk))
     end)
     end
| ONone -> begin
     POk (value)
     end)
in (match (payload) with
| POk (OSome (jv)) -> begin
     (store_only (write s state_key jv))
     end
| POk (ONone) -> begin
     (refused ax node_id a "valueFrom did not resolve to a value — no write performed" s)
     end
| PErr (m) -> begin
     (refused ax node_id a (Prims.strcat "valueFrom errored: " (Prims.strcat m " — no write performed")) s)
     end))
     end), (pl))
     end
| ANavigate (route, target) -> begin
     (

let resolved = (match (route) with
| TLiteral (literal) -> begin
     ROk (literal)
     end
| TBound (binding) -> begin
     (match ((ax.resolve_scalar s binding)) with
| SResolved (OSome (value)) -> begin
     ROk (value)
     end
| SResolved (ONone) -> begin
     RErr ("the route binding resolved to no value")
     end
| SNotResolved -> begin
     RErr ("the route binding did not resolve to a value")
     end
| SErrored (m) -> begin
     RErr (m)
     end
| SI18nUnresolved (kk) -> begin
     RErr ((unresolved_i18n kk))
     end)
     end
| TI18n (kk, uu___) -> begin
      
if (ax.i18n_has s kk) then begin
     ROk ((ax.resolve_text s route))
     end else begin
     RErr ((unresolved_i18n kk))
     end
     end)
in (((match (resolved) with
| RErr (reason) -> begin
     (refused ax node_id a (Prims.strcat reason " — nothing was navigated to") s)
     end
| ROk (r) -> begin
     (match ((ax.sanitize_url r)) with
| OSome (safe) -> begin
     {o_store = s; o_effects = (ENavigate (safe, target))::[]; o_diagnostics = []}
     end
| ONone -> begin
     (refused ax node_id a "route is not a safe URL" s)
     end)
     end)), (pl)))
     end
| AWriteToClipboard (text) -> begin
     (

let payload = (match (text) with
| TLiteral (literal) -> begin
     ROk (literal)
     end
| TBound (binding) -> begin
     (match ((ax.resolve_scalar s binding)) with
| SResolved (OSome (value)) -> begin
     ROk (value)
     end
| SResolved (ONone) -> begin
     ROk ("")
     end
| SNotResolved -> begin
     RErr ("the payload binding did not resolve to a value")
     end
| SErrored (m) -> begin
     RErr (m)
     end
| SI18nUnresolved (kk) -> begin
     RErr ((unresolved_i18n kk))
     end)
     end
| TI18n (kk, uu___) -> begin
      
if (ax.i18n_has s kk) then begin
     ROk ((ax.resolve_text s text))
     end else begin
     RErr ((unresolved_i18n kk))
     end
     end)
in (((match (payload) with
| ROk (value) -> begin
     {o_store = s; o_effects = (EClipboard (value))::[]; o_diagnostics = []}
     end
| RErr (reason) -> begin
     (refused ax node_id a (Prims.strcat reason " — nothing was written to the clipboard") s)
     end)), (pl)))
     end
| APrint -> begin
     (({o_store = s; o_effects = (EPrint)::[]; o_diagnostics = []}), (pl))
     end
| AFocus (target_node_id) -> begin
     (({o_store = s; o_effects = (EFocus (target_node_id))::[]; o_diagnostics = []}), (pl))
     end
| AReadFileBody (uu___, uu___1, encoding, uu___2) -> begin
     (

let enc = (match (encoding) with
| FText -> begin
     "Text"
     end
| FBase64 -> begin
     "Base64"
     end
| FDataUrl -> begin
     "DataUrl"
     end)
in (({o_store = s; o_effects = (EReadFileBody (node_id, enc))::[]; o_diagnostics = []}), (pl)))
     end
| AChain (ops) -> begin
     (run_many ax ar node_id ops s pl)
     end
| AConfirm (uu___, uu___1, uu___2) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| ANotify (uu___, uu___1) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| AAiTool (uu___, uu___1) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| AInvoke (uu___, uu___1) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| ADispatch (uu___) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| ACommitLocal (uu___) -> begin
     (((no_op ax node_id a s)), (pl))
     end
| ACall (uu___, uu___1, OSome (uu___2)) -> begin
     (((refused ax node_id a "the call declares a result target; a handler declares where its own results land" s)), (pl))
     end
| ACall (endpoint, uu___, ONone) -> begin
     (match ((ar.answer node_id endpoint s pl)) with
| ONone -> begin
     (((no_op ax node_id a s)), (pl))
     end
| OSome (ans) -> begin
     (({o_store = ans.h_store; o_effects = ans.h_effects; o_diagnostics = ans.h_diagnostics}), (ans.h_placement))
     end)
     end))
and run_many = (fun ( ax  :  axioms<'v, 'b> ) ( ar  :  arm<'v, 'p> ) ( node_id  :  Prims.string ) ( ops  :  Prims.list<action<'v, 'b, 'k>> ) ( s  :  store<'v> ) ( pl  :  'p ) -> (match (ops) with
| [] -> begin
     (((store_only s)), (pl))
     end
| (x)::rest -> begin
     (

let uu___ = (run ax ar node_id x s pl)
in (match (uu___) with
| (o1, p1) -> begin
     (

let uu___1 = (run_many ax ar node_id rest o1.o_store p1)
in (match (uu___1) with
| (o2, p2) -> begin
     (({o_store = o2.o_store; o_effects = (app o1.o_effects o2.o_effects); o_diagnostics = (app o1.o_diagnostics o2.o_diagnostics)}), (p2))
     end))
     end))
     end))




