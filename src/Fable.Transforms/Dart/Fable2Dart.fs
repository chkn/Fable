module rec Fable.Transforms.Fable2Dart

open System.Collections.Generic
open Fable
open Fable.AST
open Fable.AST.Dart
open Fable.Transforms.AST
open Fable.Transforms.Dart.Replacements.Util

type ReturnStrategy =
    | Return
    | ReturnVoid
    | Assign of Expression
    | Target of Ident
    | Capture

type CapturedExpr = Expression option

type ArgsInfo =
    | CallInfo of Fable.CallInfo
    | NoCallInfo of args: Fable.Expr list

type ITailCallOpportunity =
    abstract Label: string
    abstract Args: string list
    abstract IsRecursiveRef: Fable.Expr -> bool

type UsedNames =
  { RootScope: HashSet<string>
    DeclarationScopes: HashSet<string>
    CurrentDeclarationScope: HashSet<string> }

type Context =
  { File: Fable.File
    UsedNames: UsedNames
    DecisionTargets: (Fable.Ident list * Fable.Expr) list
    TailCallOpportunity: ITailCallOpportunity option
    MemberGenericParams: Fable.GenericParam list
    OptimizeTailCall: unit -> unit
    ConstIdents: Set<string> }

type MemberKind =
    | ClassConstructor
    | NonAttached of funcName: string
    | Attached of isStatic: bool

type IDartCompiler =
    inherit Compiler
    abstract GetAllImports: unit -> Import list
    abstract GetImportIdent: Context * selector: string * path: string * typ: Fable.Type * ?range: SourceLocation -> Ident
    abstract TransformType: Context * Fable.Type -> Type
    abstract Transform: Context * ReturnStrategy * Fable.Expr -> Statement list * CapturedExpr
    abstract TransformFunction: Context * string option * Fable.Ident list * Fable.Expr -> Ident list * Statement list * Type
    abstract WarnOnlyOnce: string * ?values: obj[] * ?range: SourceLocation -> unit
    abstract ErrorOnlyOnce: string * ?values: obj[] * ?range: SourceLocation -> unit

module Util =

    let (|TransformType|) (com: IDartCompiler) ctx e =
        com.TransformType(ctx, e)

    let (|Function|_|) = function
        | Fable.Lambda(arg, body, _) -> Some([arg], body)
        | Fable.Delegate(args, body, _) -> Some(args, body)
        | _ -> None

    let (|Lets|_|) = function
        | Fable.Let(ident, value, body) -> Some([ident, value], body)
        | Fable.LetRec(bindings, body) -> Some(bindings, body)
        | _ -> None

    let makeTypeRef ident genArgs =
        TypeReference(ident, genArgs)

    let makeTypeRefFromName typeName genArgs =
        let ident = makeIdent MetaType typeName
        makeTypeRef ident genArgs

    let libValue (com: IDartCompiler) ctx t moduleName memberName =
        com.GetImportIdent(ctx, memberName, getLibPath com moduleName, t)

    let libTypeRef (com: IDartCompiler) ctx moduleName memberName genArgs =
        let ident = libValue com ctx Fable.MetaType moduleName memberName
        makeTypeRef ident genArgs

    let libCall (com: IDartCompiler) ctx t moduleName memberName (args: Expression list) =
        let fn = com.GetImportIdent(ctx, memberName, getLibPath com moduleName, Fable.Any)
        Expression.invocationExpression(fn.Expr, args, transformType com ctx t)

    let extLibCall (com: IDartCompiler) ctx t modulePath memberName (args: Expression list) =
        let fn = com.GetImportIdent(ctx, memberName, modulePath, Fable.Any)
        Expression.invocationExpression(fn.Expr, args, transformType com ctx t)

    let discardUnitArg (args: Fable.Ident list) =
        match args with
        | [] -> []
        | [unitArg] when unitArg.Type = Fable.Unit -> []
        | [thisArg; unitArg] when thisArg.IsThisArgument && unitArg.Type = Fable.Unit -> [thisArg]
        | args -> args

    let addErrorAndReturnNull (com: Compiler) (range: SourceLocation option) (error: string) =
        addError com [] range error
        NullLiteral Dynamic |> Literal

    let numType kind = Fable.Number(kind, Fable.NumberInfo.Empty)

    let namedArg name expr: CallArg = Some name, expr

    let unnamedArg expr: CallArg = None, expr

    let unnamedArgs exprs: CallArg list = List.map unnamedArg exprs

    let makeIdent typ name =
        { Name = name; Type = typ; ImportModule = None }

    let makeReturnBlock expr =
        [Statement.returnStatement expr]

    let makeImmutableListExpr com ctx typ values: Expression =
        let typ = transformType com ctx typ
        let isConst, values =
            if List.forall (isConstExpr ctx) values then true, List.map removeConst values
            else false, values
        Expression.listLiteral(values, typ, isConst)

    let makeMutableListExpr com ctx typ values: Expression =
        let typ = transformType com ctx typ
        Expression.listLiteral(values, typ)

    let tryGetEntityIdent (com: IDartCompiler) ctx ent =
        Dart.Replacements.tryEntityRef com ent
        |> Option.bind (fun entRef ->
            match com.Transform(ctx, Capture, entRef) with
            | [], Some(IdentExpression ident) -> Some ident
            | _ -> addError com [] None $"Unexpected, entity ref for {ent.FullName} is not an identifer"; None)

    let getEntityIdent (com: IDartCompiler) ctx (ent: Fable.Entity) =
        match tryGetEntityIdent com ctx ent with
        | Some ident -> ident
        | None ->
            addError com [] None $"Cannot find reference for {ent.FullName}"
            makeIdent MetaType ent.DisplayName

    let transformDeclaredType (com: IDartCompiler) ctx (entRef: Fable.EntityRef) genArgs =
        let ent = com.GetEntity(entRef)
        // TODO: Discard measure types
        let genArgs = genArgs |> List.map (transformType com ctx)
        TypeReference(getEntityIdent com ctx ent, genArgs)

    let get t left memberName =
        PropertyAccess(left, memberName, t, isConst=false)

    let getExpr t left expr =
        IndexExpression(left, expr, t)

    let getUnionCaseName (uci: Fable.UnionCase) =
        match uci.CompiledName with Some cname -> cname | None -> uci.Name
    
    let getUnionExprTag expr =
        get Integer expr "tag"

    let getUnionExprFields expr =
        get (List Dynamic) expr "fields"

    let getUniqueNameInRootScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name)
            || ctx.UsedNames.DeclarationScopes.Contains(name))
        ctx.UsedNames.RootScope.Add(name) |> ignore
        name

    let getUniqueNameInDeclarationScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name) || ctx.UsedNames.CurrentDeclarationScope.Contains(name))
        ctx.UsedNames.CurrentDeclarationScope.Add(name) |> ignore
        name

    type NamedTailCallOpportunity(_com: IDartCompiler, ctx, name, args: Fable.Ident list) =
        // Capture the current argument values to prevent delayed references from getting corrupted,
        // for that we use block-scoped ES2015 variable declarations. See #681, #1859
        let argIds = discardUnitArg args |> List.map (fun arg ->
            getUniqueNameInDeclarationScope ctx (arg.Name + "_mut"))
        interface ITailCallOpportunity with
            member _.Label = name
            member _.Args = argIds
            member _.IsRecursiveRef(e) =
                match e with Fable.IdentExpr id -> name = id.Name | _ -> false

    let getDecisionTarget (ctx: Context) targetIndex =
        match List.tryItem targetIndex ctx.DecisionTargets with
        | None -> failwithf $"Cannot find DecisionTree target %i{targetIndex}"
        | Some(idents, target) -> idents, target

    let isConditionalStament ctx guard thenExpr elseExpr =
        isStatement ctx guard
        || isStatement ctx thenExpr
        || isStatement ctx elseExpr

    let isStatement ctx (e: Fable.Expr) =
        match e with
        | Fable.Unresolved _
        | Fable.Import _  | Fable.IdentExpr _ -> false

        | Fable.Test(e,_,_) | Fable.TypeCast(e,_) -> isStatement ctx e
        | Fable.Get(e, kind, _, _) ->
            match kind with
            | Fable.ListHead | Fable.ListTail | Fable.OptionValue | Fable.TupleIndex _ | Fable.UnionTag
            | Fable.UnionField _ | Fable.FieldGet _ -> isStatement ctx e
            | Fable.ExprGet e2 -> isStatement ctx e || isStatement ctx e2

        // Closures cannot be statements because they create their own scope
        | Fable.Lambda _ | Fable.Delegate _ | Fable.ObjectExpr _ -> false

        | Fable.Value(v,_) ->
            match v with
            | Fable.UnitConstant _ -> true
            | Fable.ThisValue _ | Fable.BaseValue _
            | Fable.TypeInfo _ | Fable.Null _
            | Fable.BoolConstant _ | Fable.CharConstant _ | Fable.StringConstant _
            | Fable.NumberConstant _ | Fable.RegexConstant _ -> false

            | Fable.NewRecord(e,_,_)
            | Fable.NewAnonymousRecord(e,_,_)
            | Fable.NewUnion(e,_,_,_)
            | Fable.StringTemplate(_,_,e)
            | Fable.NewTuple(e,_) -> List.exists (isStatement ctx) e
            | Fable.NewArray(kind,_,_) ->
                match kind with
                | Fable.ArrayValues e -> List.exists (isStatement ctx) e
                | Fable.ArrayAlloc e
                | Fable.ArrayFrom e -> isStatement ctx e
            | Fable.NewOption(Some e,_,_) -> isStatement ctx e
            | Fable.NewOption(None,_,_) -> false
            | Fable.NewList(Some(e1,e2),_) -> isStatement ctx e1 || isStatement ctx e2
            | Fable.NewList(None,_) -> false

        | Fable.CurriedApply(callee, args, _, _) -> callee::(discardSingleUnitArg args) |> List.exists (isStatement ctx)
        | Fable.Call(e1, info, _, _) -> e1 :: (Option.toList info.ThisArg) @ (discardSingleUnitArg info.Args) |> List.exists (isStatement ctx)
        | Fable.Operation(kind, _, _) ->
            match kind with
            | Fable.Unary(_, operand) -> isStatement ctx operand
            | Fable.Binary(_, left, right) -> isStatement ctx left || isStatement ctx right
            | Fable.Logical(_, left, right) -> isStatement ctx left || isStatement ctx right

        | Fable.Emit(i,_,_) ->
            i.IsStatement
            || (Option.toList i.CallInfo.ThisArg) @ (discardSingleUnitArg i.CallInfo.Args) |> List.exists (isStatement ctx)

        | Fable.Set _
        | Fable.Let _
        | Fable.LetRec _
        | Fable.Sequential _
        | Fable.TryCatch _
        | Fable.ForLoop _
        | Fable.WhileLoop _ -> true

        | Fable.Extended(kind, _) ->
            match kind with
            | Fable.RegionStart _ -> true
            | Fable.Throw(Some e, _) -> isStatement ctx e
            | Fable.Throw(None, _)
            | Fable.Debugger _
            | Fable.Curry _ -> false

        | Fable.DecisionTree(e, targets) ->
            // We should also check if one target is duplicated
            List.length targets > 2
            || isStatement { ctx with DecisionTargets = targets } e
            || List.exists (snd >> (isStatement ctx)) targets

        | Fable.DecisionTreeSuccess(targetIndex,_, _) ->
            getDecisionTarget ctx targetIndex
            |> snd |> isStatement ctx

        | Fable.IfThenElse(guard,thenExpr,elseExpr,_) ->
            elseExpr.Type = Fable.Unit || isConditionalStament ctx guard thenExpr elseExpr

    let isInt64OrLess = function
        | Fable.Number(DartInt, _) -> true
        | _ -> false

    // Binary operatios should be const if the operands are, but if necessary let's fold constants binary ops in FableTransforms
    let isConstExpr (ctx: Context) = function
        | CommentedExpression(_, expr) -> isConstExpr ctx expr
        | IdentExpression ident -> Option.isSome ident.ImportModule || Set.contains ident.Name ctx.ConstIdents
        | PropertyAccess(_,_,_,isConst)
        | InvocationExpression(_,_,_,_,isConst) -> isConst
        | BinaryExpression(_,left,right,_) -> isConstExpr ctx left && isConstExpr ctx right
        | Literal value ->
            match value with
            | ListLiteral(_,_,isConst) -> isConst
            | IntegerLiteral _
            | DoubleLiteral _
            | BooleanLiteral _
            | StringLiteral _
            | NullLiteral _ -> true
        | _ -> false

    // Dart linter complaints if we have too many "const"
    let removeConst = function
        | InvocationExpression(e, g, a, t, _isConst) -> InvocationExpression(e, g, a, t, false)
        | Literal value as e ->
            match value with
            | ListLiteral(values, typ, _isConst) -> ListLiteral(values, typ, false) |> Literal
            | _ -> e
        | e -> e

    let getVarKind ctx isMutable value =
        if isMutable then Var, value
        elif isConstExpr ctx value then Const, removeConst value
        else Final, value

    let assign (_range: SourceLocation option) left right =
        AssignmentExpression(left, AssignEqual, right)

    /// Immediately Invoked Function Expression
    let iife (_com: IDartCompiler) _ctx t (body: Statement list) =
        let fn = Expression.anonymousFunction([], body, t)
        Expression.invocationExpression(fn, t)

    let optimizeTailCall (com: IDartCompiler) (ctx: Context) _range (tc: ITailCallOpportunity) args =
        let rec checkCrossRefs tempVars allArgs = function
            | [] -> tempVars
            | (argId, arg: Fable.Expr)::rest ->
                let found = allArgs |> List.exists (FableTransforms.deepExists (function
                    | Fable.IdentExpr i -> argId = i.Name
                    | _ -> false))
                let tempVars =
                    if found then
                        let tempVar = getUniqueNameInDeclarationScope ctx (argId + "_tmp")
                        let tempVar = makeTypedIdent arg.Type tempVar
                        Map.add argId tempVar tempVars
                    else tempVars
                checkCrossRefs tempVars allArgs rest

        ctx.OptimizeTailCall()
        let zippedArgs = List.zip tc.Args args
        let tempVars = checkCrossRefs Map.empty args zippedArgs
        let tempVarReplacements = tempVars |> Map.map (fun _ v -> makeIdentExpr v.Name)

        // First declare temp variables
        let statements1 =
            tempVars |> Seq.mapToList (fun (KeyValue(argId, tempVar)) ->
                let tempVar = transformIdent com ctx tempVar
                let argId = makeIdent tempVar.Type argId |> Expression.identExpression
                Statement.variableDeclaration(tempVar, value=argId))

        // Then assign argument expressions to the original argument identifiers
        // See https://github.com/fable-compiler/Fable/issues/1368#issuecomment-434142713
        let statements2 =
            zippedArgs |> List.collect (fun (argId, arg) ->
                let arg = FableTransforms.replaceValues tempVarReplacements arg
                let argId = transformIdentWith com ctx arg.Type argId |> Expression.identExpression
                let statements, arg = transformAndCaptureExpr com ctx arg
                statements @ [assign None argId arg |> ExpressionStatement])

        statements1 @ statements2 @ [Statement.continueStatement(tc.Label)]

    let discardSingleUnitArg = function
        | [Fable.Value(Fable.UnitConstant,_)] -> []
        | args -> args

    let transformCallArgs (com: IDartCompiler) ctx (r: SourceLocation option) (info: ArgsInfo) =
        let namedParamsInfo, thisArg, args =
            match info with
            | NoCallInfo args -> None, None, args
            | CallInfo({ CallMemberInfo = None } as i) -> None, i.ThisArg, i.Args
            | CallInfo({ CallMemberInfo = Some mi } as info) ->
                let addUnnammedParamsWarning() =
                    "NamedParams cannot be used with unnamed parameters"
                    |> addWarning com [] r

                let mutable i = -1
                (None, List.concat mi.CurriedParameterGroups) ||> List.fold (fun acc p ->
                    i <- i + 1
                    match acc with
                    | Some(namedIndex, names) ->
                        match p.Name with
                        | Some name -> Some(namedIndex, name::names)
                        | None -> addUnnammedParamsWarning(); None
                    | None when p.IsNamed ->
                        match p.Name with
                        | Some name ->
                            let namedIndex = i
                            Some(namedIndex, [name])
                        | None -> addUnnammedParamsWarning(); None
                    | None -> None)
                |> function
                    | None -> None, info.ThisArg, info.Args
                    | Some(index, names) ->
                        let namedParamsInfo = {| Index = index; Parameters = List.rev names |}
                        Some namedParamsInfo, info.ThisArg, info.Args

        let unnamedArgs, namedArgs =
            match namedParamsInfo with
            | None -> args, []
            | Some i when i.Index > List.length args -> args, []
            | Some i ->
                let args, namedValues = List.splitAt i.Index args
                let namedValuesLen = List.length namedValues
                if List.length i.Parameters < namedValuesLen then
                    "NamedParams detected but more arguments present than param names"
                    |> addWarning com [] r
                    args, []
                else
                    let namedKeys = List.take namedValuesLen i.Parameters
                    let namedArgs =
                        List.zip namedKeys namedValues
                        |> List.choose (function
                            | k, Fable.Value(Fable.NewOption(value,_, _),_) -> value |> Option.map (fun v -> k, v)
                            | k, v -> Some(k, v))
                        |> List.map (fun (k, v) -> Some k, transformAndCaptureExpr com ctx v)
                    args, namedArgs

        let unnamedArgs = discardSingleUnitArg unnamedArgs
        let unnamedArgs =
            (Option.toList thisArg @ unnamedArgs)
            |> List.map (fun arg -> None, transformAndCaptureExpr com ctx arg)

        let keys, args = unnamedArgs @ namedArgs |> List.unzip
        let statements, args = combineStatementsAndExprs com ctx args
        statements, List.zip keys args |> List.map (function Some k, a -> namedArg k a | None, a -> unnamedArg a)

    let resolveExpr strategy expr: Statement list * CapturedExpr =
        match strategy with
        | ReturnVoid -> [ExpressionStatement expr], None
        | Return -> [ReturnStatement expr], None
        | Assign left -> [assign None left expr |> ExpressionStatement], None
        | Target left -> [assign None (IdentExpression left) expr |> ExpressionStatement], None
        | Capture -> [], Some expr

    let combineCapturedExprs com ctx (capturedExprs: (Statement list * CapturedExpr) list): Statement list * Expression list =
        let extractExpression mayHaveSideEffect (statements, capturedExpr: CapturedExpr) =
            match capturedExpr with
            | Some expr ->
                if (not mayHaveSideEffect) || isConstExpr ctx expr then
                    statements, expr
                else
                    let ident = getUniqueNameInDeclarationScope ctx "tmp" |> makeIdent expr.Type
                    let varDecl = Statement.variableDeclaration(ident, Final, expr)
                    statements @ [varDecl], ident.Expr
            | _ -> statements, ignoreExpr com ctx None

        let _, statements, exprs =
            ((false, [], []), List.rev capturedExprs)
            ||> List.fold (fun (mayHaveSideEffect, accStatements, accExprs) statements ->
                let mayHaveSideEffect = mayHaveSideEffect || not(List.isEmpty accStatements)
                let statements, expr = extractExpression mayHaveSideEffect statements
                mayHaveSideEffect, statements @ accStatements, expr::accExprs
            )
        statements, exprs

    let combineStatementsAndExprs com ctx (statementsAndExpr: (Statement list * Expression) list): Statement list * Expression list =
        statementsAndExpr |> List.map (fun (statements, expr) -> statements, Some expr) |> combineCapturedExprs com ctx

    let combineCalleeAndArgStatements _com ctx calleeStatements argStatements (callee: Expression) =
        if List.isEmpty argStatements then
            calleeStatements, callee
        elif isConstExpr ctx callee then
            calleeStatements @ argStatements, callee
        else
            let ident = getUniqueNameInDeclarationScope ctx "tmp" |> makeIdent callee.Type
            let varDecl = Statement.variableDeclaration(ident, Final, callee)
            calleeStatements @ [varDecl] @ argStatements, ident.Expr

    let transformExprsAndResolve com ctx returnStrategy exprs transformExprs =
        List.map (transform com ctx Capture) exprs
        |> combineCapturedExprs com ctx
        |> fun (statements, exprs) ->
            let statements2, capturedExpr = transformExprs exprs |> resolveExpr returnStrategy
            statements @ statements2, capturedExpr

    let transformExprAndResolve com ctx returnStrategy expr transformExpr =
        let statements, expr = transformAndCaptureExpr com ctx expr
        let statements2, capturedExpr = transformExpr expr |> resolveExpr returnStrategy
        statements @ statements2, capturedExpr

    let transformExprsAndResolve2 com ctx returnStrategy expr0 expr1 transformExprs =
        List.map (transform com ctx Capture) [expr0; expr1]
        |> combineCapturedExprs com ctx
        |> fun (statements, exprs) ->
            let statements2, capturedExpr = transformExprs exprs[0] exprs[1] |> resolveExpr returnStrategy
            statements @ statements2, capturedExpr

    let transformExprsAndResolve3 com ctx returnStrategy expr0 expr1 expr2 transformExprs =
        List.map (transform com ctx Capture) [expr0; expr1; expr2]
        |> combineCapturedExprs com ctx
        |> fun (statements, exprs) ->
            let statements2, capturedExpr = transformExprs exprs[0] exprs[1] exprs[2] |> resolveExpr returnStrategy
            statements @ statements2, capturedExpr

    let ignoreExpr com ctx expr =
        Option.toList expr |> libCall com ctx Fable.Unit "Types" "ignore"

    let getFsListTypeIdent com ctx =
        libValue com ctx Fable.MetaType "List" "FsList"

    let getTupleTypeIdent (com: IDartCompiler) ctx args =
        com.GetImportIdent(ctx, $"Tuple{List.length args}", "package:tuple/tuple.dart", Fable.MetaType)

    let transformType (com: IDartCompiler) (ctx: Context) (t: Fable.Type) =
        match t with
        | Fable.Measure _
        | Fable.Any -> Dynamic
        | Fable.Unit -> Void
        | Fable.MetaType -> MetaType
        | Fable.Boolean -> Boolean
        | Fable.String -> String
        | Fable.Char -> Integer
        | Fable.Number(kind, _) ->
            match kind with
            | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 | Int64 | UInt64 -> Integer
            | Float32 | Float64 -> Double
            | Decimal | BigInt | NativeInt | UNativeInt -> Dynamic // TODO
        | Fable.Option(genArg, _isStruct) ->
            match genArg with
            | Fable.Option _ -> com.ErrorOnlyOnce("Nested options are not supported"); Dynamic
            | TransformType com ctx genArg -> Nullable genArg
        | Fable.Array(TransformType com ctx genArg, _) -> List genArg
        | Fable.List(TransformType com ctx genArg) ->
            TypeReference(getFsListTypeIdent com ctx, [genArg])
        | Fable.Tuple(genArgs, _isStruct) ->
            let tup = getTupleTypeIdent com ctx genArgs
            let genArgs = genArgs |> List.map (transformType com ctx)
            TypeReference(tup, genArgs)
        | Fable.LambdaType(TransformType com ctx argType, TransformType com ctx returnType) ->
            Function([argType], returnType)
        | Fable.DelegateType(argTypes, TransformType com ctx returnType) ->
            let argTypes = argTypes |> List.map (transformType com ctx)
            Function(argTypes, returnType)
        | Fable.GenericParam(name, _constraints) -> Generic name
        | Fable.DeclaredType(ref, genArgs) -> transformDeclaredType com ctx ref genArgs
        | Fable.Regex -> makeTypeRefFromName "RegExp" []
        | Fable.AnonymousRecordType _ -> Dynamic // TODO

    let transformIdentWith (com: IDartCompiler) ctx typ name: Ident =
        let typ = transformType com ctx typ
        makeIdent typ name

    let transformIdent (com: IDartCompiler) ctx (id: Fable.Ident): Ident =
        transformIdentWith com ctx id.Type id.Name

    let transformIdentAsExpr (com: IDartCompiler) ctx (id: Fable.Ident) =
        transformIdentWith com ctx id.Type id.Name |> Expression.identExpression

    let transformGenericParam (com: IDartCompiler) ctx ownerFullName (g: Fable.GenericParam): GenericParam =
        let warn() =
            "Dart only accepts single inheritance constraints, "
            + $"you may need to explicitly cast values of generic type %s{g.Name} in %s{ownerFullName} "
            + "when accessing interface members."
            |> addWarning com [] None
            None

        let extends =
            g.Constraints
            |> Seq.chooseToList (function
                | Fable.Constraint.CoercesTo t -> Some t
                | _ -> None)
            |> function
                | [] -> None
                | [t] ->
                    match t with
                    | Fable.DeclaredType(e, _) ->
                        let e = com.GetEntity(e)
                        if e.IsInterface then
                            match e.FullName with
                            // We assume Iterable is extended not implemented
                            | Types.ienumerableGeneric -> transformType com ctx t |> Some
                            // We use types.dispose to call .Dispose so it's ok to ignore the constraint
                            | Types.idisposable -> None
                            | _ -> warn()
                        else
                            transformType com ctx t |> Some
                    | _ -> warn()
                | _ -> warn()

        { Name = g.Name; Extends = extends }

    let transformImport (com: IDartCompiler) ctx r t (selector: string) (path: string) =
        let rec getParts t (parts: string list) (expr: Expression) =
            match parts with
            | [] -> expr
            | [part] -> get (transformType com ctx t) expr part
            | m::ms -> get Dynamic expr m |> getParts t ms
        let selector, parts =
            let parts = Array.toList(selector.Split('.'))
            parts.Head, parts.Tail
        com.GetImportIdent(ctx, selector, path, (match parts with [] -> t | _ -> Fable.Any), ?range=r)
        |> Expression.identExpression
        |> getParts t parts

    let transformNumberLiteral com r kind (x: obj) =
        match kind, x with
        | Int8, (:? int8 as x) -> Expression.integerLiteral(int64 x)
        | UInt8, (:? uint8 as x) -> Expression.integerLiteral(int64 x)
        | Int16, (:? int16 as x) -> Expression.integerLiteral(int64 x)
        | UInt16, (:? uint16 as x) -> Expression.integerLiteral(int64 x)
        | Int32, (:? int32 as x) -> Expression.integerLiteral(x)
        | UInt32, (:? uint32 as x) -> Expression.integerLiteral(int64 x)
        | Int64, (:? int64 as x) -> Expression.integerLiteral(x)
        | UInt64, (:? uint64 as x) -> Expression.integerLiteral(int64 x)
        | Float32, (:? float32 as x) -> Expression.doubleLiteral(float x)
        | Float64, (:? float as x) -> Expression.doubleLiteral(x)
        | _ ->
            $"Expected literal of type %A{kind} but got {x.GetType().FullName}"
            |> addErrorAndReturnNull com r

    let transformTuple (com: IDartCompiler) ctx (args: Expression list) =
        let tup = getTupleTypeIdent com ctx args
        let genArgs = args |> List.map (fun a -> a.Type)
        let t = TypeReference(tup, genArgs)
        let isConst, args =
            if List.forall (isConstExpr ctx) args then true, List.map removeConst args
            else false, args
        // Generic arguments can be omitted from invocation expression
        Expression.invocationExpression(tup.Expr, args, t, isConst=isConst)

    let transformValue (com: IDartCompiler) (ctx: Context) (r: SourceLocation option) returnStrategy kind: Statement list * CapturedExpr =
        match kind with
        | Fable.UnitConstant -> [], None
        | Fable.ThisValue t -> transformType com ctx t |> ThisExpression |> resolveExpr returnStrategy
        | Fable.BaseValue(None, t) -> transformType com ctx t |> SuperExpression |> resolveExpr returnStrategy
        | Fable.BaseValue(Some boundIdent, _) -> transformIdentAsExpr com ctx boundIdent |> resolveExpr returnStrategy
        | Fable.TypeInfo(t, _d) -> transformType com ctx t |> TypeLiteral |> resolveExpr returnStrategy
        | Fable.Null t -> transformType com ctx t |> Expression.nullLiteral |> resolveExpr returnStrategy
        | Fable.BoolConstant v -> Expression.booleanLiteral v |> resolveExpr returnStrategy
        | Fable.CharConstant v -> Expression.integerLiteral(int v) |> resolveExpr returnStrategy
        | Fable.StringConstant v -> Expression.stringLiteral v |> resolveExpr returnStrategy
        | Fable.StringTemplate(_tag, parts, values) ->
            transformExprsAndResolve com ctx returnStrategy values (fun values ->
                Expression.InterpolationString(parts, values))

        // Dart enums are limited as we cannot set arbitrary values or combine as flags
        // so for now we compile F# enums as ints
        | Fable.NumberConstant(x, kind, _) ->
            transformNumberLiteral com r kind x |> resolveExpr returnStrategy

        | Fable.RegexConstant(source, flags) ->
            let flagToArg = function
                | RegexIgnoreCase -> Some(Some "caseSensitive", Expression.booleanLiteral false)
                | RegexMultiline -> Some(Some "multiLine", Expression.booleanLiteral true)
                | RegexGlobal
                | RegexSticky -> None
            let regexIdent = makeIdent MetaType "RegExp"
            let args = [
                None, Expression.stringLiteral source
                yield! flags |> List.choose flagToArg
            ]
            Expression.invocationExpression(regexIdent.Expr, args, makeTypeRef regexIdent [])
            |> resolveExpr returnStrategy

        | Fable.NewOption(Some expr, _, _) -> transform com ctx returnStrategy expr
        | Fable.NewOption(None, typ, _) ->
            transformType com ctx typ
            |> Nullable
            |> Expression.nullLiteral
            |> resolveExpr returnStrategy

        | Fable.NewTuple(exprs, _) ->
            transformExprsAndResolve com ctx returnStrategy exprs (transformTuple com ctx)

        | Fable.NewArray(Fable.ArrayValues exprs, typ, _) ->
            transformExprsAndResolve com ctx returnStrategy exprs (makeMutableListExpr com ctx typ)
        // We cannot allocate in Dart without filling the array to a non-null value
        | Fable.NewArray((Fable.ArrayFrom expr | Fable.ArrayAlloc expr), typ, _) ->
            transformExprsAndResolve com ctx returnStrategy [expr] (fun exprs ->
                let listIdent = makeIdent MetaType "List"
                let typ = transformType com ctx typ
                Expression.invocationExpression(listIdent.Expr, "of", exprs, makeTypeRef listIdent [typ]))

        | Fable.NewRecord(values, ref, genArgs) ->
            transformExprsAndResolve com ctx returnStrategy values (fun args ->
                let ent = com.GetEntity(ref)
                let genArgs = genArgs |> List.map (transformType com ctx)
                let consRef = getEntityIdent com ctx ent
                let typeRef = TypeReference(consRef, genArgs)
                let isConst, args =
                    let isConst = List.forall (isConstExpr ctx) args && (ent.FSharpFields |> List.forall (fun f -> not f.IsMutable))
                    if isConst then true, List.map removeConst args
                    else false, args
                Expression.invocationExpression(consRef.Expr, args, typeRef, genArgs=genArgs, isConst=isConst)
            )
        | Fable.NewAnonymousRecord _ ->
            "TODO: Anonymous record is not supported yet"
            |> addErrorAndReturnNull com r
            |> resolveExpr returnStrategy

        | Fable.NewUnion(values, tag, ref, genArgs) ->
            transformExprsAndResolve com ctx returnStrategy values (fun fields ->
                let ent = com.GetEntity(ref)
                let caseName = ent.UnionCases |> List.item tag |> getUnionCaseName
                let tag = Expression.integerLiteral(tag) |> Expression.commented caseName
                let args = [tag; makeImmutableListExpr com ctx Fable.Any fields]
                let genArgs = genArgs |> List.map (transformType com ctx)
                let consRef = getEntityIdent com ctx ent
                let typeRef = TypeReference(consRef, genArgs)
                let isConst, args =
                    if List.forall (isConstExpr ctx) args then true, List.map removeConst args
                    else false, args
                Expression.invocationExpression(consRef.Expr, args, typeRef, genArgs=genArgs, isConst=isConst)
            )

        | Fable.NewList(headAndTail, typ) ->
            let rec getItems acc = function
                | None -> List.rev acc, None
                | Some(head, Fable.Value(Fable.NewList(tail, _),_)) -> getItems (head::acc) tail
                | Some(head, tail) -> List.rev (head::acc), Some tail

            match getItems [] headAndTail with
            | [], None ->
                libCall com ctx (Fable.List typ) "List" "empty" []
                |> resolveExpr returnStrategy

            | [expr], None ->
                transformExprsAndResolve com ctx returnStrategy [expr] (fun exprs ->
                    libCall com ctx (Fable.List typ) "List" "singleton" exprs)

            | exprs, None ->
                transformExprsAndResolve com ctx returnStrategy exprs (fun exprs ->
                    [makeImmutableListExpr com ctx typ exprs]
                    |> libCall com ctx (Fable.List typ) "List" "ofArray")

            | [head], Some tail ->
                transformExprsAndResolve com ctx returnStrategy [head; tail] (fun exprs ->
                    libCall com ctx (Fable.List typ) "List" "cons" exprs)

            | exprs, Some tail ->
                transformExprsAndResolve com ctx returnStrategy (exprs @ [tail]) (fun exprs ->
                    let exprs, tail = List.splitLast exprs
                    let exprs = makeImmutableListExpr com ctx typ exprs
                    [exprs; tail]
                    |> libCall com ctx (Fable.List typ) "List" "ofArrayWithTail")

    let transformOperation com ctx (_: SourceLocation option) t returnStrategy opKind: Statement list * CapturedExpr =
        match opKind with
        | Fable.Unary(op, expr) ->
            transformExprAndResolve com ctx returnStrategy expr (fun expr ->
                UnaryExpression(op, expr))

        | Fable.Binary(op, left, right) ->
            transformExprsAndResolve2 com ctx returnStrategy left right (fun left right ->
                BinaryExpression(op, left, right, transformType com ctx t))

        | Fable.Logical(op, left, right) ->
            transformExprsAndResolve2 com ctx returnStrategy left right (fun left right ->
                LogicalExpression(op, left, right))

    let transformEmit (com: IDartCompiler) ctx range t returnStrategy (emitInfo: Fable.EmitInfo) =
        let info = emitInfo.CallInfo
        let statements, args = transformCallArgs com ctx range (CallInfo info)
        let args = List.map snd args

        let emitExpr = Expression.emitExpression(emitInfo.Macro, args, transformType com ctx t)
        if emitInfo.IsStatement then
            // Ignore the return strategy
            statements @ [ExpressionStatement(emitExpr)], None
        else
            let statements2, captureExpr = resolveExpr returnStrategy emitExpr
            statements @ statements2, captureExpr

    let transformCall com ctx range (t: Fable.Type) returnStrategy callee callInfo =
        let argsLen (i: Fable.CallInfo) =
            List.length i.Args + (if Option.isSome i.ThisArg then 1 else 0)
        // Warn when there's a recursive call that couldn't be optimized?
        match returnStrategy, ctx.TailCallOpportunity with
        | (Return|ReturnVoid), Some tc when tc.IsRecursiveRef(callee)
                                            && argsLen callInfo = List.length tc.Args ->
            let args =
                match callInfo.ThisArg with
                | Some thisArg -> thisArg::callInfo.Args
                | None -> callInfo.Args
            optimizeTailCall com ctx range tc args, None
        | _ ->
            // Try to optimize some patterns after FableTransforms
            let optimized =
                match callInfo.OptimizableInto, callInfo.Args with
                | Some "array", [Replacements.Util.ArrayOrListLiteral(vals,_)] ->
                    Fable.Value(Fable.NewArray(Fable.ArrayValues vals, Fable.Any, Fable.MutableArray), range) |> Some
                | _ -> None

            match optimized with
            | Some e -> transform com ctx returnStrategy e
            | None ->
                let t = transformType com ctx t
                // Maybe we can omit generic args if they can be inferred from the arguments
                let genArgs = callInfo.GenericArgs |> List.map (transformType com ctx)
                let calleeStatements, callee = transformAndCaptureExpr com ctx callee
                let argStatements, args = transformCallArgs com ctx range (CallInfo callInfo)
                let statements, callee = combineCalleeAndArgStatements com ctx calleeStatements argStatements callee
                let isConst =
                    callInfo.IsConstructor && List.forall (snd >> isConstExpr ctx) args && (
                        callInfo.CallMemberInfo
                        |> Option.bind (fun i -> i.DeclaringEntity)
                        |> Option.map (fun e ->
                            com.GetEntity(e).Attributes
                            |> Seq.exists (fun att -> att.Entity.FullName = Atts.dartIsConst))
                        |> Option.defaultValue false
                    )
                let args =
                    if isConst then args |> List.map (fun (name, arg) -> name, removeConst arg)
                    else args
                let statements2, capturedExpr =
                    Expression.invocationExpression(callee, args, t, genArgs, isConst=isConst)
                    |> resolveExpr returnStrategy
                statements @ statements2, capturedExpr

    let transformCurriedApplyAsStatements com ctx range t returnStrategy callee args =
        // Warn when there's a recursive call that couldn't be optimized?
        match returnStrategy, ctx.TailCallOpportunity with
        | (Return|ReturnVoid), Some tc when tc.IsRecursiveRef(callee)
                                            && List.sameLength args tc.Args ->
            optimizeTailCall com ctx range tc args, None
        | _ ->
            let t = transformType com ctx t
            let calleeStatements, callee = transformAndCaptureExpr com ctx callee
            let argStatements, args = transformCallArgs com ctx range (NoCallInfo args)
            let statements, callee = combineCalleeAndArgStatements com ctx calleeStatements argStatements callee
            let invocation =
                match args with
                | [] -> Expression.invocationExpression(callee, t)
                | args -> (callee, args) ||> List.fold (fun e arg -> Expression.invocationExpression(e, [arg], t))
            let statements2, capturedExpr = resolveExpr returnStrategy invocation
            statements @ statements2, capturedExpr

    let typeImplementsOrExtends (com: IDartCompiler) (baseEnt: Fable.Entity) (t: Fable.Type) =
        match baseEnt.FullName, t with
        | Types.ienumerableGeneric, (Fable.Array _ | Fable.List _) -> true
        | baseFullName, Fable.DeclaredType(e, _) ->
            let e = com.GetEntity(e)
            if baseEnt.IsInterface then
                e.AllInterfaces |> Seq.exists (fun i -> i.Entity.FullName = baseFullName)
            else
                let rec extends baseFullName (e: Fable.Entity) =
                    match e.BaseType with
                    | Some baseType ->
                        if baseType.Entity.FullName = baseFullName
                        then true
                        else com.GetEntity(baseType.Entity) |> extends baseFullName
                    | None -> false
                extends baseFullName e
        | baseFullName, Fable.GenericParam(_, constraints) ->
            constraints |> List.exists (function
                | Fable.Constraint.CoercesTo(Fable.DeclaredType(e, _)) -> e.FullName = baseFullName
                | _ -> false)
        | _ -> false

    let transformCast (com: IDartCompiler) (ctx: Context) t returnStrategy expr =
        let needsCast (com: IDartCompiler) sourceType targetType =
            match targetType, sourceType with
            | Fable.DeclaredType(ent, _), _ ->
                let ent = com.GetEntity(ent)
                typeImplementsOrExtends com ent sourceType |> not

            | Fable.Number(DartInt, _), Fable.Number(DartInt, _)
            | Fable.Number(DartDouble, _), Fable.Number(DartDouble, _) -> false
            | _ -> targetType <> sourceType

        match t, expr with
        // Optimization for (numeric) array or list literals casted to seq
        // Done at the very end of the compile pipeline to get more opportunities
        // of matching cast and literal expressions after resolving pipes, inlining...
        | Fable.DeclaredType(EntFullName(Types.ienumerableGeneric | Types.ienumerable), [_]),
          Replacements.Util.ArrayOrListLiteral(exprs, typ) ->
            transformExprsAndResolve com ctx returnStrategy exprs
                (makeImmutableListExpr com ctx typ)

        | Fable.Any, _ -> com.Transform(ctx, returnStrategy, expr)
        | Fable.Unit, _ -> com.Transform(ctx, ReturnVoid, expr)

        | _ ->
            if needsCast com expr.Type t then
                transformExprAndResolve com ctx returnStrategy expr (fun expr ->
                    Expression.asExpression(expr, transformType com ctx t))
            else
                com.Transform(ctx, returnStrategy, expr)

    // TODO: Try to identify type testing in the catch clause and use Dart's `on ...` exception checking
    let transformTryCatch com ctx _r returnStrategy (body: Fable.Expr, catch, finalizer) =
        let returnStrategy, prevStmnt, captureExpr =
            convertCaptureStrategyIntoAssign com ctx body.Type returnStrategy
        // try .. catch statements cannot be tail call optimized
        let ctx = { ctx with TailCallOpportunity = None }
        let handlers =
            catch |> Option.map (fun (param, body) ->
                let param = transformIdent com ctx param
                let body, _ = com.Transform(ctx, returnStrategy, body)
                CatchClause(param=param, body=body))
            |> Option.toList
        let finalizer =
            finalizer
            |> Option.map (transform com ctx ReturnVoid >> fst)
        let statements, _ = transform com ctx returnStrategy body
        prevStmnt @ [Statement.tryStatement(statements, handlers=handlers, ?finalizer=finalizer)], captureExpr

    /// Branching expressions like conditionals, decision trees or try catch cannot capture
    /// the resulting expression at once so declare a variable and assign the potential results to it
    let convertCaptureStrategyIntoAssign com ctx t returnStrategy =
        match returnStrategy with
        | Capture ->
            let t = transformType com ctx t
            let ident = getUniqueNameInDeclarationScope ctx "tmp" |> makeIdent t
            let varDecl = Statement.variableDeclaration(ident, Var)
            Assign ident.Expr, [varDecl], Some ident.Expr
        | _ -> returnStrategy, [], None

    let transformConditional (com: IDartCompiler) ctx _r returnStrategy guardExpr thenExpr elseExpr =
        let asStatement =
            match returnStrategy with
            | ReturnVoid -> true
            | Target _ -> true // Compile as statement so values can be bound
            | Capture | Assign _ -> isConditionalStament ctx guardExpr thenExpr elseExpr
            | Return -> Option.isSome ctx.TailCallOpportunity || isConditionalStament ctx guardExpr thenExpr elseExpr
        if not asStatement then
            transformExprsAndResolve3 com ctx returnStrategy guardExpr thenExpr elseExpr
                (fun guardExpr thenExpr elseExpr -> Expression.conditionalExpression(guardExpr, thenExpr, elseExpr))
        else
            let prevStmnt, guardExpr = transformAndCaptureExpr com ctx guardExpr
            match guardExpr with
            | Literal(BooleanLiteral(value=value)) ->
                let bodyStmnt, captureExpr = com.Transform(ctx, returnStrategy, if value then thenExpr else elseExpr)
                prevStmnt @ bodyStmnt, captureExpr
            | guardExpr ->
                let returnStrategy, prevStmnt2, captureExpr =
                    convertCaptureStrategyIntoAssign com ctx thenExpr.Type returnStrategy
                let thenStmnt, _ = com.Transform(ctx, returnStrategy, thenExpr)
                let elseStmnt, _ = com.Transform(ctx, returnStrategy, elseExpr)
                prevStmnt @ prevStmnt2 @ [Statement.ifStatement(guardExpr, thenStmnt, elseStmnt)], captureExpr

    let transformGet (com: IDartCompiler) ctx _range t returnStrategy kind fableExpr =
        let t = transformType com ctx t

        match kind with
        | Fable.ExprGet prop ->
            transformExprsAndResolve2 com ctx returnStrategy fableExpr prop (fun expr prop ->
                Expression.indexExpression(expr, prop, t))

        | Fable.FieldGet(fieldName, info) ->
            let fableExpr =
                match fableExpr with
                // If we're accessing a virtual member with default implementation (see #701)
                // from base class, we can use `super` so we don't need the bound this arg
                | Fable.Value(Fable.BaseValue(_,t), r) -> Fable.Value(Fable.BaseValue(None, t), r)
                | _ -> fableExpr
            transformExprAndResolve com ctx returnStrategy fableExpr (fun expr ->
                Expression.propertyAccess(expr, fieldName, t, isConst=info.IsConst))

        | Fable.ListHead ->
            transformExprAndResolve com ctx returnStrategy fableExpr (fun expr -> get t expr "head")

        | Fable.ListTail ->
            transformExprAndResolve com ctx returnStrategy fableExpr (fun expr -> get t expr "tail")

        | Fable.TupleIndex index ->
            match fableExpr with
            // Check the erased expressions don't have side effects?
            | Fable.Value(Fable.NewTuple(exprs,_), _) ->
                List.item index exprs |> transform com ctx returnStrategy
            | fableExpr ->
                transformExprAndResolve com ctx returnStrategy fableExpr (fun expr ->
                    Expression.propertyAccess(expr, $"item%i{index + 1}", t))

        | Fable.OptionValue ->
            transformExprAndResolve com ctx returnStrategy fableExpr NotNullAssert

        | Fable.UnionTag ->
            transformExprAndResolve com ctx returnStrategy fableExpr getUnionExprTag

        | Fable.UnionField(_caseIndex, fieldIndex) ->
            transformExprAndResolve com ctx returnStrategy fableExpr (fun expr ->
                let fields = getUnionExprFields expr
                let index = Expression.indexExpression(fields, Expression.integerLiteral fieldIndex, t)
                match t with
                | Dynamic -> index
                | t -> Expression.asExpression(index, t))

    let transformFunction com ctx name (args: Fable.Ident list) (body: Fable.Expr): Ident list * Statement list * Type =
        let tailcallChance = Option.map (fun name ->
            NamedTailCallOpportunity(com, ctx, name, args) :> ITailCallOpportunity) name

        let args = discardUnitArg args
        let mutable isTailCallOptimized = false
        let ctx =
            { ctx with TailCallOpportunity = tailcallChance
                       OptimizeTailCall = fun () -> isTailCallOptimized <- true }

        let ret, returnType =
            match body.Type with
            | Fable.Unit -> ReturnVoid, Void
            | t -> Return, transformType com ctx t

        let body, _ = transform com ctx ret body

        match isTailCallOptimized, tailcallChance with
        | true, Some tc ->
            // Replace args, see NamedTailCallOpportunity constructor
            let args' =
                List.zip args tc.Args
                |> List.map (fun (id, tcArg) ->
                    let t = transformType com ctx id.Type
                    makeIdent t tcArg)

            let varDecls =
                List.zip args args'
                |> List.map (fun (id, tcArg) ->
                    let ident = transformIdent com ctx id
                    Statement.variableDeclaration(ident, value=Expression.identExpression(tcArg)))

            // Make sure we don't get trapped in an infinite loop, see #1624
            let breakStmnt =
                Statement.breakStatement()
                |> Statement.commented "ignore: dead_code"
            let body = varDecls @ body @ [breakStmnt]
            args', [Statement.labeledStatement(
                tc.Label,
                Statement.whileStatement(Expression.booleanLiteral(true), body)
            )], returnType

        | _ -> args |> List.map (transformIdent com ctx), body, returnType

    let transformSet (com: IDartCompiler) ctx range kind toBeSet (value: Fable.Expr) =
        match kind with
        | Fable.ValueSet ->
            transformExprsAndResolve2 com ctx ReturnVoid toBeSet value (assign range)
        | Fable.ExprSet(prop) ->
            transformExprsAndResolve3 com ctx ReturnVoid toBeSet prop value (fun toBeSet prop value ->
                let toBeSet = getExpr Dynamic toBeSet prop
                assign range toBeSet value)
        | Fable.FieldSet(fieldName) ->
            transformExprsAndResolve2 com ctx ReturnVoid toBeSet value (fun toBeSet value ->
                let toBeSet = get Dynamic toBeSet fieldName
                assign range toBeSet value)

    let transformBinding (com: IDartCompiler) ctx (var: Fable.Ident) (value: Fable.Expr) =
        let ident = transformIdent com ctx var
        match value with
        | Function(args, body) ->
            let genParams = args |> List.map (fun a -> a.Type) |> getLocalFunctionGenericParams com ctx
            let args, body, returnType = transformFunction com ctx (Some var.Name) args body
            if var.IsMutable then
                let value = Expression.anonymousFunction(args, body, returnType, genParams)
                ctx, [Statement.variableDeclaration(ident, Var, value)]
            else
                let args = args |> List.map FunctionArg
                let genParams = genParams |> List.map (fun g -> { Name = g; Extends = None })
                ctx, [Statement.functionDeclaration(ident.Name, args, body, returnType, genParams=genParams)]
        | _ ->
            let valueStmnts, value = transformAndCaptureExpr com ctx value
            let kind, value = getVarKind ctx var.IsMutable value
            let ctx =
                match kind with
                | Const -> { ctx with ConstIdents = Set.add ident.Name ctx.ConstIdents }
                | Var | Final -> ctx
            ctx, valueStmnts @ [Statement.variableDeclaration(ident, kind, value)]

    let transformSwitch (com: IDartCompiler) ctx returnStrategy evalExpr cases defaultCase =
        let cases =
            cases |> List.choose (fun (guards, expr) ->
                // Remove empty branches
                match returnStrategy, expr, guards with
                | ReturnVoid, Fable.Value(Fable.UnitConstant,_), _
                | _, _, [] -> None
                | _, _, guards ->
                    // Switch is only activated when guards are literals so we can ignore the statements
                    let guards = guards |> List.map (transformAndCaptureExpr com ctx >> snd)
                    let caseBody, _ = com.Transform(ctx, returnStrategy, expr)
                    SwitchCase(guards, caseBody) |> Some
                )
        let defaultCase =
            defaultCase
            |> Option.map (fun expr -> com.Transform(ctx, returnStrategy, expr) |> fst)

        let evalStmnt, evalExpr = transformAndCaptureExpr com ctx evalExpr
        evalStmnt @ [Statement.switchStatement(evalExpr, cases, defaultCase)]

    let matchTargetIdentAndValues idents values =
        if List.isEmpty idents then []
        elif List.sameLength idents values then List.zip idents values
        else failwith "Target idents/values lengths differ"

    let getDecisionTargetAndBindValues (com: IDartCompiler) (ctx: Context) targetIndex boundValues =
        let idents, target = getDecisionTarget ctx targetIndex
        let identsAndValues = matchTargetIdentAndValues idents boundValues
        if not com.Options.DebugMode then
            let bindings, replacements =
                (([], Map.empty), identsAndValues)
                ||> List.fold (fun (bindings, replacements) (ident, expr) ->
                    if canHaveSideEffects expr then
                        (ident, expr)::bindings, replacements
                    else
                        bindings, Map.add ident.Name expr replacements)
            let target = FableTransforms.replaceValues replacements target
            List.rev bindings, target
        else
            identsAndValues, target

    let transformDecisionTreeSuccess (com: IDartCompiler) (ctx: Context) returnStrategy targetIndex boundValues =
        match returnStrategy with
        | Target targetId ->
            let idents, _ = getDecisionTarget ctx targetIndex
            let assignments =
                matchTargetIdentAndValues idents boundValues
                |> List.collect (fun (id, value) ->
                    let id = transformIdentAsExpr com ctx id
                    transform com ctx (Assign id) value |> fst)
            let targetAssignment =
                assign None (IdentExpression targetId) (Expression.integerLiteral targetIndex)
                |> ExpressionStatement
            targetAssignment::assignments, None
        | ret ->
            let bindings, target = getDecisionTargetAndBindValues com ctx targetIndex boundValues
            let bindings = bindings |> List.collect (fun (i, v) -> transformBinding com ctx i v |> snd)
            let statements, capturedExpr = com.Transform(ctx, ret, target)
            bindings @ statements, capturedExpr

    let canTransformDecisionTreeAsSwitch expr =
        let (|Equals|_|) = function
            | Fable.Operation(Fable.Binary(BinaryEqual, expr, right), _, _) ->
                match expr with
                | Fable.Value((Fable.CharConstant _ | Fable.StringConstant _ | Fable.NumberConstant _), _) -> Some(expr, right)
                | _ -> None
            | Fable.Test(expr, Fable.UnionCaseTest tag, _) ->
                let evalExpr = Fable.Get(expr, Fable.UnionTag, numType Int32, None)
                let right = makeIntConst tag
                Some(evalExpr, right)
            | _ -> None
        let sameEvalExprs evalExpr1 evalExpr2 =
            match evalExpr1, evalExpr2 with
            | Fable.IdentExpr i1, Fable.IdentExpr i2
            | Fable.Get(Fable.IdentExpr i1,Fable.UnionTag,_,_), Fable.Get(Fable.IdentExpr i2,Fable.UnionTag,_,_) ->
                i1.Name = i2.Name
            | Fable.Get(Fable.IdentExpr i1, Fable.FieldGet(fieldName1, _),_,_), Fable.Get(Fable.IdentExpr i2, Fable.FieldGet(fieldName2, _),_,_) ->
                i1.Name = i2.Name && fieldName1 = fieldName2
            | _ -> false
        let rec checkInner cases evalExpr = function
            | Fable.IfThenElse(Equals(evalExpr2, caseExpr),
                               Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _)
                                    when sameEvalExprs evalExpr evalExpr2 ->
                match treeExpr with
                | Fable.DecisionTreeSuccess(defaultTargetIndex, defaultBoundValues, _) ->
                    let cases = (caseExpr, targetIndex, boundValues)::cases |> List.rev
                    Some(evalExpr, cases, (defaultTargetIndex, defaultBoundValues))
                | treeExpr -> checkInner ((caseExpr, targetIndex, boundValues)::cases) evalExpr treeExpr
            | _ -> None
        match expr with
        | Fable.IfThenElse(Equals(evalExpr, caseExpr),
                           Fable.DecisionTreeSuccess(targetIndex, boundValues, _), treeExpr, _) ->
            match checkInner [caseExpr, targetIndex, boundValues] evalExpr treeExpr with
            | Some(evalExpr, cases, defaultCase) ->
                Some(evalExpr, cases, defaultCase)
            | None -> None
        | _ -> None

    let groupSwitchCases t (cases: (Fable.Expr * int * Fable.Expr list) list) (defaultIndex, defaultBoundValues) =
        cases
        |> List.groupBy (fun (_,idx,boundValues) ->
            // Try to group cases with some target index and empty bound values
            // If bound values are non-empty use also a non-empty Guid to prevent grouping
            if List.isEmpty boundValues
            then idx, System.Guid.Empty
            else idx, System.Guid.NewGuid())
        |> List.map (fun ((idx,_), cases) ->
            let caseExprs = cases |> List.map Tuple3.item1
            // If there are multiple cases, it means boundValues are empty
            // (see `groupBy` above), so it doesn't mind which one we take as reference
            let boundValues = cases |> List.head |> Tuple3.item3
            caseExprs, Fable.DecisionTreeSuccess(idx, boundValues, t))
        |> function
            | [] -> []
            // Check if the last case can also be grouped with the default branch, see #2357
            | cases when List.isEmpty defaultBoundValues ->
                match List.splitLast cases with
                | cases, (_, Fable.DecisionTreeSuccess(idx, [], _))
                    when idx = defaultIndex -> cases
                | _ -> cases
            | cases -> cases

    let getTargetsWithMultipleReferences expr =
        let rec findSuccess (targetRefs: Map<int,int>) = function
            | [] -> targetRefs
            | expr::exprs ->
                match expr with
                // We shouldn't actually see this, but shortcircuit just in case
                | Fable.DecisionTree _ ->
                    findSuccess targetRefs exprs
                | Fable.DecisionTreeSuccess(idx,_,_) ->
                    let count =
                        Map.tryFind idx targetRefs
                        |> Option.defaultValue 0
                    let targetRefs = Map.add idx (count + 1) targetRefs
                    findSuccess targetRefs exprs
                | expr ->
                    let exprs2 = FableTransforms.getSubExpressions expr
                    findSuccess targetRefs (exprs @ exprs2)
        findSuccess Map.empty [expr] |> Seq.choose (fun kv ->
            if kv.Value > 1 then Some kv.Key else None) |> Seq.toList

    /// When several branches share target create first a switch to get the target index and bind value
    /// and another to execute the actual target
    let transformDecisionTreeWithTwoSwitches (com: IDartCompiler) ctx returnStrategy
                    (targets: (Fable.Ident list * Fable.Expr) list) treeExpr =
        // Declare target and bound idents
        let targetId =
            getUniqueNameInDeclarationScope ctx "pattern_matching_result"
            |> makeTypedIdent (numType Int32)
        let varDecls =
            [
                transformIdent com ctx targetId
                yield! targets |> List.collect (fun (idents,_) ->
                    idents |> List.map (transformIdent com ctx))
            ]
            |> List.map Statement.variableDeclaration
        // Transform targets as switch
        let switch2 =
            // Declare the last case as the default case?
            let cases = targets |> List.mapi (fun i (_,target) -> [makeIntConst i], target)
            transformSwitch com ctx returnStrategy (targetId |> Fable.IdentExpr) cases None
        // Transform decision tree
        let targetAssign = Target(transformIdent com ctx targetId)
        let ctx = { ctx with DecisionTargets = targets }
        match canTransformDecisionTreeAsSwitch treeExpr with
        | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
            let cases = groupSwitchCases (numType Int32) cases (defaultIndex, defaultBoundValues)
            let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, numType Int32)
            let switch1 = transformSwitch com ctx targetAssign evalExpr cases (Some defaultCase)
            varDecls @ switch1 @ switch2
        | None ->
            let decisionTree, _ = com.Transform(ctx, targetAssign, treeExpr)
            varDecls @ decisionTree @ switch2

    let transformDecisionTree (com: IDartCompiler) (ctx: Context) returnStrategy
                        (targets: (Fable.Ident list * Fable.Expr) list) (treeExpr: Fable.Expr) =
        let t = treeExpr.Type
        let returnStrategy, prevStmnt, captureExpr = convertCaptureStrategyIntoAssign com ctx t returnStrategy
        let resolve stmnts = prevStmnt @ stmnts, captureExpr

        // If some targets are referenced multiple times, hoist bound idents,
        // resolve the decision index and compile the targets as a switch
        let targetsWithMultiRefs =
            if com.Options.Language = TypeScript then [] // no hoisting when compiled with types
            else getTargetsWithMultipleReferences treeExpr
        match targetsWithMultiRefs with
        | [] ->
            let ctx = { ctx with DecisionTargets = targets }
            match canTransformDecisionTreeAsSwitch treeExpr with
            | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                let cases = cases |> List.map (fun (caseExpr, targetIndex, boundValues) ->
                    [caseExpr], Fable.DecisionTreeSuccess(targetIndex, boundValues, t))
                let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                transformSwitch com ctx returnStrategy evalExpr cases (Some defaultCase) |> resolve
            | None ->
                com.Transform(ctx, returnStrategy, treeExpr) |> fst |> resolve
        | targetsWithMultiRefs ->
            // If the bound idents are not referenced in the target, remove them
            let targets =
                targets |> List.map (fun (idents, expr) ->
                    idents
                    |> List.exists (fun i -> FableTransforms.isIdentUsed i.Name expr)
                    |> function
                        | true -> idents, expr
                        | false -> [], expr)
            let hasAnyTargetWithMultiRefsBoundValues =
                targetsWithMultiRefs |> List.exists (fun idx ->
                    targets[idx] |> fst |> List.isEmpty |> not)
            if not hasAnyTargetWithMultiRefsBoundValues then
                match canTransformDecisionTreeAsSwitch treeExpr with
                | Some(evalExpr, cases, (defaultIndex, defaultBoundValues)) ->
                    let cases = groupSwitchCases t cases (defaultIndex, defaultBoundValues)
                    let ctx = { ctx with DecisionTargets = targets }
                    let defaultCase = Fable.DecisionTreeSuccess(defaultIndex, defaultBoundValues, t)
                    transformSwitch com ctx returnStrategy evalExpr cases (Some defaultCase) |> resolve
                | None ->
                    transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr |> resolve
            else
                transformDecisionTreeWithTwoSwitches com ctx returnStrategy targets treeExpr |> resolve

    let transformTest (com: IDartCompiler) ctx _range returnStrategy kind expr =
        transformExprAndResolve com ctx returnStrategy expr (fun expr ->
            match kind with
            | Fable.TypeTest t ->
                Expression.isExpression(expr, transformType com ctx t)
            | Fable.OptionTest isSome ->
                let t = match expr.Type with Nullable t -> t | t -> t
                let op = if isSome then BinaryUnequal else BinaryEqual
                Expression.binaryExpression(op, expr, Expression.nullLiteral t, Boolean)
            | Fable.ListTest nonEmpty ->
                let expr = get Boolean expr "isNil"
                if nonEmpty then Expression.unaryExpression(UnaryNot, expr) else expr
            | Fable.UnionCaseTest tag ->
                let expected = Expression.integerLiteral tag
                let actual = getUnionExprTag expr
                Expression.binaryExpression(BinaryEqual, actual, expected, Boolean))

    let extractBaseArgs (com: IDartCompiler) (ctx: Context) (classDecl: Fable.ClassDecl) =
        match classDecl.BaseCall with
        | Some(Fable.Call(_baseRef, info, _, _) as e) ->
            match transformCallArgs com ctx None (CallInfo info) with
            | [], args -> args
            | _, args ->
                $"Rewrite base arguments for {classDecl.Entity.FullName} so they can be compiled as Dart expressions"
                |> addWarning com [] e.Range
                args
        | Some(Fable.Value _ as e) ->
            $"Ignoring base call for {classDecl.Entity.FullName}" |> addWarning com [] e.Range
            []
        | Some e ->
            $"Unexpected base call for {classDecl.Entity.FullName}" |> addError com [] e.Range
            []
        | None ->
            []

    let transformAndCaptureExpr (com: IDartCompiler) ctx expr =
        match com.Transform(ctx, Capture, expr) with
        | statements, Some expr -> statements, expr
        | statements, None -> statements, ignoreExpr com ctx None

    let transform (com: IDartCompiler) ctx (returnStrategy: ReturnStrategy) (expr: Fable.Expr): Statement list * CapturedExpr =
        match expr with
        | Fable.ObjectExpr _ -> [], None // TODO

        | Fable.Unresolved(_,_,r) ->
            addError com [] r "Unexpected unresolved expression"
            [], None

        | Fable.Extended(kind, _r) ->
            match kind with
            | Fable.Curry(e, arity) -> failwith "todo: transformCurry (statement)"
            | Fable.RegionStart _ -> [], None
            | Fable.Throw(None, t) ->
                [Expression.rethrowExpression(transformType com ctx t) |> Statement.ExpressionStatement], None
            | Fable.Throw(Some expr, t) ->
                transformExprAndResolve com ctx returnStrategy expr (fun expr ->
                    Expression.throwExpression(expr, transformType com ctx t))
            | Fable.Debugger ->
                [extLibCall com ctx Fable.Unit "dart:developer" "debugger" [] |> Statement.ExpressionStatement], None

        | Fable.TypeCast(e, t) ->
            transformCast com ctx t returnStrategy e

        | Fable.Value(kind, r) ->
            transformValue com ctx r returnStrategy kind

        | Fable.IdentExpr id ->
            transformIdentAsExpr com ctx id |> resolveExpr returnStrategy

        | Fable.Import({ Selector = selector; Path = path }, t, r) ->
            transformImport com ctx r t selector path |> resolveExpr returnStrategy

        | Fable.Test(expr, kind, range) ->
            transformTest com ctx range returnStrategy kind expr

        | Fable.Lambda(arg, body, info) ->
            let genParams = getLocalFunctionGenericParams com ctx [arg.Type]
            let args, body, t = transformFunction com ctx info.Name [arg] body
            Expression.anonymousFunction(args, body, t, genParams)
            |> resolveExpr returnStrategy

        | Fable.Delegate(args, body, info) ->
            let genParams = args |> List.map (fun a -> a.Type) |> getLocalFunctionGenericParams com ctx
            let args, body, t = transformFunction com ctx info.Name args body
            Expression.anonymousFunction(args, body, t, genParams)
            |> resolveExpr returnStrategy

        | Fable.Call(callee, info, typ, range) ->
            transformCall com ctx range typ returnStrategy callee info

        | Fable.CurriedApply(callee, args, typ, range) ->
            transformCurriedApplyAsStatements com ctx range typ returnStrategy callee args

        | Fable.Emit(info, t, range) ->
            transformEmit com ctx range t returnStrategy info

        | Fable.Operation(kind, t, r) ->
            transformOperation com ctx r t returnStrategy kind

        | Fable.Get(expr, kind, t, range) ->
            transformGet com ctx range t returnStrategy kind expr

        | Fable.Set(expr, kind, _typ, value, range) ->
            transformSet com ctx range kind expr value

        | Fable.Let(ident, value, body) ->
            let ctx, binding = transformBinding com ctx ident value
            let body, captureExpr = transform com ctx returnStrategy body
            binding @ body, captureExpr

        | Fable.LetRec(bindings, body) ->
            let ctx, bindings =
                ((ctx, []), bindings) ||> List.fold (fun (ctx, bindings) (i, v) ->
                    let ctx, newBindings = transformBinding com ctx i v
                    ctx, bindings @ newBindings)
            let body, captureExpr = transform com ctx returnStrategy body
            bindings @ body, captureExpr

        | Fable.Sequential exprs ->
            let exprs, lastExpr = List.splitLast exprs
            let statements1 = exprs |> List.collect (transform com ctx ReturnVoid >> fst)
            let statements2, expr = transform com ctx returnStrategy lastExpr
            statements1 @ statements2, expr

        | Fable.TryCatch (body, catch, finalizer, r) ->
            transformTryCatch com ctx r returnStrategy (body, catch, finalizer)

        | Fable.IfThenElse(guardExpr, thenExpr, elseExpr, r) ->
            transformConditional com ctx r returnStrategy guardExpr thenExpr elseExpr

        | Fable.DecisionTree(expr, targets) ->
            transformDecisionTree com ctx returnStrategy targets expr

        | Fable.DecisionTreeSuccess(idx, boundValues, _) ->
            transformDecisionTreeSuccess com ctx returnStrategy idx boundValues

        | Fable.WhileLoop(guard, body, label, _range) ->
            let statements1, guard = transformAndCaptureExpr com ctx guard
            let body, _ = transform com ctx ReturnVoid body
            let whileLoop = Statement.whileStatement(guard, body)
            match label with
            | Some label -> statements1 @ [Statement.labeledStatement(label, whileLoop)], None
            | None -> statements1 @ [whileLoop], None

        | Fable.ForLoop (var, start, limit, body, isUp, _range) ->
            let statements, startAndLimit = combineStatementsAndExprs com ctx [
                transformAndCaptureExpr com ctx start
                transformAndCaptureExpr com ctx limit
            ]
            let body, _ = transform com ctx ReturnVoid body
            let param = transformIdent com ctx var
            let paramExpr = Expression.identExpression param
            let op1, op2 =
                if isUp
                then BinaryOperator.BinaryLessOrEqual, UpdateOperator.UpdatePlus
                else BinaryOperator.BinaryGreaterOrEqual, UpdateOperator.UpdateMinus
            statements @ [Statement.forStatement(body, (param, startAndLimit[0]),
                Expression.binaryExpression(op1, paramExpr, startAndLimit[1], Boolean),
                Expression.updateExpression(op2, paramExpr)
            )], None

    let getLocalFunctionGenericParams (_com: IDartCompiler) (ctx: Context) argTypes =
        let rec getGenParams = function
            | Fable.GenericParam(name, _constraints) -> [name]
            | t -> t.Generics |> List.collect getGenParams

        let genParams =
            (Set.empty, argTypes) ||> List.fold (fun genArgs t ->
                (genArgs, getGenParams t) ||> List.fold (fun genArgs n -> Set.add n genArgs))
            |> List.ofSeq

        match genParams, ctx.MemberGenericParams with
        | [], _ | _, [] -> genParams
        | localGenParams, memberGenParams ->
            let memberGenParams = memberGenParams |> List.map (fun p -> p.Name) |> set
            localGenParams |> List.filter (memberGenParams.Contains >> not)

    let getMemberGenericParams (com: IDartCompiler) ctx (membDecl: Fable.MemberDecl): GenericParam list =
        let fullName = membDecl.FullDisplayName
        membDecl.GenericParams |> List.map (transformGenericParam com ctx fullName)

    let getMemberArgsAndBody (com: IDartCompiler) ctx kind (genParams: Fable.GenericParam list) (args: Fable.ArgDecl list) (body: Fable.Expr) =
        let funcName, args, body =
            match kind, args with
            | Attached(isStatic=false), (thisArg::args) ->
                let body =
                    // TODO: If ident is not captured maybe we can just replace it with "this"
                    let thisArg = thisArg.Ident
                    if FableTransforms.isIdentUsed thisArg.Name body then
                        let thisKeyword = Fable.IdentExpr { thisArg with Name = "this" }
                        Fable.Let(thisArg, thisKeyword, body)
                    else body
                None, args, body
            | Attached(isStatic=true), _
            | ClassConstructor, _ -> None, args, body
            | NonAttached funcName, _ -> Some funcName, args, body
            | _ -> None, args, body

        let argsMap = args |> List.map (fun a -> a.Ident.Name, a) |> Map
        let argIdents = args |> List.map (fun a -> a.Ident)
        let ctx = { ctx with MemberGenericParams = genParams }
        let argIdents, body, returnType = transformFunction com ctx funcName argIdents body
        let args = argIdents |> List.map (fun a ->
            match Map.tryFind a.Name argsMap with
            | None -> FunctionArg(a)
            | Some a' -> FunctionArg(a, isOptional=a'.IsOptional, isNamed=a'.IsNamed)
        )
        args, body, returnType

    let transformModuleFunction (com: IDartCompiler) ctx (memb: Fable.MemberDecl) =
        let args, body, returnType = getMemberArgsAndBody com ctx (NonAttached memb.Name) memb.GenericParams memb.Args memb.Body
        let isEntryPoint =
            memb.Info.Attributes
            |> Seq.exists (fun att -> att.Entity.FullName = Atts.entryPoint)
        if isEntryPoint then
            Declaration.functionDeclaration("main", args, body, Void)
        else
            let genParams = getMemberGenericParams com ctx memb
            Declaration.functionDeclaration(memb.Name, args, body, returnType, genParams=genParams)

    // TODO: Inheriting interfaces
    let transformInterfaceDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let genParams = ent.GenericParameters |> List.map (transformGenericParam com ctx ent.FullName)
        let methods =
            ent.MembersFunctionsAndValues
            |> Seq.choose (fun m ->
                // TODO: Indexed properties
                if m.IsGetter then Some IsGetter
                elif m.IsSetter then Some IsSetter
                elif m.IsProperty then None
                else Some IsMethod
                |> Option.map (fun kind ->
                    let name = m.DisplayName
                    let args =
                        m.CurriedParameterGroups
                        |> List.concat
                        |> List.mapi (fun i p ->
                            let name =
                                match p.Name with
                                | Some name -> name
                                | None -> $"$arg{i}"
                            let t = transformType com ctx p.Type
                            FunctionArg(makeIdent t name) // TODO, isOptional=p.IsOptional, isNamed=p.IsNamed)
                        )
                    // TODO: genArgs
                    InstanceMethod(name, kind=kind, args=args, returnType=transformType com ctx m.ReturnParameter.Type)
                )
            )
            |> Seq.toList
        [Declaration.classDeclaration(decl.Name, genParams=genParams, methods=methods, isAbstract=true)]

    let transformUnionDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let extends = libTypeRef com ctx "Types" "Union" []
        let selfTypeRef = makeTypeRefFromName decl.Name []
        let implements = makeTypeRefFromName "Comparable" [selfTypeRef]
        let constructor =
            let tag = makeIdent Integer "tag"
            let fields = makeIdent (Type.List Object) "fields"
            Constructor(args=[ConsArg tag; ConsArg fields], superArgs=unnamedArgs [tag.Expr; fields.Expr], isConst=true)
        let compareTo =
            let other = makeIdent selfTypeRef "other"
            let args = [Expression.identExpression other]
            let body =
                Expression.invocationExpression(SuperExpression extends, "compareTagAndFields", args, Integer)
                |> makeReturnBlock
            InstanceMethod("compareTo", [FunctionArg other], Integer, body=body, isOverride=true)
        [Declaration.classDeclaration(
            decl.Name,
            constructor=constructor,
            extends=extends,
            implements=[implements],
            methods=[compareTo])]

    // TODO: generic args
    let transformRecordDeclaration (com: IDartCompiler) ctx (decl: Fable.ClassDecl) (ent: Fable.Entity) =
        let selfTypeRef = makeTypeRefFromName decl.Name []
        let implements = [
            libTypeRef com ctx "Types" "Record" []
            makeTypeRefFromName "Comparable" [selfTypeRef]
        ]
        let mutable hasMutableFields = false
        let fields, varDecls, consArgs =
            ent.FSharpFields
            |> List.map (fun f ->
                let kind =
                    if f.IsMutable then
                        hasMutableFields <- true
                        Var
                    else
                        Final
                let ident = transformIdentWith com ctx f.FieldType f.Name
                ident, InstanceVariable(ident, kind=kind), ConsThisArg f.Name)
            |> List.unzip3

        let constructor = Constructor(args=consArgs, isConst=not hasMutableFields)

        // TODO: implement toString
        // TODO: check if there are already custom Equals, GetHashCode and/or CompareTo implementations
        let equals =
            let other = makeIdent Object "other"

            let makeFieldEq (field: Ident) =
                let otherField = Expression.propertyAccess(other.Expr, field.Name, field.Type) 
                Expression.binaryExpression(BinaryEqual, otherField, field.Expr, Boolean)

            let rec makeFieldsEq fields acc =
                match fields with
                | [] -> acc
                | field::fields ->
                    let eq = makeFieldEq field
                    Expression.logicalExpression(LogicalAnd, eq, acc)
                    |> makeFieldsEq fields

            let typeTest =
                Expression.isExpression(other.Expr, selfTypeRef)

            let body =
                match List.rev fields with
                | [] -> typeTest
                | field::fields ->
                    let eq = makeFieldEq field |> makeFieldsEq fields
                    Expression.logicalExpression(LogicalAnd, typeTest, eq)
                |> makeReturnBlock

            InstanceMethod("==", [FunctionArg other], Boolean, body=body, kind=MethodKind.IsOperator, isOverride=true)

        let hashCode =
            let intType = Fable.Number(Int32, Fable.NumberInfo.Empty)
            let body =
                fields
                |> List.map (fun f -> Expression.propertyAccess(Expression.identExpression f, "hashCode", Integer))
                |> makeImmutableListExpr com ctx intType
                |> List.singleton
                |> libCall com ctx (numType Int32) "Util" "combineHashCodes"
                |> makeReturnBlock
            InstanceMethod("hashCode", [], Integer, kind=IsGetter, body=body, isOverride=true)

        let compareTo =
            let r = makeIdent Integer "$r"
            let other = makeIdent selfTypeRef "other"

            let makeAssign (field: Ident) =
                let otherField = Expression.propertyAccess(other.Expr, field.Name, field.Type)
                Expression.assignmentExpression(r.Expr,
                    Expression.invocationExpression(field.Expr, "compareTo", [otherField], Integer))

            let makeFieldComp (field: Ident) =
                Expression.binaryExpression(BinaryEqual, makeAssign field, Expression.integerLiteral 0, Boolean)

            let rec makeFieldsComp (fields: Ident list) (acc: Statement list) =
                match fields with
                | [] -> acc
                | field::fields ->
                    let eq = makeFieldComp field
                    [Statement.ifStatement(eq, acc)]
                    |> makeFieldsComp fields

            let body = [
                Statement.variableDeclaration(r, kind=Var)
                yield!
                    match List.rev fields with
                    | [] -> []
                    | field::fields ->
                        [makeAssign field |> ExpressionStatement]
                        |> makeFieldsComp fields
                Statement.returnStatement r.Expr
            ]

            InstanceMethod("compareTo", [FunctionArg other], Integer, body=body, isOverride=true)

        [Declaration.classDeclaration(
            decl.Name,
            constructor=constructor,
            implements=implements,
            variables=varDecls,
            methods=[equals; hashCode; compareTo])]

    let transformAttachedMember (com: IDartCompiler) ctx (memb: Fable.MemberDecl) =
        let isStatic = not memb.Info.IsInstance
        let genParams = getMemberGenericParams com ctx memb
        let args, body, returnType =
            getMemberArgsAndBody com ctx (Attached isStatic) memb.GenericParams memb.Args memb.Body

        let kind, name =
            match memb.Name, args with
            | "ToString", [] -> MethodKind.IsMethod, "toString"
            | "GetEnumerator", [] -> MethodKind.IsGetter, "iterator"
            | "System.Collections.Generic.IEnumerator`1.get_Current", _ -> MethodKind.IsGetter, "current"
            | "System.Collections.IEnumerator.MoveNext", _ -> MethodKind.IsMethod, "moveNext"
            | "GetHashCode", [] -> MethodKind.IsGetter, "hashCode"
            | "CompareTo", [_] -> MethodKind.IsMethod, "compareTo"
            | "Equals", [_] -> MethodKind.IsOperator, "=="
            | name, _ ->
                let kind =
                    if memb.Info.IsGetter then MethodKind.IsGetter
                    else if memb.Info.IsSetter then MethodKind.IsSetter
                    else MethodKind.IsMethod
                kind, Naming.sanitizeIdentForbiddenCharsWith (fun _ -> "_") name

        InstanceMethod(name, args, returnType,
                       body=body,
                       kind=kind,
                       genParams=genParams,
                       isStatic=isStatic,
                       isOverride=memb.Info.IsOverrideOrExplicitInterfaceImplementation)

    let transformClassWithImplicitConstructor (com: IDartCompiler) ctx (classEnt: Fable.Entity) (classDecl: Fable.ClassDecl) classMethods (cons: Fable.MemberDecl) =
        let genParams = classEnt.GenericParameters |> List.map (transformGenericParam com ctx classEnt.FullName)
        let classIdent = makeIdent MetaType classDecl.Name
        let classType = TypeReference(classIdent, genParams |> List.map (fun g -> Generic g.Name))
        let consArgDecls, consBody, _ = getMemberArgsAndBody com ctx ClassConstructor [] cons.Args cons.Body
        let consArgs = consArgDecls |> List.map (fun a -> a.Ident)

        let exposedCons =
            let argExprs = consArgs |> List.map Expression.identExpression
            let exposedConsBody = Expression.invocationExpression(classIdent.Expr, argExprs, classType) |> makeReturnBlock
            Declaration.functionDeclaration(cons.Name, consArgDecls, exposedConsBody, classType, genParams=genParams)

        // TODO: Analize the constructor body to see if we can assign fields
        // directly and prevent declarign them as late final
//        let hasMutableFields = classEnt.FSharpFields |> List.exists (fun f -> f.IsMutable)
        let variables =
            classEnt.FSharpFields |> List.map (fun f ->
                let t = transformType com ctx f.FieldType
                let ident = makeIdent t f.Name
                let kind = if f.IsMutable then Var else Final
                InstanceVariable(ident, kind=kind, isLate=true))

        let constructor = Constructor(
            args = (List.map ConsArg consArgs),
            body = consBody,
            superArgs = (extractBaseArgs com ctx classDecl)
//            isConst = not hasMutableFields
        )

        let mutable implementsIterable = None
        let implements =
            classEnt.DeclaredInterfaces
            |> Seq.toList
            |> List.collect (fun ifc ->
                let t = transformDeclaredType com ctx ifc.Entity ifc.GenericArgs
                match ifc.Entity.FullName with
                | Types.ienumerableGeneric -> implementsIterable <- Some t; []
                | Types.ienumerable -> []
                | Types.ienumeratorGeneric -> [t; TypeReference(libValue com ctx Fable.MetaType "Types" "IDisposable", [])]
                | _ -> [t])

        let extends =
            match implementsIterable, classEnt.BaseType with
            | Some iterable, Some _ ->
                $"Types implementing IEnumerable cannot inherit from another class: {classEnt.FullName}"
                |> addError com [] None
                Some iterable
            | Some iterable, None -> Some iterable
            | None, Some e ->
                Fable.DeclaredType(e.Entity, e.GenericArgs)
                |> transformType com ctx
                |> Some
            | None, None -> None

        [
            Declaration.classDeclaration(
                classDecl.Name,
                genParams = genParams,
                ?extends = extends,
                implements = implements,
                constructor = constructor,
                methods = classMethods,
                variables = variables)
            exposedCons
        ]

    let transformDeclaration (com: IDartCompiler) ctx decl =
        let withCurrentScope ctx (usedNames: Set<string>) f =
            let ctx = { ctx with UsedNames = { ctx.UsedNames with CurrentDeclarationScope = HashSet usedNames } }
            let result = f ctx
            ctx.UsedNames.DeclarationScopes.UnionWith(ctx.UsedNames.CurrentDeclarationScope)
            result

        match decl with
        | Fable.ModuleDeclaration decl ->
            decl.Members |> List.collect (transformDeclaration com ctx)

        | Fable.ActionDeclaration d ->
            "Standalone actions are not supported in Dart, please use an initialize function"
            |> addError com [] d.Body.Range
            []

        // TODO: Prefix non-public values with underscore or raise warning?
        | Fable.MemberDeclaration memb ->
            withCurrentScope ctx memb.UsedNames <| fun ctx ->
                if memb.Info.IsValue then
                    let ident = transformIdentWith com ctx memb.Body.Type memb.Name
                    let statements, expr = transformAndCaptureExpr com ctx memb.Body
                    let value =
                        match statements with
                        | [] -> expr
                        | statements ->
                            // We may need to distinguish between void and unit for these cases
                            statements @ [ReturnStatement(expr)] |> iife com ctx Void
                    let kind, value = getVarKind ctx memb.Info.IsMutable value
                    [Declaration.variableDeclaration(ident, kind, value)]
                else
                    [transformModuleFunction com ctx memb]

        | Fable.ClassDeclaration decl ->
            let entRef = decl.Entity
            let ent = com.GetEntity(entRef)
            if ent.IsInterface then
                transformInterfaceDeclaration com ctx decl ent
            else
                let instanceMethods =
                    decl.AttachedMembers |> List.choose (fun memb ->
                        match memb.Name with
                        | "System.Collections.IEnumerable.GetEnumerator"
                        | "System.Collections.IEnumerator.get_Current"
                        | "System.Collections.IEnumerator.Reset" -> None
                        | _ ->
                            withCurrentScope ctx memb.UsedNames (fun ctx ->
                                transformAttachedMember com ctx memb) |> Some)

                // TODO: Implementing interfaces
                match decl.Constructor with
                | Some cons ->
                    withCurrentScope ctx cons.UsedNames <| fun ctx ->
                        transformClassWithImplicitConstructor com ctx ent decl instanceMethods cons
                | None ->
                    if ent.IsFSharpUnion then transformUnionDeclaration com ctx decl ent
                    elif ent.IsFSharpRecord then transformRecordDeclaration com ctx decl ent
                    else failwith "TODO: transformClassWithCompilerGeneratedConstructor"

    let getIdentForImport (ctx: Context) (path: string) =
        Path.GetFileNameWithoutExtension(path).Replace(".", "_").Replace(":", "_")
        |> Naming.applyCaseRule Core.CaseRules.SnakeCase
        |> getUniqueNameInRootScope ctx

module Compiler =
    open Util

    type DartCompiler (com: Compiler) =
        let onlyOnceErrors = HashSet<string>()
        let imports = Dictionary<string, Import>()

        interface IDartCompiler with
            member _.WarnOnlyOnce(msg, ?values, ?range) =
                if onlyOnceErrors.Add(msg) then
                    let msg =
                        match values with
                        | None -> msg
                        | Some values -> System.String.Format(msg, values)
                    addWarning com [] range msg

            member _.ErrorOnlyOnce(msg, ?values, ?range) =
                if onlyOnceErrors.Add(msg) then
                    let msg =
                        match values with
                        | None -> msg
                        | Some values -> System.String.Format(msg, values)
                    addError com [] range msg

            member com.GetImportIdent(ctx, selector, path, t, r) =
                let localId =
                    match imports.TryGetValue(path) with
                    | true, i ->
                        match i.LocalIdent with
                        | Some localId -> localId
                        | None ->
                            let localId = getIdentForImport ctx path
                            imports[path] <- { Path = path; LocalIdent = Some localId }
                            localId
                    | false, _ ->
                        let localId = getIdentForImport ctx path
                        imports.Add(path, { Path = path; LocalIdent = Some localId })
                        localId
                let t = transformType com ctx t
                let ident = makeIdent t localId
                match selector with
                | Naming.placeholder ->
                    "`importMember` must be assigned to a variable"
                    |> addError com [] r
                    ident
                | "*" -> ident
                | selector -> { ident with ImportModule = Some ident.Name; Name = selector }

            member _.GetAllImports() = imports.Values |> Seq.toList
            member this.TransformType(ctx, t) = transformType this ctx t
            member this.Transform(ctx, ret, e) = transform this ctx ret e
            member this.TransformFunction(ctx, name, args, body) = transformFunction this ctx name args body

        interface Compiler with
            member _.Options = com.Options
            member _.Plugins = com.Plugins
            member _.LibraryDir = com.LibraryDir
            member _.CurrentFile = com.CurrentFile
            member _.OutputDir = com.OutputDir
            member _.OutputType = com.OutputType
            member _.ProjectFile = com.ProjectFile
            member _.IsPrecompilingInlineFunction = com.IsPrecompilingInlineFunction
            member _.WillPrecompileInlineFunction(file) = com.WillPrecompileInlineFunction(file)
            member _.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
            member _.GetRootModule(fileName) = com.GetRootModule(fileName)
            member _.TryGetEntity(fullName) = com.TryGetEntity(fullName)
            member _.GetInlineExpr(fullName) = com.GetInlineExpr(fullName)
            member _.AddWatchDependency(fileName) = com.AddWatchDependency(fileName)
            member _.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
                com.AddLog(msg, severity, ?range=range, ?fileName=fileName, ?tag=tag)

    let makeCompiler com = DartCompiler(com)

    let transformFile (com: Compiler) (file: Fable.File) =
        let com = makeCompiler com :> IDartCompiler
        let declScopes =
            let hs = HashSet()
            for decl in file.Declarations do
                hs.UnionWith(decl.UsedNames)
            hs

        let ctx =
          { File = file
            UsedNames = { RootScope = HashSet file.UsedNamesInRootScope
                          DeclarationScopes = declScopes
                          CurrentDeclarationScope = Unchecked.defaultof<_> }
            DecisionTargets = []
            MemberGenericParams = []
            TailCallOpportunity = None
            OptimizeTailCall = fun () -> ()
            ConstIdents = Set.empty }
        let rootDecls = List.collect (transformDeclaration com ctx) file.Declarations
        let imports = com.GetAllImports()
        { File.Imports = imports
          Declarations = rootDecls }
