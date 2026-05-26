# NoJsonSchema

**Generate C# types and a zero-dependency UTF-8 JSON parser/emitter from JSON Schema.**

Point NoJsonSchema at a JSON Schema (Draft 2020-12 / Draft-07) or OpenAPI 3.x document and it emits:

1. **POCO types** — `class` / `record` / `readonly record struct`, your choice per type.
2. **A per-type Formatter** — UTF-8 parser and emitter built on `ref byte` + `Unsafe.Add`.
3. **A namespace-wide Serializer** — generic `Deserialize<T>` / `Serialize<T>` with `Cache<T>` dispatch (one resolve per CLR generic instantiation, then a single static-field load).

```
JSON Schema  ─►  NoJsonSchema  ─►  C# types + UTF-8 JSON IO  ─►  your app
                  (build-time)        (BCL-only, AOT-safe)
```

## Zero runtime dependencies

The generated `.cs` files reference **nothing except the BCL** — no `System.Text.Json`, no `Newtonsoft.Json`, no reflection, no third-party packages of any kind. Just `System`, `System.Buffers`, `System.IO`, `System.Runtime.CompilerServices`.

That means:

- **Native AOT** works out of the box. Nothing to root in `rd.xml`, no warnings.
- **Unity / IL2CPP** can swallow it whole. No `System.Text.Json`, no `Newtonsoft Json` , no `JsonUtility`.
- **Trimming (`PublishTrimmed=true`)** is safe. The generator doesn't emit anything that survives trim analysis as un-rooted reflection.

The Core library itself (`NoJsonSchema.dll`) does depend on `System.Text.Json` — but only for reading the schema document at *generator time*. None of that touches your shipped binary.

## Why

| | NoJsonSchema | System.Text.Json source-gen |
|---|---:|---:|
| Generated code runtime deps | **none** | `System.Text.Json` |
| Deserialize (8-property DTO) | **416 ns** (0.74×) | 565 ns |
| Serialize (same DTO) | **211 ns** (0.78×) | 271 ns |
| Allocations on deserialize | **856 B** (0.64×) | 1328 B |
| Unity / IL2CPP / AOT | ✅ | ✅ (limited) |

(Benchmark: Apple M4 / .NET 10, ShortRun. See [`samples/Bench/`](samples/Bench).)

The main use cases are:
- When a JSON schema is already published as a general-purpose specification, but a corresponding C# SDK does not exist (or you want to build your own).
- When a protocol is defined using a schema-first approach, such as https://github.com/microsoft/typespec or JSON-RPC, and you need to align your code with it.

For general applications that are contained entirely within C#, a code-first approach (generating a schema from C# type declarations) is likely more convenient. However, NoJsonSchema might be useful in the use cases mentioned above.

## Installation

Pick the workflow that matches your project:

### Source generato

```sh
dotnet add package NoJsonSchema
dotnet add package NoJsonSchema.SourceGenerator
```

Drop your schemas into the csproj as `AdditionalFiles`:

```xml
<ItemGroup>
  <AdditionalFiles Include="schemas/my-schema.json">
    <NoJsonSchemaNamespace>MyApp.Models</NoJsonSchemaNamespace>
  </AdditionalFiles>
</ItemGroup>
```

…and the types appear under that namespace on the next build.

### Standalone CLI (good for vendored / bulk generation)

```sh
dotnet tool install -g NoJsonSchema.Cli

nojsonschema generate -i schema.json -o ./Generated -n MyApp.Models
```


The CLI accepts file paths **or** http(s) URLs — point it at a remote schema directly:

```sh
nojsonschema generate \
  -i https://raw.githubusercontent.com/microsoft/debug-adapter-protocol/main/debugAdapterProtocol.json \
  -o ./Dap -n Dap
```

```
Usage: generate [options...] [-h|--help] [--version]

Generate C# code from a JSON Schema.

Options:
  -i, --input <string>                Path or http(s) URL of the JSON Schema. [Required]
  -o, --output <string>               Directory to write generated .cs files into. [Required]
  -n, --namespace <string>            Namespace for generated types. [Default: @"Generated"]
  --root-type <string?>               Optional root type name override. [Default: null]
  --type-style <TypeStyle>            Type style for objects (Class | Record | ReadonlyRecordStruct). [Default: Class]
  --allof-strategy <AllOfStrategy>    How to represent allOf composition (Inherit | Flatten). [Default: Inherit]
  --strict-extra                      Treat unknown JSON properties as errors.
  --value-object <string[]?>          Comma-separated list of $defs entries to emit as readonly record struct (primary-ctor) value objects. [Default: null]
  --use-required                      Emit the C# 11 'required' modifier on non-nullable required properties (otherwise '= null!' is used to suppress CS8618).
  --include-type <string[]?>          Comma-separated whitelist of $defs / components.schemas entries to generate (transitive deps included automatically). Default is everything. [Default: null]
```


### Library (custom tooling, MSBuild target, Roslyn integration)

```sh
dotnet add package NoJsonSchema
```

```csharp
var pipeline = new NoJsonSchema.Core.GeneratorPipeline();
var result = pipeline.Generate(File.ReadAllText("schema.json"), new GenerationOptions
{
    Namespace = "MyApp.Models",
});
foreach (var f in result.Files)
    File.WriteAllText(Path.Combine("./Generated", f.FileName), f.SourceText);
```

## Quick start

Given this schema (`user.json`):

```json
{
  "$defs": {
    "Address": {
      "type": "object",
      "properties": {
        "city": { "type": "string" },
        "zip":  { "type": "string" }
      },
      "required": ["city", "zip"]
    },
    "User": {
      "type": "object",
      "properties": {
        "id":      { "type": "integer", "format": "int32" },
        "name":    { "type": "string" },
        "email":   { "type": "string" },
        "joined":  { "type": "string", "format": "date" },
        "address": { "$ref": "#/$defs/Address" }
      },
      "required": ["id", "name"]
    }
  }
}
```

NoJsonSchema emits `User.g.cs`, `Address.g.cs`, `Formatters/UserFormatter.g.cs`, `Formatters/AddressFormatter.g.cs`, and a namespace-wide `MyAppModelsSerializer.g.cs`. You use it like this:

```csharp
using MyApp.Models;

// The namespace-wide Serializer is the entry point.
var user = MyAppModelsSerializer.Deserialize<User>(utf8Bytes);
var bytes = MyAppModelsSerializer.SerializeToUtf8Bytes(user);

// IBufferWriter<byte> overload too (zero extra copy).
MyAppModelsSerializer.Serialize(myBufferWriter, user);
```

The CLI / source generator stamps a static `{Namespace}Serializer` class into the same namespace as the POCOs. Use that — the per-type `XxxFormatter` is internal-by-design (no public surface to lock in).

## Configuration

All knobs are surfaced through three equivalent channels — MSBuild metadata on the `AdditionalFiles` entry (source generator), CLI flags (`nojsonschema`), and `GenerationOptions` properties (library).

| MSBuild metadata | CLI flag | `GenerationOptions` | Default | What it does |
|---|---|---|---|---|
| `NoJsonSchemaNamespace` | `-n, --namespace` | `Namespace` | `Generated` | Target C# namespace. |
| — | `--root-type` | `RootTypeName` | (none) | Optional root type name override (only useful when the schema's root is itself a single object schema). |
| `NoJsonSchemaTypeStyle` | `--type-style` | `TypeStyle` | `Class` | `Class` / `Record` / `ReadonlyRecordStruct`. |
| — | `--allof-strategy` | `AllOfStrategy` | `Inherit` | How to represent `allOf` composition — `Inherit` (emit base class + derived) or `Flatten` (inline all properties into one type). |
| `NoJsonSchemaStrictExtraProperties` | `--strict-extra` | `StrictExtraProperties` | `false` | Throw `NoJsonFormatException` on unknown JSON properties. |
| `NoJsonSchemaValueObjects` | `--value-object` | `ValueObjectTypes` | (empty) | `;`/`,`-separated `$defs` entries to emit as `readonly partial record struct` (primary-ctor form). |
| `NoJsonSchemaUseRequired` | `--use-required` | `UseRequiredModifier` | `false` | Use C# 11 `required` modifier on non-nullable required properties (otherwise `= null!`). |
| `NoJsonSchemaIncludeTypes` | `--include-type` | `IncludedTypes` | (everything) | `;`/`,`-separated whitelist of `$defs` / `components.schemas` entries to generate (transitive deps included automatically). |

### Per-type metadata example

```xml
<AdditionalFiles Include="schemas/dap.json">
  <NoJsonSchemaNamespace>Dap</NoJsonSchemaNamespace>
  <NoJsonSchemaValueObjects>Checksum;Source</NoJsonSchemaValueObjects>
  <NoJsonSchemaIncludeTypes>InitializeRequest;StoppedEvent;StackTraceResponse</NoJsonSchemaIncludeTypes>
</AdditionalFiles>
```

## CLI reference

The `nojsonschema` tool has two subcommands. Both accept either a local file path **or** an `http(s)` URL for `--input`.

### `nojsonschema generate`

Generate C# types + UTF-8 parser/emitter from a schema.

```sh
nojsonschema generate -i <schema> -o <out-dir> [options...]
```

| Flag | Argument | Default | Description |
|---|---|---|---|
| `-i`, `--input` | `<path-or-url>` | **required** | Local path or `http(s)` URL of the JSON Schema / OpenAPI document. |
| `-o`, `--output` | `<dir>` | **required** | Directory to write generated `.cs` files into (subdirs are created as needed). |
| `-n`, `--namespace` | `<string>` | `Generated` | Target C# namespace for the emitted types and Serializer. |
| `--root-type` | `<string>` | (none) | Override the root type's name. Most schemas drive type names from `$defs` entries — this is for the rare case where you want to rename the top-level object. |
| `--type-style` | `Class \| Record \| ReadonlyRecordStruct` | `Class` | How to emit object types. `Class` uses `{ get; set; }`. `Record` is a C# `record` with `{ get; init; }`. `ReadonlyRecordStruct` is a `readonly partial record struct` with a primary constructor (value-object form). |
| `--allof-strategy` | `Inherit \| Flatten` | `Inherit` | How to represent `allOf` composition. `Inherit` emits a base class + derived class. `Flatten` inlines all parent properties into a single class. |
| `--strict-extra` | (flag) | `false` | Throw `NoJsonFormatException` when the JSON payload contains a property the schema didn't declare. Otherwise unknown properties are skipped. |
| `--value-object` | `<csv>` | (empty) | Comma- or semicolon-separated list of `$defs` entries to emit as `readonly partial record struct` (primary-ctor form). E.g. `--value-object Color,SemVer`. Per-type override of `--type-style`. |
| `--use-required` | (flag) | `false` | Emit the C# 11 `required` modifier on non-nullable required properties. Without this, `= null!` suppresses CS8618 instead. Shipping a `_SetsRequiredMembersShim.g.cs` polyfill for ns2.0/pre-net7 consumers. |
| `--include-type` | `<csv>` | (everything) | Whitelist of top-level `$defs` / `components.schemas` entries to generate. Transitive dependencies are included automatically — e.g. `--include-type Pet` will also pull `Cat`, `Dog` if they're `oneOf` branches. Useful for trimming massive schemas (DAP has 192 defs; you might only need a handful). |
| `-h`, `--help` | (flag) | | Print usage and exit. |

#### Examples

```sh
# Vanilla: local schema → ./Generated under "MyApp.Models"
nojsonschema generate -i schema.json -o Generated -n MyApp.Models

# Remote schema with --include-type whitelist (trim 192-def DAP to 13 files)
nojsonschema generate \
  -i https://raw.githubusercontent.com/microsoft/debug-adapter-protocol/main/debugAdapterProtocol.json \
  -o ./Dap -n Dap \
  --include-type Capabilities,InitializeRequest,StoppedEvent

# Mix: most types as classes, but Checksum/Source as readonly record structs
nojsonschema generate -i dap.json -o ./Dap -n Dap \
  --value-object Checksum,Source \
  --use-required --strict-extra
```

### `nojsonschema lint`

Parse a schema and print a summary; reports structural problems without generating code.

```sh
nojsonschema lint -i <schema>
```

| Flag | Argument | Default | Description |
|---|---|---|---|
| `-i`, `--input` | `<path-or-url>` | **required** | Local path or `http(s)` URL of the JSON Schema / OpenAPI document. |
| `-h`, `--help` | (flag) | | Print usage and exit. |

Output:

```
$ nojsonschema lint -i https://json.schemastore.org/package.json
file:        https://json.schemastore.org/package.json
$schema:     http://json-schema.org/draft-07/schema#
$id:         https://json.schemastore.org/package.json
root kind:   Object
definitions: 29
properties:  61 (on root)
```

Exit codes: `0` on success, `1` on any failure (network / IO / parse / schema validation).

## Supported schema subset

| Construct | Status |
|---|---|
| `type: object` with `properties`, `required`, `additionalProperties` (bool / schema) | ✅ |
| `type: string` / `integer` / `number` / `boolean` / `null` | ✅ |
| `type: array` with `items` | ✅ |
| Nullable via JSON Schema `type: ["X", "null"]` *and* OpenAPI `nullable: true` | ✅ |
| `enum` (string), `const` | ✅ |
| `$ref` (local) | ✅ |
| `$defs` / `definitions` / OpenAPI `components.schemas` | ✅ |
| `allOf` (inheritance pattern: `$ref` base + inline derived) | ✅ |
| `oneOf` + `discriminator` (OpenAPI / Swagger polymorphism) | ✅ |
| `format`: `date-time` / `date` / `time` / `duration` / `uuid` / `uri` / `uri-reference` / `byte` / `binary` | ✅ |
| Integer subtypes: `int8` / `uint8` / `byte` / `int16` / `uint16` / `int32` / `uint32` / `int64` / `uint64` | ✅ |
| `pattern`, `minLength` / `maxLength`, numeric ranges | ⏳ runtime validation TBD |
| `if` / `then` / `else`, `not`, `anyOf` | ⏳ |
| External `$ref` (cross-document) | ⏳ |

`format` strings the IR doesn't recognise fall back to the base type (e.g. unknown `string` format → `string`).

## Generated-code shape

The generator emits one file per type, one Formatter per type, and one namespace-wide Serializer:

```
Generated/
  User.g.cs                          ← POCO
  Address.g.cs
  Formatters/
    UserFormatter.g.cs               ← per-type UTF-8 parser/emitter (internal static class)
    AddressFormatter.g.cs
  MyAppModelsSerializer.g.cs         ← public entry point: Cache<T> + Deserialize<T> / Serialize<T>
  _IsExternalInitShim.g.cs           ← `init` setter polyfill for netstandard
  _SetsRequiredMembersShim.g.cs      ← only when --use-required and TFM < net7
```

Hot-path details (commentary lives in [`Emit/SerializerTemplate.cs`](src/NoJsonSchema.Core/Emit/SerializerTemplate.cs)):

- **Tokenizer / Writer** are `ref struct`s with a `ref byte head` field on net7+ (Span<byte> + slicing on netstandard 2.1). `Unsafe.Add` / `Unsafe.CopyBlockUnaligned` instead of span-indexed access — per-byte bounds checks elided on the hot path.
- **WriteString fast path**: bulk `Encoding.UTF8.GetBytes(chars, span)` for ASCII-safe runs, escape only on demand.
- **Property dispatch** is bucketed by UTF-8 byte length — `switch (__name.Length)` then `SequenceEqual` within bucket. Mismatches short-circuit fast.
- **Generic dispatch** routes through a per-`T` static `Cache<T>` with two delegate fields. The `typeof(T) ==` resolution runs once per CLR generic instantiation; subsequent calls are a single static-field load + one delegate invocation. The Cache + delegate types are private-nested in the Serializer, so multiple generated namespaces in the same assembly don't collide.
- **`Throw*` helpers** are `[DoesNotReturn] [MethodImpl(NoInlining)]` so callers stay inlineable.

## Compatibility

| Target | Status |
|---|---|
| .NET 8 / 9 / 10 (Native AOT, trimmed) | ✅ |
| .NET Standard 2.0 / 2.1 (Core library) | ✅ |
| Unity 2022.3+ / 2023.x (IL2CPP, source generator loadable by bundled Roslyn 4.3) | ✅ |
| Generated code | targets C# 11 (`ref` fields), so net7+ runtime |

## License

MIT
