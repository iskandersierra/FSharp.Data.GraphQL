﻿namespace FSharp.Data.GraphQL

open System
open FSharp.Core
open FSharp.Data
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Parser
open FSharp.Data.GraphQL.Client
open System.Collections.Generic
open FSharp.Data.GraphQL.Types.Introspection
open FSharp.Data.GraphQL.Ast
open FSharp.Data.GraphQL.Ast.Extensions
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Quotations
open System.Reflection
open System.Text
open Microsoft.FSharp.Reflection
open System.Collections

module QuotationHelpers = 
    let rec coerceValues fieldTypeLookup fields = 
        let arrayExpr (arrayType : Type) (v : obj) =
            let typ = arrayType.GetElementType()
            let instance =
                match v with
                | :? IEnumerable as x -> Seq.cast<obj> x |> Array.ofSeq
                | _ -> failwith "Unexpected array value."
            let exprs = coerceValues (fun _ -> typ) instance
            Expr.NewArray(typ, exprs)
        let tupleExpr (tupleType : Type) (v : obj) =
            let typ = FSharpType.GetTupleElements tupleType |> Array.mapi (fun i t -> i, t) |> Map.ofArray
            let fieldTypeLookup i = typ.[i]
            let fields = FSharpValue.GetTupleFields v
            let exprs = coerceValues fieldTypeLookup fields
            Expr.NewTuple(exprs)
        Array.mapi (fun i v ->
                let expr = 
                    if v = null then simpleTypeExpr v
                    else
                        let tpy = v.GetType()
                        if tpy.IsArray then arrayExpr tpy v
                        elif FSharpType.IsTuple tpy then tupleExpr tpy v
                        elif FSharpType.IsUnion tpy then unionExpr v |> snd
                        elif FSharpType.IsRecord tpy then recordExpr v |> snd
                        else simpleTypeExpr v
                Expr.Coerce(expr, fieldTypeLookup i)
        ) fields |> List.ofArray
    
    and simpleTypeExpr instance = Expr.Value(instance)

    and unionExpr instance = 
        let caseInfo, fields = FSharpValue.GetUnionFields(instance, instance.GetType())    
        let fieldInfo = caseInfo.GetFields()
        let fieldTypeLookup indx = fieldInfo.[indx].PropertyType
        caseInfo.DeclaringType, Expr.NewUnionCase(caseInfo, coerceValues fieldTypeLookup fields)

    and recordExpr instance = 
        let tpy = instance.GetType()
        let fields = FSharpValue.GetRecordFields(instance)
        let fieldInfo = FSharpType.GetRecordFields(tpy)
        let fieldTypeLookup indx = fieldInfo.[indx].PropertyType
        tpy, Expr.NewRecord(instance.GetType(), coerceValues fieldTypeLookup fields)

    and arrayExpr (instance : 'a array) =
        let typ = typeof<'a>
        let arrayType = instance.GetType()
        let exprs = coerceValues (fun _ -> typ) (instance |> Array.map box)
        arrayType, Expr.NewArray(typ, exprs)

    let createLetExpr varType instance body args = 
        let var = Var("instance", varType)  
        Expr.Let(var, instance, body args (Expr.Var(var)))

    let quoteUnion instance = unionExpr instance ||> createLetExpr
    let quoteRecord instance = recordExpr instance ||> createLetExpr
    let quoteArray instance = arrayExpr instance ||> createLetExpr

type EnumBase (name : string, value : string) =
    member __.Name = name

    member __.Value = value

    static member internal MakeProvidedType(name, items : string seq) =
        let tdef = ProvidedTypeDefinition(name, Some typeof<EnumBase>, nonNullable = true, isSealed = true)
        for item in items do
            let getterCode (_ : Expr list) =
                Expr.NewObject(EnumBase.Constructor, [ <@@ name @@>; <@@ item @@> ])
            let idef = ProvidedProperty(item, tdef, getterCode, isStatic = true)
            tdef.AddMember(idef)
        tdef

    static member internal Constructor = typeof<EnumBase>.GetConstructors().[0]

    override x.ToString() = x.Value

    override x.Equals(other : obj) =
        match other with
        | :? EnumBase as other -> x.Name = other.Name && x.Value = other.Value
        | _ -> false

    override x.GetHashCode() = x.Name.GetHashCode() ^^^ x.Value.GetHashCode()

type RecordBase (properties : (string * obj) list) =
    member internal __.Properties = properties

    static member internal MakeProvidedType(name : string, properties : (string * Type) list, baseType : Type option) =
        let baseType = Option.defaultValue typeof<RecordBase> baseType
        let tdef = ProvidedTypeDefinition(name.FirstCharUpper(), Some baseType, nonNullable = true, isSealed = true)
        let propertyMapper (pname : string, ptype : Type) : MemberInfo =
            let pname = pname.FirstCharUpper()
            let getterCode (args : Expr list) =
                <@@ let this = %%args.[0] : RecordBase
                    let propdef = typeof<RecordBase>.GetProperty("Properties", BindingFlags.NonPublic ||| BindingFlags.Instance)
                    let props = propdef.GetValue(this) :?> (string * obj) list
                    match props |> List.tryFind (fun (name, _) -> name = pname) with
                    | Some (_, value) -> value
                    | None -> failwithf "Expected to find property \"%s\" under properties %A, but was not found." pname (List.map snd props) @@>
            upcast ProvidedProperty(pname, ptype, getterCode)
        let pdefs = properties |> List.map propertyMapper
        tdef.AddMembers(pdefs)
        tdef

    static member internal Constructor = typeof<RecordBase>.GetConstructors().[0]

    static member internal NewObjectExpr(properties : (string * obj) list) =
        let names = properties |> List.map fst
        let values = properties |> List.map snd
        Expr.NewObject(RecordBase.Constructor, [ <@@ List.zip names values @@> ])

    override x.ToString() =
        let sb = StringBuilder()
        sb.Append("{") |> ignore
        let rec printProperties (properties : (string * obj) list) =
            match properties with
            | [] -> ()
            | [name, value] -> sb.Append(sprintf "%s = %A;" name value) |> ignore
            | (name, value) :: tail -> sb.AppendLine(sprintf "%s = %A;" name value) |> ignore; printProperties tail
        printProperties x.Properties
        sb.Append("}") |> ignore
        sb.ToString()

    member x.Equals(other : RecordBase) =
        x.Properties = other.Properties

    override x.Equals(other : obj) =
        match other with
        | :? RecordBase as other -> x.Equals(other)
        | _ -> false

    override x.GetHashCode() = x.Properties.GetHashCode()

    interface IEquatable<RecordBase> with
        member x.Equals(other) = x.Equals(other)

module Types =
    let scalar =
        [| "Int", typeof<int>
           "Boolean", typeof<bool>
           "Date", typeof<DateTime>
           "Float", typeof<float>
           "ID", typeof<string>
           "String", typeof<string>
           "URI", typeof<Uri> |]
        |> Map.ofArray

    let schema =
        [| "__TypeKind"
           "__DirectiveLocation"
           "__Type"
           "__InputValue"
           "__Field"
           "__EnumValue"
           "__Directive"
           "__Schema" |]

module JsonValueHelper =
    let getResponseFields (responseJson : JsonValue) =
        match responseJson with
        | JsonValue.Record fields -> fields
        | _ -> failwithf "Expected root type to be a Record type, but type is %A." responseJson

    let getResponseDataFields (responseJson : JsonValue) =
        match getResponseFields responseJson |> Array.tryFind (fun (name, _) -> name = "data") with
        | Some (_, data) -> 
            match data with
            | JsonValue.Record fields -> fields
            | _ -> failwithf "Expected data field of root type to be a Record type, but type is %A." data
        | None -> failwith "Expected root type to have a \"data\" field, but it was not found."

    let getResponseCustomFields (responseJson : JsonValue) =
        getResponseFields responseJson
        |> Array.filter (fun (name, _) -> name <> "data")

    let private removeTypeNameField (fields : (string * JsonValue) list) =
        fields |> List.filter (fun (name, _) -> name <> "__typename")

    let firstUpper (name : string, value) =
        name.FirstCharUpper(), value

    let getFields (schemaType : IntrospectionType) =
        match schemaType.Fields with
        | None -> Map.empty
        | Some fields -> fields |> Array.map (fun field -> field.Name.FirstCharUpper(), field.Type) |> Map.ofSeq

    let getTypeName (fields : (string * JsonValue) seq) =
        fields
        |> Seq.tryFind (fun (name, _) -> name = "__typename")
        |> Option.map (fun (_, value) ->
            match value with
            | JsonValue.String x -> x
            | _ -> failwithf "Expected \"__typename\" field to be a string field, but it was %A." value)

    let rec getFieldValue (schemaTypes : Map<string, IntrospectionType>) (fieldType : IntrospectionTypeRef) (fieldName : string, fieldValue : JsonValue) =
        let getOptionCases (t: Type) =
            let otype = typedefof<_ option>.MakeGenericType(t)
            let cases = FSharpType.GetUnionCases(otype)
            let some = cases |> Array.find (fun c -> c.Name = "Some")
            let none = cases |> Array.find (fun c -> c.Name = "None")
            (some, none, otype)
        let makeSome (value : obj) =
            let (some, _, _) = getOptionCases (value.GetType())
            FSharpValue.MakeUnion(some, [|value|])
        let makeNone (t : Type) =
            let (_, none, _) = getOptionCases t
            FSharpValue.MakeUnion(none, [||])
        let makeArray (itype : Type) (items : obj []) =
            if Array.exists (fun x -> isNull x) items
            then failwith "Array is an array of non null items, but a null item was found."
            else
                let arr = Array.CreateInstance(itype, items.Length)
                items |> Array.iteri (fun i x -> arr.SetValue(x, i))
                box arr
        let makeOptionArray (itype : Type) (items : obj []) =
            let (some, none, otype) = getOptionCases(itype)
            let arr = Array.CreateInstance(otype, items.Length)
            let mapper (i : int) (x : obj) =
                if isNull x
                then arr.SetValue(FSharpValue.MakeUnion(none, [||]), i)
                else arr.SetValue(FSharpValue.MakeUnion(some, [|x|]), i)
            items |> Array.iteri mapper
            box arr
        let getScalarType (typeRef : IntrospectionTypeRef) =
            let getType (typeName : string) =
                match Map.tryFind typeName Types.scalar with
                | Some t -> t
                | None -> failwithf "Unsupported scalar type \"%s\"." typeName
            match typeRef.Name with
            | Some name -> getType name
            | None -> failwith "Expected scalar type to have a name, but it does not have one."
        let rec helper (useOption : bool) (fieldType : IntrospectionTypeRef) (fieldValue : JsonValue) : obj =
            let makeSomeIfNeeded value =
                match fieldType.Kind with
                | TypeKind.NON_NULL | TypeKind.LIST -> value
                | _ when useOption -> makeSome value
                | _ -> value
            let makeNoneIfNeeded (t : Type) =
                match fieldType.Kind with
                | TypeKind.NON_NULL | TypeKind.LIST -> null
                | _ when useOption -> makeNone t
                | _ -> null
            match fieldValue with
            | JsonValue.Array items ->
                let itemType =
                    match fieldType.OfType with
                    | Some t when t.Kind = TypeKind.LIST && t.OfType.IsSome -> t.OfType.Value
                    | _ -> failwithf "Expected field to be a list type with an underlying item, but it is %A." fieldType.OfType
                let items = items |> Array.map (helper false itemType)
                match itemType.Kind with
                | TypeKind.NON_NULL -> 
                    match itemType.OfType with
                    | Some itemType ->
                        match itemType.Kind with
                        | TypeKind.NON_NULL -> failwith "Schema definition is not supported: a non null type of a non null type was specified."
                        | TypeKind.OBJECT | TypeKind.INTERFACE | TypeKind.UNION -> makeArray typeof<RecordBase> items
                        | TypeKind.ENUM -> makeArray typeof<EnumBase> items
                        | TypeKind.SCALAR -> makeArray (getScalarType itemType) items
                        | kind -> failwithf "Unsupported type kind \"%A\"." kind
                    | None -> failwith "Item type is a non null type, but no underlying type exists on the schema definition of the type."
                | TypeKind.OBJECT | TypeKind.INTERFACE | TypeKind.UNION -> makeOptionArray typeof<RecordBase> items |> makeSomeIfNeeded
                | TypeKind.ENUM -> makeOptionArray typeof<EnumBase> items |> makeSomeIfNeeded
                | TypeKind.SCALAR -> makeOptionArray (getScalarType itemType) items |> makeSomeIfNeeded
                | kind -> failwithf "Unsupported type kind \"%A\"." kind
            | JsonValue.Record props -> 
                let typeName =
                    match getTypeName props with
                    | Some typeName -> typeName
                    | None -> failwith "Expected type to have a \"__typename\" field, but it was not found."
                let schemaType =
                    match schemaTypes.TryFind(typeName) with
                    | Some tref -> tref
                    | None -> failwithf "Expected to find a type \"%s\" in the schema types, but it was not found." typeName
                let fields = getFields schemaType
                let mapper (name : string, value : JsonValue) =
                    match fields.TryFind(name) with
                    | Some fieldType -> name, (helper true fieldType value)
                    | None -> failwithf "Expected to find a field named \"%s\" on the type %s, but found none." name schemaType.Name
                props
                |> List.ofArray
                |> removeTypeNameField
                |> List.map (firstUpper >> mapper)
                |> RecordBase
                |> makeSomeIfNeeded
            | JsonValue.Boolean b -> makeSomeIfNeeded b
            | JsonValue.Float f -> makeSomeIfNeeded f
            | JsonValue.Null ->
                match fieldType.Kind with
                | TypeKind.NON_NULL -> failwith "Expected a non null item from the schema definition, but a null item was found in the response."
                | TypeKind.OBJECT | TypeKind.INTERFACE | TypeKind.UNION -> makeNoneIfNeeded typeof<RecordBase>
                | TypeKind.ENUM -> makeNoneIfNeeded typeof<EnumBase>
                | TypeKind.SCALAR -> getScalarType fieldType |> makeNoneIfNeeded
                | kind -> failwithf "Unsupported type kind \"%A\"." kind
            | JsonValue.Number n -> makeSomeIfNeeded (float n)
            | JsonValue.String s -> 
                match fieldType.Kind with
                | TypeKind.NON_NULL ->
                    match fieldType.OfType with
                    | Some itemType ->
                        match itemType.Kind with
                        | TypeKind.NON_NULL -> failwith "Schema definition is not supported: a non null type of a non null type was specified."
                        | TypeKind.SCALAR -> 
                            match itemType.Name with
                            | Some "String" -> box s
                            | _ -> failwith "A string type was received in the query response item, but the matching schema field is not a string based type."
                        | TypeKind.ENUM when itemType.Name.IsSome -> EnumBase(itemType.Name.Value, s) |> box
                        | _ -> failwith "A string type was received in the query response item, but the matching schema field is not a string or an enum type."
                    | None -> failwith "Item type is a non null type, but no underlying type exists on the schema definition of the type."
                | TypeKind.SCALAR ->
                    match fieldType.Name with
                    | Some "String" -> makeSomeIfNeeded s
                    | _ -> failwith "A string type was received in the query response item, but the matching schema field is not a string based type."
                | _ -> failwith "A string type was received in the query response item, but the matching schema field is not a string based type or an enum type."
        fieldName, (helper true fieldType fieldValue)

    let getFieldValues (schemaTypes : Map<string, IntrospectionType>) (schemaType : IntrospectionType) (fields : (string * JsonValue) list) =
        let mapper (name : string, value : JsonValue) =
            let fields = getFields schemaType
            match fields.TryFind(name) with
            | Some fieldType -> getFieldValue schemaTypes fieldType (name, value)
            | None -> failwithf "Expected to find a field named \"%s\" on the type %s, but found none." name schemaType.Name
        removeTypeNameField fields
        |> List.map (firstUpper >> mapper)

type ProvidedTypeKind =
    | EnumType of name : string
    | OutputType of path : string list * name : string

type OperationResultProvidingInformation =
    { SchemaTypeNames : string []
      SchemaTypes : IntrospectionType []
      QueryTypeName : string }

type OperationResultBase (responseJson : string) =
    member __.ResponseJson = JsonValue.Parse responseJson

    member this.DataFields = JsonValueHelper.getResponseDataFields this.ResponseJson |> List.ofArray
    
    member this.CustomFields = JsonValueHelper.getResponseCustomFields this.ResponseJson |> List.ofArray

    static member internal MakeProvidedType(providingInformation : OperationResultProvidingInformation, outputQueryType : ProvidedTypeDefinition) =
        let tdef = ProvidedTypeDefinition("OperationResult", Some typeof<OperationResultBase>, nonNullable = true)
        let qpdef = 
            let getterCode =
                QuotationHelpers.quoteRecord providingInformation (fun (args : Expr list) var ->
                    <@@ let this = %%args.[0] : OperationResultBase
                        let info = %%var : OperationResultProvidingInformation
                        let schemaTypes = Map.ofArray (Array.zip info.SchemaTypeNames info.SchemaTypes)
                        let queryType =
                            match schemaTypes.TryFind(info.QueryTypeName) with
                            | Some def -> def
                            | _ -> failwithf "Query type %s could not be found on the schema types." info.QueryTypeName
                        let fieldValues = JsonValueHelper.getFieldValues schemaTypes queryType this.DataFields
                        RecordBase(fieldValues) @@>)
            ProvidedProperty("Data", outputQueryType, getterCode)
        tdef.AddMember(qpdef)
        tdef

type OperationBase (serverUrl : string, customHttpHeaders : seq<string * string> option, operationName : string option) =
    member __.ServerUrl = serverUrl

    member __.CustomHttpHeaders = customHttpHeaders

    member __.OperationName = operationName

    static member internal MakeProvidedType(requestHashCode : int, serverUrl, operationName, query, queryTypeName : string, schemaTypes : (string * IntrospectionType) [], outputTypes : Map<ProvidedTypeKind, ProvidedTypeDefinition>) =
        let className = sprintf "Operation%s" (requestHashCode.ToString("x2"))
        let tdef = ProvidedTypeDefinition(className, Some typeof<OperationBase>)
        // We need to convert the operation name to a nullable string instead of an option here,
        // because we are going to use it inside a quotation, and quotations have issues with options as constant values.
        let operationName = Option.toObj operationName
        let outputQueryType =
            match outputTypes.TryFind(OutputType ([], queryTypeName)) with
            | Some tdef -> tdef
            | _ -> failwithf "Query type %s could not be found on the provided types. This could be a internal bug. Please report the author." queryTypeName
        let info = 
            { SchemaTypeNames = schemaTypes |> Seq.map (fst >> (fun name -> name.FirstCharUpper())) |> Array.ofSeq
              SchemaTypes = schemaTypes |> Seq.map snd |> Array.ofSeq
              QueryTypeName = queryTypeName }
        let rtdef = OperationResultBase.MakeProvidedType(info, outputQueryType)
        // TODO : Parse query parameters in the method args
        let invoker (args : Expr list) =
            <@@ let request =
                    { ServerUrl = serverUrl
                      CustomHeaders = None
                      OperationName = Option.ofObj operationName
                      Query = query
                      Variables = None }
                let responseJson = GraphQLClient.sendRequest request
                OperationResultBase(responseJson) @@>
        let mdef = ProvidedMethod("Run", [], rtdef, invoker)
        let members : MemberInfo list = [rtdef; mdef]
        tdef.AddMembers(members)
        tdef

type ContextBase (serverUrl : string, schema : IntrospectionSchema) =
    static member private GetSchemaTypes (schema : IntrospectionSchema) =
        let isScalarType (name : string) =
            Types.scalar |> Map.containsKey name
        let isIntrospectionType (name : string) =
            Types.schema |> Array.contains name
        schema.Types
        |> Array.filter (fun t -> not (isIntrospectionType t.Name) && not (isScalarType t.Name))
        |> Array.map (fun t -> t.Name, t)
    static member private BuildOutputTypes(schemaTypes : (string * IntrospectionType) [], operationName : string option, queryAst : Document, responseJson : string) =
        let responseJson = JsonValue.Parse responseJson
        let schemaTypes = Map.ofArray schemaTypes
        let providedTypes = Dictionary<ProvidedTypeKind, ProvidedTypeDefinition>()
        let createEnumType (t : IntrospectionType) =
            match t.EnumValues with
            | Some enumValues -> 
                let edef = EnumBase.MakeProvidedType(t.Name, enumValues |> Array.map (fun x -> x.Name))
                providedTypes.Add(EnumType t.Name, edef)
            | None -> failwithf "Type %s is a enum type, but no enum values were found for this type." t.Name
        schemaTypes
        |> Map.filter (fun _ t -> t.Kind = TypeKind.ENUM)
        |> Map.iter (fun _ t -> createEnumType t)
        let astInfoMap = 
            match queryAst.GetInfoMap() |> Map.tryFind operationName with
            | Some info -> info
            | None ->
                match operationName with
                | Some name -> failwithf "Operation \"%s\" was not found in query document." name
                | None -> failwith "No unamed operation was found in query document."
        let getAstInfo (path : string list) =
            match astInfoMap |> Map.tryFind path with
            | Some ast -> 
                let typeFields = 
                    ast
                    |> List.choose (function | TypeField name -> Some name | _ -> None)
                let fragmentFields = 
                    ast
                    |> List.choose (function | TypeField _ -> None | FragmentField (tc, name) -> Some (tc, name))
                    |> List.groupBy fst
                    |> List.map (fun (key, items) -> key, (items |> List.map snd |> List.rev))
                    |> Map.ofList
                typeFields, fragmentFields
            | None -> failwithf "Property \"%s\" is a union or interface type, but no inheritance information could be determined from the input query." path.Head
        //let getFragmentFields (typeName : string) (path : string list) (fields : (string * JsonValue) []) =
        //    match getAstInfo path |> snd |> Map.tryFind typeName with
        //    | Some fragmentFields -> fields |> Array.filter (fun (name, _) -> List.contains name fragmentFields || name = "__typename")
        //    | None -> [||]
        let getTypeFields (path : string list) (fields : (string * JsonValue) []) =
            let astInfo = getAstInfo path
            let typeFields = fst astInfo
            fields |> Array.filter (fun (name, _) -> List.contains name typeFields || name = "__typename")
        let buildOutputTypes (fields : (string * JsonValue) []) =
            let getScalarType (typeName : string) =
                match Types.scalar |> Map.tryFind typeName with
                | Some t -> t
                | None -> failwithf "Scalar type %s is not supported." typeName
            let getEnumType (typeName : string) =
                let key = EnumType typeName
                if providedTypes.ContainsKey(key)
                then providedTypes.[key]
                else failwithf "Enum type %s was not found in the schema." typeName
            let rec getRecordOrInterfaceType (path : string list) (typeName : string option) (baseType : Type option) (fields : (string * JsonValue) []) =
                let helper (path : string list) typeName (fields : (string * JsonValue) []) (schemaType : IntrospectionType) =
                    let key = OutputType (path, typeName)
                    if providedTypes.ContainsKey(key)
                    then providedTypes.[key]
                    else
                        let properties =
                            let fields =
                                match schemaType.Kind with
                                | TypeKind.OBJECT -> fields
                                | TypeKind.INTERFACE | TypeKind.UNION -> getTypeFields path fields
                                | _ -> failwithf "Type \"%s\" is not a Record, Union or Interface type." schemaType.Name
                            match fields with
                            | [||] -> []
                            | _ -> getProperties path fields schemaType
                        let outputType =
                            match schemaType.Kind with
                            | TypeKind.OBJECT -> RecordBase.MakeProvidedType(typeName, properties, baseType)
                            | TypeKind.INTERFACE | TypeKind.UNION -> RecordBase.MakeProvidedType(typeName, properties, None)
                            | _ -> failwithf "Type \"%s\" is not a Record, Union or Interface type." schemaType.Name
                        providedTypes.Add(key, outputType)
                        outputType
                match typeName |> Option.orElse (JsonValueHelper.getTypeName fields) with
                | Some typeName ->
                    match schemaTypes |> Map.tryFind typeName with
                    | Some schemaType -> helper path typeName fields schemaType
                    | None -> failwithf "Expected to find a type \"%s\" on schema, but it was not found." typeName
                | None -> failwith "Expected type to have a \"__typename\" field, but it was not found."
            and getProperties (path : string list) (fields : (string * JsonValue) []) (schemaType : IntrospectionType) =
                let getFieldType (name : string) =
                    match schemaType.Fields with
                    | Some fields ->
                        match fields |> Array.tryFind (fun f -> f.Name = name) with
                        | Some field -> field.Type
                        | None -> failwithf "Expected type \"%s\" to have a field \"%s\", but it was not found in schema." schemaType.Name name
                    | None -> failwithf "Expected type \"%s\" to have fields, but it does not have any field." schemaType.Name
                let makeOption (name : string, t : Type) = name, typedefof<_ option>.MakeGenericType(t)
                let makeArray (name : string, t : Type) = name, t.MakeArrayType()
                let unwrapOption (name: string, t : Type) = 
                    if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ option>
                    then name, t.GetGenericArguments().[0]
                    else failwithf "Expected native type of property \"%s\" to be an option type, but it is %s." name t.Name
                let unwrapOptionArray (name : string, t : Type) =
                    if t.IsArray
                    then (name, t.GetElementType()) |> unwrapOption |> makeArray
                    else failwithf "Expected type of property \"%s\" to be an array, but it is %s" name t.Name
                let rec getListProperty (name : string, items : JsonValue [], tref : IntrospectionTypeRef) =
                    let path = name :: path
                    match tref.Kind with
                    | TypeKind.NON_NULL ->
                        match tref.OfType with
                        | Some tref when tref.Kind <> TypeKind.NON_NULL -> getListProperty (name, items, tref) |> unwrapOptionArray
                        | _ -> failwithf "Property \"%s\" is a list of a non-null type, but it does not have an underlying type, or its underlying type is no supported." name
                    | TypeKind.UNION | TypeKind.INTERFACE ->
                        if tref.Name.IsSome
                        then
                            let itemTypeMapper = function
                                | JsonValue.Record fields -> getRecordOrInterfaceType path tref.Name None fields
                                | other -> failwithf "Expected property \"%s\" to be a Record type, but it is %A." name other
                            let baseType : Type = upcast (Array.head items |> itemTypeMapper)
                            (name, baseType) |> makeOption |> makeArray
                        else failwithf "Property \"%s\" is an union or interface type, but it does not have a type name, or its base type does not have a name." name
                    | TypeKind.OBJECT ->
                        let itemTypeMapper = function
                            | JsonValue.Record fields ->
                                match JsonValueHelper.getTypeName fields with
                                | Some typeName -> getRecordOrInterfaceType path (Some typeName) None fields
                                | None -> failwith "Expected type to have a \"__typename\" field, but it was not found."
                            | other -> failwithf "Expected property \"%s\" to be a Record type, but it is %A." name other
                        let itemType : Type = upcast (Array.head items |> itemTypeMapper)
                        (name, itemType) |> makeOption |> makeArray
                    | TypeKind.ENUM ->
                        match tref.Name with
                        | Some typeName -> (name, getEnumType typeName) |> makeOption |> makeArray
                        | None -> failwith "Expected enum type to have a name, but it does not have one."
                    | kind -> failwithf "Unsupported type kind \"%A\"." kind
                let rec getProperty (name : string, value : JsonValue, tref : IntrospectionTypeRef) =
                    match tref.Kind with
                    | TypeKind.NON_NULL ->
                        match tref.OfType with
                        | Some tref when tref.Kind <> TypeKind.NON_NULL -> getProperty (name, value, tref) |> unwrapOption
                        | _ -> failwithf "Property \"%s\" is a non-null type, but it does not have an underlying type, or its underlying type is no supported." name
                    | TypeKind.LIST ->
                        match tref.OfType, value with
                        | Some tref, JsonValue.Array items -> getListProperty (name, items, tref) |> makeOption
                        | _ -> failwithf "Property \"%s\" is a list type, but it does not have an underlying type, or its combination of type and the response value is not supported." name
                    | TypeKind.OBJECT | TypeKind.INTERFACE | TypeKind.UNION ->
                        match value with
                        | JsonValue.Record fields -> (name, getRecordOrInterfaceType (name :: path) None None fields) |> makeOption
                        | _ -> failwithf "Expected property \"%s\" to be a Record type, but it is %A." name value
                    | TypeKind.SCALAR -> 
                        match tref.Name with
                        | Some typeName -> (name, getScalarType typeName) |> makeOption
                        | None -> failwith "Expected scalar type to have a name, but it does not have one."
                    | TypeKind.ENUM ->
                        match tref.Name with
                        | Some typeName -> (name, getEnumType typeName) |> makeOption
                        | None -> failwith "Expected enum type to have a name, but it does not have one."
                    | kind -> failwithf "Unsupported type kind \"%A\"." kind
                fields
                |> Array.filter (fun (name, _) -> name <> "__typename")
                |> Array.map ((fun (name, value) -> name, value, getFieldType name) >> getProperty)
                |> List.ofArray
            getRecordOrInterfaceType [] None None fields |> ignore
        JsonValueHelper.getResponseDataFields responseJson |> buildOutputTypes
        providedTypes |> Seq.map (|KeyValue|) |> Map.ofSeq

    member __.ServerUrl = serverUrl

    member __.Schema = schema

    static member internal MakeProvidedType(schema : IntrospectionSchema, serverUrl : string) =
        let tdef = ProvidedTypeDefinition("Context", Some typeof<ContextBase>)
        let mdef =
            let sprm = 
                [ ProvidedStaticParameter("queryString", typeof<string>)
                  ProvidedStaticParameter("operationName", typeof<string>, "") ]
            let smdef = ProvidedMethod("Query", [], typeof<OperationBase>)
            let genfn (mname : string) (args : obj []) =
                let originalQuery = args.[0] :?> string
                let queryAst = parse originalQuery
                let query = queryAst.ToQueryString(QueryStringPrintingOptions.IncludeTypeNames)
                let operationName = 
                    match args.[1] :?> string with
                    | "" -> 
                        match queryAst.Definitions with
                        | [] -> failwith "Error parsing query. Can not choose a default operation: query document has no definitions."
                        | _ -> queryAst.Definitions.Head.Name
                    | x -> Some x
                let request =
                    { ServerUrl = serverUrl
                      CustomHeaders = None
                      OperationName = operationName
                      Query = query
                      Variables = None }
                let responseJson = GraphQLClient.sendRequest request
                let schemaTypes = ContextBase.GetSchemaTypes(schema)
                let outputTypes = ContextBase.BuildOutputTypes(schemaTypes, operationName, queryAst, responseJson)
                let generateWrapper name = ProvidedTypeDefinition(name, None, isSealed = true)
                let contextWrapper = generateWrapper "Types"
                outputTypes
                |> Seq.map (|KeyValue|)
                |> Seq.choose (fun (kind, tdef) -> match kind with | EnumType _ -> Some tdef | _ -> None)
                |> Seq.iter (contextWrapper.AddMember)
                tdef.AddMember(contextWrapper)
                let wrappersByPath = Dictionary<string list, ProvidedTypeDefinition>()
                let rootWrapper = generateWrapper "Types"
                wrappersByPath.Add([], rootWrapper)
                let rec getWrapper (path : string list) =
                    if wrappersByPath.ContainsKey path
                    then wrappersByPath.[path]
                    else
                        let wrapper = generateWrapper (path.Head.FirstCharUpper())
                        let upperWrapper =
                            let path = path.Tail
                            if wrappersByPath.ContainsKey(path)
                            then wrappersByPath.[path]
                            else getWrapper path
                        upperWrapper.AddMember(wrapper)
                        wrappersByPath.Add(path, wrapper)
                        wrapper
                let includeType (path : string list) (t : ProvidedTypeDefinition) =
                    let wrapper = getWrapper path
                    wrapper.AddMember(t)
                outputTypes
                |> Seq.map (|KeyValue|)
                |> Seq.choose (fun (kind, t) -> match kind with | OutputType (path, _) -> Some (path, t) | _ -> None)
                |> Seq.iter (fun (path, t) -> includeType path t)
                let queryTypeName =
                    match schema.QueryType.Name with
                    | Some name -> name
                    | None -> failwith "Query type does not have a name in the introspection."
                let odef = OperationBase.MakeProvidedType(request.GetHashCode(), serverUrl, operationName, query, queryTypeName, schemaTypes, outputTypes)
                odef.AddMember(rootWrapper)
                let invoker (args : Expr list) =
                    let operationName = Option.toObj operationName
                    <@@ let this = %%args.[0] : ContextBase
                        let customHttpHeaders = (%%args.[1] : seq<string * string>) |> Option.ofObj
                        OperationBase(this.ServerUrl, customHttpHeaders, Option.ofObj operationName) @@>
                let prm = [ProvidedParameter("customHttpHeaders", typeof<seq<string * string>>, optionalValue = None)]
                let mdef = ProvidedMethod(mname, prm, odef, invoker)
                let members : MemberInfo list = [odef; mdef]
                tdef.AddMembers(members)
                mdef
            smdef.DefineStaticParameters(sprm, genfn)
            smdef
        tdef.AddMember(mdef)
        tdef

    static member internal Constructor = typeof<ContextBase>.GetConstructors().[0]