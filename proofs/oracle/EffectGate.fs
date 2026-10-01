module EffectGate

let rec admitted : (Prims.string  ->  Prims.bool)  ->  Prims.list<Prims.string>  ->  Prims.bool = (fun ( gate  :  Prims.string  ->  Prims.bool ) ( xs  :  Prims.list<Prims.string> ) -> (match (xs) with
| [] -> begin
     true
     end
| (c)::rest -> begin
     ((gate c) && (admitted gate rest))
     end))

type contract<'v> = {ct_name : Prims.string; ct_holds : 'v  ->  Prims.bool}


let __proj__Mkcontract__item__ct_name = (fun ( projectee  :  contract<'v> ) -> (match (projectee) with
| {ct_name = ct_name; ct_holds = ct_holds} -> begin
     ct_name
     end))


let __proj__Mkcontract__item__ct_holds = (fun ( projectee  :  contract<'v> ) -> (match (projectee) with
| {ct_name = ct_name; ct_holds = ct_holds} -> begin
     ct_holds
     end))


let contract_reason = (fun ( c  :  contract<'v> ) -> (Prims.strcat "return-contract:" c.ct_name))


let check_return = (fun ( c  :  contract<'v> ) ( perf  :  'p  ->  'v  ->  Staging.res<'v> ) ( tok  :  'p ) ( args  :  'v ) -> (match ((perf tok args)) with
| Staging.RErr (reason) -> begin
     Staging.RErr (reason)
     end
| Staging.ROk (result) -> begin
      
if (c.ct_holds result) then begin
     Staging.ROk (result)
     end else begin
     Staging.RErr ((contract_reason c))
     end
     end))


let checked_by = (fun ( ct  :  'p  ->  Staging.opt<contract<'v>> ) ( perf  :  'p  ->  'v  ->  Staging.res<'v> ) ( tok  :  'p ) ( args  :  'v ) -> (match ((ct tok)) with
| Staging.ONone -> begin
     (perf tok args)
     end
| Staging.OSome (c) -> begin
     (check_return c perf tok args)
     end))




