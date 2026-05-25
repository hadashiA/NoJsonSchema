using NoJsonSchema.Core.Ir;
using NoJsonSchema.Core.Naming;
using NoJsonSchema.Core.Schema;

namespace NoJsonSchema.Core.Resolver;

/// <summary>
/// Lowers a <see cref="JsonSchemaDocument"/> into the <see cref="TypeGraph"/> consumed by the emitter.
/// </summary>
/// <remarks>
/// Currently supports: object / array / primitive / $ref / nullable (via <c>type: [..., "null"]</c>),
/// string enum, allOf inheritance (single $ref base + inline branches) and const properties (value not validated).
/// Pure oneOf/anyOf, non-string enum, and standalone const still produce <see cref="OpaqueTypeDescriptor"/> placeholders.
/// </remarks>
public sealed class TypeGraphBuilder
{
    readonly NameFactory names = new();
    readonly RefResolver refs = new();
    readonly Dictionary<string, TypeDescriptor> descriptors = new(StringComparer.Ordinal);
    /// <summary>
    /// $defs entries that are nothing more than a re-named primitive (e.g. <c>{ "type": "string" }</c>
    /// or a DAP <c>_enum</c>-only definition). Stored by JSON pointer so $ref-resolution can swap them
    /// in directly without minting a C# type.
    /// </summary>
    readonly Dictionary<string, TypeRef> primitiveAliases = new(StringComparer.Ordinal);

    /// <summary>
    /// Build a graph from <paramref name="document"/>. Names reserved via <paramref name="reservedNames"/>
    /// are excluded from the candidate pool (used to keep the namespace-wide <c>{Ns}Serializer</c> name free).
    /// </summary>
    public TypeGraph Build(JsonSchemaDocument document, IEnumerable<string>? reservedNames = null)
    {
        if (reservedNames is not null)
        {
            foreach (var n in reservedNames) names.ReserveTypeName(n);
        }

        // Pass 1: classify each $defs entry. Bare primitive defs (e.g. `{ "type": "string" }` or
        // DAP `_enum`-only entries) are recorded as primitive aliases and skip C# type generation.
        // Everything else gets a unique C# name and is registered with the ref resolver.
        var namedSchemas = new List<(string Name, SchemaNode Schema)>(document.Root.Defs.Count);
        foreach (var (key, schema) in document.Root.Defs)
        {
            if (TryClassifyAsPrimitiveAlias(schema, out var aliasRef))
            {
                primitiveAliases[schema.Pointer] = aliasRef;
                continue;
            }
            var typeName = names.MakeUniqueTypeName(key);
            refs.Register(schema.Pointer, typeName);
            namedSchemas.Add((typeName, schema));
        }

        // Pass 2: materialise descriptors. Inline nested objects discovered during this pass are
        // appended with synthesised names.
        foreach (var (name, schema) in namedSchemas)
        {
            descriptors[name] = BuildNamedDescriptor(name, schema);
        }

        // Resolve the root reference. If the root itself is an inline object, it produces a fresh type.
        var rootRef = BuildTypeRef(document.Root, contextHint: "Root");

        return new TypeGraph
        {
            Types = descriptors,
            Root = rootRef,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Building descriptors for names that are known up-front (i.e. $defs entries).
    // ---------------------------------------------------------------------------------------------

    TypeDescriptor BuildNamedDescriptor(string name, SchemaNode schema)
    {
        if (schema.Ref is not null)
        {
            // Surface ref errors (incl. external $ref) at $defs-time, before any property tries to use it.
            if (!TryResolveAlias(schema.Ref, out _))
            {
                _ = refs.ResolveToName(schema.Ref, schema.Pointer);
            }
            return new OpaqueTypeDescriptor
            {
                Name = name,
                SourcePointer = schema.Pointer,
                Reason = "$ref alias at $defs root (not lowered yet)",
            };
        }

        if (IsStringEnum(schema, out var enumValues))
        {
            return BuildEnumDescriptor(name, schema, enumValues);
        }

        if (schema.AllOf.Count > 0 && TryBuildAllOfDescriptor(name, schema, out var allOfDesc))
        {
            return allOfDesc;
        }

        if (TryDetectUnsupported(schema, out var reason))
        {
            return new OpaqueTypeDescriptor
            {
                Name = name,
                SourcePointer = schema.Pointer,
                Description = schema.Description,
                Deprecated = schema.Deprecated,
                Reason = reason,
            };
        }

        if (IsObjectLike(schema))
        {
            return BuildObjectDescriptor(name, schema);
        }

        // A named, non-object scalar/array root — emit as opaque for now; later passes can fold
        // these into a typedef-like alias.
        return new OpaqueTypeDescriptor
        {
            Name = name,
            SourcePointer = schema.Pointer,
            Description = schema.Description,
            Deprecated = schema.Deprecated,
            Reason = "non-object named root (not supported)",
        };
    }

    ObjectTypeDescriptor BuildObjectDescriptor(string name, SchemaNode schema)
    {
        var siblings = new HashSet<string>(StringComparer.Ordinal);
        var properties = new List<PropertyDescriptor>(schema.Properties.Count);
        var requiredSet = schema.Required.Count == 0
            ? null
            : new HashSet<string>(schema.Required, StringComparer.Ordinal);

        foreach (var (jsonName, propSchema) in schema.Properties)
        {
            var (typeRef, hasNull) = BuildPropertyTypeRef(name, jsonName, propSchema);
            var propName = ResolvePropertyName(name, jsonName, siblings);

            properties.Add(new PropertyDescriptor
            {
                Name = propName,
                JsonName = jsonName,
                Type = typeRef,
                IsRequired = requiredSet?.Contains(jsonName) ?? false,
                IsNullable = hasNull,
                Description = propSchema.Description,
                SourcePointer = propSchema.Pointer,
            });
        }

        var (additional, denied) = ResolveAdditionalProperties(schema, name);

        return new ObjectTypeDescriptor
        {
            Name = name,
            SourcePointer = schema.Pointer,
            Description = schema.Description,
            Deprecated = schema.Deprecated,
            Properties = properties,
            AdditionalProperties = additional,
            AdditionalPropertiesDenied = denied,
        };
    }

    /// <summary>
    /// Try to interpret <paramref name="schema"/>'s <c>allOf</c> as an "inherit + extend" pattern:
    /// at most one <c>$ref</c> branch (the base) plus zero or more inline object branches whose
    /// properties are merged into the resulting descriptor.
    /// </summary>
    bool TryBuildAllOfDescriptor(string name, SchemaNode schema, out TypeDescriptor descriptor)
    {
        descriptor = null!;
        var branches = schema.AllOf;
        if (branches.Count == 0) return false;

        string? baseName = null;
        var inlineBranches = new List<SchemaNode>();

        foreach (var branch in branches)
        {
            if (branch.Ref is not null)
            {
                // Primitive aliases can't serve as a base — fall back to flat composition.
                if (TryResolveAlias(branch.Ref, out _)) return false;
                if (baseName is not null) return false; // multiple bases — fall back to opaque
                baseName = refs.ResolveToName(branch.Ref, branch.Pointer);
                continue;
            }

            if (branch.Kind != SchemaNodeKind.Object) return false;
            if (branch.AllOf.Count > 0 || branch.OneOf.Count > 0 || branch.AnyOf.Count > 0 || branch.Not is not null)
                return false;
            inlineBranches.Add(branch);
        }

        // Aggregate required from every inline branch.
        var requiredAll = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in inlineBranches)
        {
            foreach (var r in b.Required) requiredAll.Add(r);
        }

        var properties = new List<PropertyDescriptor>();
        var seenJson = new HashSet<string>(StringComparer.Ordinal);
        var siblings = new HashSet<string>(StringComparer.Ordinal);

        // Map of base-chain properties by their JSON name. Lets the derived inline branches:
        //   - inherit as-is when the inline type matches the base type;
        //   - emit a narrower override (with `new` modifier) when the type differs.
        var inherited = new Dictionary<string, PropertyDescriptor>(StringComparer.Ordinal);
        if (baseName is not null
            && descriptors.TryGetValue(baseName, out var baseDescriptor)
            && baseDescriptor is ObjectTypeDescriptor baseObj)
        {
            foreach (var bp in WalkInheritedProperties(baseObj))
            {
                inherited[bp.JsonName] = bp;
                siblings.Add(bp.Name);
            }
        }

        TypeRef? additional = null;
        var denied = false;

        foreach (var inline in inlineBranches)
        {
            foreach (var (jsonName, propSchema) in inline.Properties)
            {
                if (!seenJson.Add(jsonName)) continue; // first inline branch wins

                var (typeRef, hasNull) = BuildPropertyTypeRef(name, jsonName, propSchema);

                if (inherited.TryGetValue(jsonName, out var inheritedProp))
                {
                    // Same shape → derived simply inherits it; nothing to add on this type.
                    if (Equals(inheritedProp.Type, typeRef) && inheritedProp.IsNullable == hasNull)
                        continue;

                    // Narrower override → re-declare with the inherited C# name and `new` modifier.
                    properties.Add(new PropertyDescriptor
                    {
                        Name = inheritedProp.Name,
                        JsonName = jsonName,
                        Type = typeRef,
                        IsRequired = requiredAll.Contains(jsonName),
                        IsNullable = hasNull,
                        Description = propSchema.Description,
                        SourcePointer = propSchema.Pointer,
                        HidesBaseProperty = true,
                    });
                    continue;
                }

                var propName = ResolvePropertyName(name, jsonName, siblings);
                properties.Add(new PropertyDescriptor
                {
                    Name = propName,
                    JsonName = jsonName,
                    Type = typeRef,
                    IsRequired = requiredAll.Contains(jsonName),
                    IsNullable = hasNull,
                    Description = propSchema.Description,
                    SourcePointer = propSchema.Pointer,
                });
            }

            if (inline.AdditionalProperties is not null)
            {
                var (a, d) = ResolveAdditionalProperties(inline, name);
                additional ??= a;
                denied = denied || d;
            }
        }

        descriptor = new ObjectTypeDescriptor
        {
            Name = name,
            SourcePointer = schema.Pointer,
            Description = schema.Description,
            Deprecated = schema.Deprecated,
            BaseTypeName = baseName,
            Properties = properties,
            AdditionalProperties = additional,
            AdditionalPropertiesDenied = denied,
        };
        return true;
    }

    EnumTypeDescriptor BuildEnumDescriptor(string name, SchemaNode schema, List<string> values)
    {
        var siblings = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<EnumMember>(values.Count);
        foreach (var v in values)
        {
            var memberName = NameFactory.MakeUniquePropertyName(v, siblings);
            members.Add(new EnumMember { Name = memberName, JsonValue = v });
        }
        return new EnumTypeDescriptor
        {
            Name = name,
            SourcePointer = schema.Pointer,
            Description = schema.Description,
            Deprecated = schema.Deprecated,
            Underlying = PrimitiveKind.String,
            Members = members,
        };
    }

    (TypeRef? Additional, bool Denied) ResolveAdditionalProperties(SchemaNode schema, string contextHint)
    {
        if (schema.AdditionalProperties is null) return (null, false);
        if (schema.AdditionalProperties.Kind == SchemaNodeKind.AlwaysTrue) return (TypeRef.Any.Instance, false);
        if (schema.AdditionalProperties.Kind == SchemaNodeKind.AlwaysFalse) return (null, true);
        var (inner, _) = BuildPropertyTypeRef(contextHint, "AdditionalProperty", schema.AdditionalProperties);
        return (inner, false);
    }

    (TypeRef Ref, bool HasNull) BuildPropertyTypeRef(string parentName, string jsonName, SchemaNode schema)
    {
        var contextHint = parentName + NameFactory.ToTypeIdentifier(jsonName);
        var typeRef = BuildTypeRef(schema, contextHint, out var hasNull);
        return (typeRef, hasNull);
    }

    // ---------------------------------------------------------------------------------------------
    // Recursive walker turning a SchemaNode into a TypeRef. Inline objects are materialised as
    // brand-new descriptors with synthesised names and recorded in `descriptors`.
    // ---------------------------------------------------------------------------------------------

    TypeRef BuildTypeRef(SchemaNode schema, string contextHint)
        => BuildTypeRef(schema, contextHint, out _);

    TypeRef BuildTypeRef(SchemaNode schema, string contextHint, out bool hasNull)
    {
        hasNull = false;

        if (schema.Kind == SchemaNodeKind.AlwaysTrue)  return TypeRef.Any.Instance;
        if (schema.Kind == SchemaNodeKind.AlwaysFalse) return new TypeRef.Unsupported("schema=false");

        if (schema.Ref is not null)
        {
            if (TryResolveAlias(schema.Ref, out var aliasRef)) return aliasRef;
            var name = refs.ResolveToName(schema.Ref, schema.Pointer);
            return new TypeRef.Named(name);
        }

        if (IsStringEnum(schema, out var enumValues))
        {
            var enumName = names.MakeUniqueTypeName(contextHint);
            refs.Register(schema.Pointer, enumName);
            descriptors[enumName] = BuildEnumDescriptor(enumName, schema, enumValues);
            return new TypeRef.Named(enumName);
        }

        if (TryDetectUnsupported(schema, out var reason)) return new TypeRef.Unsupported(reason);

        // Strip "null" out of the type set — it becomes a Nullable wrapper.
        var types = schema.Types;
        if (types.Count > 0 && types.Contains(JsonSchemaType.Null))
        {
            hasNull = true;
            var withoutNull = new List<JsonSchemaType>(types.Count - 1);
            foreach (var t in types)
            {
                if (t != JsonSchemaType.Null) withoutNull.Add(t);
            }
            types = withoutNull;
        }

        // Determine the dominant type. Schemas without an explicit type but with `properties` are
        // implicitly objects; with `items` they're implicitly arrays.
        var dominant = ResolveDominantType(schema, types);

        TypeRef result = dominant switch
        {
            JsonSchemaType.String  => MapStringPrimitive(schema),
            JsonSchemaType.Integer => MapIntegerPrimitive(schema),
            JsonSchemaType.Number  => MapNumberPrimitive(schema),
            JsonSchemaType.Boolean => TypeRef.PrimitiveBoolean,
            JsonSchemaType.Array   => BuildArrayRef(schema, contextHint),
            JsonSchemaType.Object  => BuildObjectRef(schema, contextHint),
            _ => TypeRef.Any.Instance,
        };

        return hasNull ? TypeRef.MakeNullable(result) : result;
    }

    TypeRef BuildArrayRef(SchemaNode schema, string contextHint)
    {
        var elementHint = contextHint + "Item";
        var element = schema.Items is null
            ? TypeRef.Any.Instance
            : BuildTypeRef(schema.Items, elementHint);
        return new TypeRef.Array(element);
    }

    TypeRef BuildObjectRef(SchemaNode schema, string contextHint)
    {
        // Empty object → dictionary-style payload if additionalProperties is set, else "any".
        if (schema.Properties.Count == 0 && schema.AdditionalProperties is not null)
        {
            var (addl, _) = ResolveAdditionalProperties(schema, contextHint);
            return addl is null ? TypeRef.Any.Instance : new TypeRef.Dictionary(addl);
        }

        if (schema.Properties.Count == 0) return TypeRef.Any.Instance;

        // Inline object → mint a new named descriptor and refer to it.
        var inlineName = names.MakeUniqueTypeName(contextHint);
        refs.Register(schema.Pointer, inlineName);
        // Reserve the slot first so recursive references to the same pointer don't loop infinitely.
        descriptors[inlineName] = new OpaqueTypeDescriptor
        {
            Name = inlineName,
            SourcePointer = schema.Pointer,
            Reason = "inline-object placeholder",
        };
        descriptors[inlineName] = BuildObjectDescriptor(inlineName, schema);
        return new TypeRef.Named(inlineName);
    }

    // ---------------------------------------------------------------------------------------------
    // Static helpers.
    // ---------------------------------------------------------------------------------------------

    static JsonSchemaType ResolveDominantType(SchemaNode schema, IReadOnlyList<JsonSchemaType> types)
    {
        if (types.Count == 1) return types[0];
        if (types.Count > 1) return JsonSchemaType.Object; // multi-type unions land as objects/any below

        if (schema.Properties.Count > 0 || schema.AdditionalProperties is not null) return JsonSchemaType.Object;
        if (schema.Items is not null) return JsonSchemaType.Array;
        return default; // sentinel → Any
    }

    static TypeRef MapStringPrimitive(SchemaNode schema) => schema.Format switch
    {
        "date-time" => TypeRef.PrimitiveDateTimeOffset,
        "uuid"      => TypeRef.PrimitiveGuid,
        _           => TypeRef.PrimitiveString,
    };

    static TypeRef MapIntegerPrimitive(SchemaNode schema) => schema.Format switch
    {
        "int32" or "int16" or "byte" => TypeRef.PrimitiveInt32,
        _ => TypeRef.PrimitiveInt64,
    };

    static TypeRef MapNumberPrimitive(SchemaNode schema) => schema.Format switch
    {
        "float" or "single" => TypeRef.PrimitiveSingle,
        _ => TypeRef.PrimitiveDouble,
    };

    static bool IsObjectLike(SchemaNode schema)
    {
        if (schema.Properties.Count > 0) return true;
        if (schema.AdditionalProperties is not null) return true;
        foreach (var t in schema.Types)
        {
            if (t == JsonSchemaType.Object) return true;
        }
        return false;
    }

    static bool TryDetectUnsupported(SchemaNode schema, out string reason)
    {
        if (schema.AllOf.Count > 0) { reason = "allOf"; return true; }
        if (schema.OneOf.Count > 0) { reason = "oneOf"; return true; }
        if (schema.AnyOf.Count > 0) { reason = "anyOf"; return true; }
        if (schema.Not is not null) { reason = "not";   return true; }
        // 'const' is fine as long as we can still infer a type — the value check is deferred to a later pass.
        if (schema.Const is not null && schema.Types.Count == 0)
        {
            reason = "const without a type hint";
            return true;
        }
        if (schema.Enum is not null && !IsAllStringEnum(schema.Enum))
        {
            reason = "non-string enum";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    static bool IsAllStringEnum(IReadOnlyList<JsonValue> values)
    {
        foreach (var v in values)
        {
            if (v is not JsonValue.String) return false;
        }
        return true;
    }

    /// <summary>
    /// Look up the primitive alias map by the literal <c>$ref</c> string (which equals the
    /// referenced pointer). Returns true when the reference points at a renamed primitive.
    /// </summary>
    bool TryResolveAlias(string refString, out TypeRef aliasRef) =>
        primitiveAliases.TryGetValue(refString, out aliasRef!);

    /// <summary>
    /// Allocate a C# property name for <paramref name="jsonName"/> inside type
    /// <paramref name="typeName"/>. Properties that would shadow the enclosing type get a
    /// <c>Value</c> suffix (e.g. <c>Checksum.checksum</c> -> <c>Checksum.ChecksumValue</c>); any
    /// remaining collisions inside <paramref name="siblings"/> get a numeric suffix.
    /// </summary>
    static string ResolvePropertyName(string typeName, string jsonName, ISet<string> siblings)
    {
        var candidate = NameFactory.ToPropertyIdentifier(jsonName);
        if (string.Equals(candidate, typeName, StringComparison.Ordinal))
        {
            candidate += "Value";
        }
        return NameFactory.MakeUniquePropertyName(candidate, siblings);
    }

    /// <summary>
    /// Walk <paramref name="obj"/>'s inheritance chain (base first), yielding every property in order.
    /// </summary>
    IEnumerable<PropertyDescriptor> WalkInheritedProperties(ObjectTypeDescriptor obj)
    {
        // Collect base-first by recursing into the base, then yielding own.
        if (obj.BaseTypeName is not null
            && descriptors.TryGetValue(obj.BaseTypeName, out var b)
            && b is ObjectTypeDescriptor baseObj)
        {
            foreach (var bp in WalkInheritedProperties(baseObj)) yield return bp;
        }
        foreach (var p in obj.Properties) yield return p;
    }

    /// <summary>
    /// True when <paramref name="schema"/> is just a primitive (string / integer / boolean) wrapped
    /// in a $defs entry — no properties, no allOf/oneOf, no composition. <c>_enum</c>-only DAP
    /// definitions land here too because we don't currently materialise open enums.
    /// </summary>
    static bool TryClassifyAsPrimitiveAlias(SchemaNode schema, out TypeRef aliasRef)
    {
        aliasRef = null!;
        if (schema.Ref is not null) return false;
        if (schema.Properties.Count > 0) return false;
        if (schema.AdditionalProperties is not null) return false;
        if (schema.AllOf.Count > 0 || schema.OneOf.Count > 0 || schema.AnyOf.Count > 0 || schema.Not is not null) return false;
        if (schema.Items is not null) return false;
        // Closed string enums are real types — only escape here for plain primitives / _enum-only defs.
        if (schema.Enum is not null && schema.Enum.Count > 0) return false;

        if (schema.Types.Count != 1) return false;
        aliasRef = schema.Types[0] switch
        {
            JsonSchemaType.String  => MapStringPrimitive(schema),
            JsonSchemaType.Integer => MapIntegerPrimitive(schema),
            JsonSchemaType.Number  => MapNumberPrimitive(schema),
            JsonSchemaType.Boolean => TypeRef.PrimitiveBoolean,
            _ => null!,
        };
        return aliasRef is not null;
    }

    /// <summary>
    /// True only for a closed string enum with two or more members. Single-member "enums" are
    /// effectively a <c>const</c> constraint (DAP uses them on discriminator fields) — we treat
    /// those as plain strings so subclass shadowing of inherited properties stays type-compatible.
    /// </summary>
    static bool IsStringEnum(SchemaNode schema, out List<string> values)
    {
        values = [];
        if (schema.Enum is null || schema.Enum.Count < 2) return false;
        var collected = new List<string>(schema.Enum.Count);
        foreach (var v in schema.Enum)
        {
            if (v is JsonValue.String s) collected.Add(s.Value);
            else return false;
        }
        values = collected;
        return true;
    }
}
