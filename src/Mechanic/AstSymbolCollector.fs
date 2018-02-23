module Mechanic.AstSymbolCollector
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices.AstTraversal
open Mechanic.Utils

type Symbol =
    | Identificator of string
    | RecordField of string
    | TypeSymbol of string
module Symbol = 
    let get x = match x with |Identificator s -> s |RecordField s -> s |TypeSymbol s -> s
    let map f = function
        | Identificator s -> Identificator (f s)
        | RecordField s -> RecordField (f s)
        | TypeSymbol s -> TypeSymbol (f s)

type OpenDecl = { OpenName: string; Pos: Range.pos; Range: Range.range }
type SymbolUse = { SymbolName: Symbol; Range: Range.range }
type OpenDeclGroup = { Opens: list<string>; UsedSymbols: list<Symbol> }

let visitLongIdent (ident: LongIdent) =
    let names = String.concat "." [ for i in ident -> i.idText ]
    names

let visitPattern = function
    | SynPat.Named(SynPat.Wild(_), name, _, _, _) -> Some name.idText
    | SynPat.Named(_, name, _, _, _) -> Some name.idText
    | SynPat.LongIdent(LongIdentWithDots(ident, _), _, _, _, _, _) -> Some <| visitLongIdent ident
    | _ -> None

let rec getBind bindings =
    bindings |> Seq.map (fun binding ->
        let (Binding(_, _, _, _, _, _, _, pat, _, _, _, _)) = binding
        visitPattern pat)
    |> Seq.choose id |> Seq.toList

let getDefSymbols (tree: ParsedInput) =
    let mutable xs = []
    let getNamespace path =
        path |> List.choose (function
            | TraverseStep.ModuleOrNamespace(SynModuleOrNamespace(lId,_,_,_,_,_,_,_)) -> 
                Some (visitLongIdent lId)
            | TraverseStep.Module(SynModuleDecl.NestedModule(ComponentInfo(_,_,_,lId,_,_,_,_),_,_,_,_)) -> 
                Some (visitLongIdent lId)
            | TraverseStep.TypeDefn(SynTypeDefn.TypeDefn(ComponentInfo(_,_,_,lId,_,_,_,_),_,_,_)) -> 
                Some (visitLongIdent lId)
            | _ -> None
        ) |> List.rev |> String.concat "."
    let getTypeDefnFromPath path =
        path |> List.rev |> List.choose (function
            | TraverseStep.TypeDefn(t) -> Some t
            | _ -> None
        ) |> List.tryHead
    let getFieldsFromTypeDefn (SynTypeDefn.TypeDefn(_,repr,_,_)) =
        match repr with
        | SynTypeDefnRepr.Simple(simpleRepr,_) ->
            match simpleRepr with
            | SynTypeDefnSimpleRepr.Record(_,fields,_) -> 
                fields |> List.choose (function SynField.Field(_,_,ident,_type,_,_,_,_) -> ident |> Option.map (fun ident -> RecordField ident.idText))
            | SynTypeDefnSimpleRepr.Union(_,cases,_) ->
                cases |> List.map (function SynUnionCase.UnionCase(_,ident,_type,_,_,_) -> Identificator ident.idText)
            | _ -> []
        | _ -> []
    let visitor = { new AstVisitorBase<_>() with    
        //TODO: union fields
        override __.VisitExpr(_, subExprF, defF, e) =
            match e with
            | _ -> defF e
        override __.VisitBinding(path, defF, x) = 
            match path with
            | TraverseStep.Expr _ :: _ -> defF x
            | _ ->
                xs <- xs @ (getBind [x] |> List.map (fun s -> getNamespace path + "." + s |> Identificator)); defF x
        override __.VisitComponentInfo(path, _) =
            let fields = path |> getTypeDefnFromPath |> Option.map getFieldsFromTypeDefn |> Option.defaultValue []
            let symbolCons = 
                path |> getTypeDefnFromPath |> function 
                    | Some (SynTypeDefn.TypeDefn(_, SynTypeDefnRepr.ObjectModel _, _, _)) -> Identificator 
                    | _ -> TypeSymbol
            xs <- xs @ [getNamespace path |> symbolCons] @ (fields |> List.map (Symbol.map (fun s -> (getNamespace path |> Namespace.removeLastPart) + "." + s))); None
        }
    Traverse(tree, visitor) |> ignore
    //printfn "Defs: %A" xs    
    xs

let getUsedSymbols (tree: ParsedInput) =
    let mutable xs = []
    let visitor = { new AstVisitorBase<_>() with
        override __.VisitExpr(path, subExprF, defF, e) =
            match e with
            | SynExpr.Ident(id) -> xs <- (Identificator id.idText, id.idRange) :: xs; defF e
            | SynExpr.LongIdent(_, LongIdentWithDots(lId,_), _, r) -> xs <- (Identificator(visitLongIdent lId), r) :: xs; defF e
            | _ -> defF e
        override __.VisitTyped(synType, range) =
            match synType with
            | SynType.LongIdent(LongIdentWithDots(lId, _)) -> xs <- (TypeSymbol(visitLongIdent lId), range) :: xs; None
            | _ -> None
        override __.VisitRecordField(_path, _, ident, range) =
            ident |> Option.iter (fun (LongIdentWithDots(ident, _)) ->
                xs <- xs @ [RecordField(visitLongIdent ident), range])
            None
        }
    Traverse(tree, visitor) |> ignore
    //printfn "Uses: %A" xs    
    xs |> List.map (fun (x,r) -> {SymbolName = x; Range = r})

let getOpenDecls (tree: ParsedInput) =
    //TODO: open in module with scope
    let getUsesInRange range = getUsedSymbols tree |> List.filter (fun u -> Range.rangeContainsRange range u.Range);
    let mkOpenDecl xs uses = { Opens = xs; UsedSymbols  = uses }
    let getScope path =
        path |> List.choose (function
            | TraverseStep.ModuleOrNamespace(SynModuleOrNamespace(_,_,_,_,_,_,_,r))
            | TraverseStep.Module(SynModuleDecl.NestedModule(_,_,_,_,r)) -> Some r
            | _ -> None
        ) |> List.tryHead
    let getNamespace path =
        path |> List.choose (function
            | TraverseStep.ModuleOrNamespace(SynModuleOrNamespace(lId,_,isModule,_,_,_,_,_)) -> 
                Some (if isModule then visitLongIdent lId |> Namespace.removeLastPart else visitLongIdent lId)
            | _ -> None
        ) |> List.tryHead
    let getFullNamespace path =
        path |> List.choose (function
            | TraverseStep.ModuleOrNamespace(SynModuleOrNamespace(lId,_,_,_,_,_,_,_))
            | TraverseStep.Module(SynModuleDecl.NestedModule(ComponentInfo(_,_,_,lId,_,_,_,_),_,_,_,_)) -> Some (visitLongIdent lId)
            | _ -> None
        ) |> List.rev |> Namespace.joinByDot
    let openWithNamespace path x = getNamespace path |> Option.map (fun n -> Namespace.merge n x) |> Option.defaultValue x
    let openWithFullNamespace path x = getFullNamespace path |> fun n -> Namespace.merge n x
    let mutable xs = []
    let visitor = { new AstVisitorBase<_>() with
        override __.VisitExpr(_, subExprF, defF, e) =
            match e with | _ -> defF e
        override __.VisitModuleDecl(path, defF, d) =
            match d with
            | SynModuleDecl.Open(LongIdentWithDots(lId, _),r) -> xs <- ((visitLongIdent lId |> openWithNamespace path), r.Start, getScope path |> Option.get) :: xs; defF d
            | SynModuleDecl.NestedModule(ComponentInfo(_,_,_,lId,_,_,_,_),_,_,_,r) -> xs <- (visitLongIdent lId |> openWithFullNamespace path, r.Start, r) :: xs; defF d
            | _ -> defF d
        override __.VisitModuleOrNamespace(SynModuleOrNamespace(lId,_,isModule,_,_,_,_,r)) =
            let ident = visitLongIdent lId
            if isModule then xs <- (Namespace.removeLastPart ident, r.Start, r) :: xs 
            xs <- (ident, r.Start, r) :: xs
            None
        }
    Traverse(tree, visitor) |> ignore
    let opensAndUses = xs |> List.map (fun (x, pos, openR) -> { OpenName = x; Pos = pos; Range = openR }, getUsesInRange openR)
    let opensWithNoUse = opensAndUses |> List.filter (fun (_,uses) -> List.isEmpty uses)
    let usesWithOpens =
        opensAndUses |> List.collect (fun (openD, uses) -> uses |> List.map (fun u -> u, openD))
        |> List.groupBy (fun (u,_) -> u.SymbolName, u.Range) |> List.map (fun ((u,_), xs) -> 
            let opensWithRange = xs |> List.map snd 
            u, (opensWithRange |> List.sortBy (fun o -> o.Pos.Line, o.Pos.Column) |> List.map (fun o -> o.OpenName)))
    //printfn "UsesWithOpens: %A" usesWithOpens
    let r =
        let openGroups = 
            usesWithOpens 
            |> List.groupBy snd |> List.map (fun (opens,g) -> mkOpenDecl (List.rev opens) (g |> List.map fst))
        openGroups @ [opensWithNoUse |> List.map (fun (o,_) -> o.OpenName) |> fun x -> mkOpenDecl x []]
    //printfn "Opens: %A" r
    r
