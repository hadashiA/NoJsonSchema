# NoJsonSchema

[![NuGet](https://img.shields.io/nuget/v/NoJsonSchema.svg?label=NoJsonSchema)](https://www.nuget.org/packages/NoJsonSchema/)
[![NuGet (Generator)](https://img.shields.io/nuget/v/NoJsonSchema.SourceGenerator.svg?label=SourceGenerator)](https://www.nuget.org/packages/NoJsonSchema.SourceGenerator/)
[![NuGet (CLI)](https://img.shields.io/nuget/v/NoJsonSchema.Cli.svg?label=Cli)](https://www.nuget.org/packages/NoJsonSchema.Cli/)
[![CI](https://github.com/hadashiA/NoJsonSchema/actions/workflows/ci.yml/badge.svg)](https://github.com/hadashiA/NoJsonSchema/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

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

After build, `dotnet list package` for your project looks the same as it did before — NoJsonSchema is gone:

```sh
$ dotnet add package NoJsonSchema.SourceGenerator
$ dotnet build
$ dotnet list package
# (no NoJsonSchema entries — the generator is build-time only, no runtime carry-over)
```

That means:

- **Native AOT / `PublishAot=true`** works out of the box. Nothing to root in `rd.xml`, no warnings.
- **Unity / IL2CPP** can swallow it whole. No `JsonSerializerOptions` configuration, no `[JsonSerializable]` attributes to track, no `TypeInfoResolver` chains.
- **Trimming (`PublishTrimmed=true`)** is safe. The generator doesn't emit anything that survives trim analysis as un-rooted reflection.
- **No version skew.** The CLI/SourceGenerator package can bump independently; your app's runtime surface is unaffected because it doesn't link to NoJsonSchema at all.
- **No transitive STJ pull.** Apps targeting netstandard2.0 that use NoJsonSchema-generated code don't transitively pull `System.Text.Json` 8.x and the chain of `System.Text.Encodings.Web`, `System.Memory`, etc. that comes with it.

The Core library itself (`NoJsonSchema.dll`) does depend on `System.Text.Json` — but only for reading the schema document at *generator time*. None of that touches your shipped binary.

## Why

| | NoJsonSchema | System.Text.Json source-gen |
|---|---:|---:|
| Schema-first workflow (DAP, OpenAPI, custom JSON Schema) | ✅ | ✋ hand-written DTOs |
| Generated code runtime deps | **none** | `System.Text.Json` |
| Deserialize (8-property DTO) | **416 ns** (0.74×) | 565 ns |
| Serialize (same DTO) | **211 ns** (0.78×) | 271 ns |
| Allocations on deserialize | **856 B** (0.64×) | 1328 B |
| Unity / IL2CPP / AOT | ✅ | ✅ (limited) |

(Benchmark: Apple M4 / .NET 10, ShortRun. See [`samples/Bench/`](samples/Bench).)

## Install

Pick the workflow that matches your project:

### Source generator (recommended for app code)

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

// Round-trip via the per-type Formatter.
var user = UserFormatter.Deserialize(utf8Bytes);
var bytes = UserFormatter.SerializeToUtf8Bytes(user);

// Or via the namespace-wide Serializer<T>.
var u2 = MyAppModelsSerializer.Deserialize<User>(utf8Bytes);
MyAppModelsSerializer.Serialize(stream, u2);

// Stream / async also work.
var u3 = await UserFormatter.DeserializeAsync(networkStream, cancellationToken: ct);
```

## Configuration

All options can be set per-schema in MSBuild metadata, via CLI flags, or via `GenerationOptions` from the library.

| MSBuild metadata | CLI flag | What it does |
|---|---|---|
| `NoJsonSchemaNamespace` | `-n, --namespace` | Target C# namespace. Defaults to `Generated`. |
| `NoJsonSchemaTypeStyle` | `--type-style` | `Class` (default) / `Record` / `ReadonlyRecordStruct`. |
| `NoJsonSchemaValueObjects` | `--value-object` | `;`-separated $defs entries to emit as `readonly partial record struct` (primary-ctor form). |
| `NoJsonSchemaStrictExtraProperties` | `--strict-extra` | Throw on unknown JSON properties. |
| `NoJsonSchemaUseRequired` | `--use-required` | Use C# 11 `required` modifier (default: `= null!`). |
| `NoJsonSchemaIncludeTypes` | `--include-type` | `;`-separated whitelist of $defs entries (transitive deps included automatically). |

### Per-type metadata example

```xml
<AdditionalFiles Include="schemas/dap.json">
  <NoJsonSchemaNamespace>Dap</NoJsonSchemaNamespace>
  <NoJsonSchemaValueObjects>Checksum;Source</NoJsonSchemaValueObjects>
  <NoJsonSchemaIncludeTypes>InitializeRequest;StoppedEvent;StackTraceResponse</NoJsonSchemaIncludeTypes>
</AdditionalFiles>
```

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

## End-to-end: Debug Adapter Protocol

[`samples/Dap/`](samples/Dap) generates the **complete** [Debug Adapter Protocol schema](https://github.com/microsoft/debug-adapter-protocol/blob/main/debugAdapterProtocol.json) — 192 definitions, 226 `$ref`s, 112 `allOf`s, 83 enums.

```sh
nojsonschema generate \
  -i samples/Dap/debugAdapterProtocol.json \
  -o samples/Dap/Generated -n Dap
# → 495 .cs files, compiles with 0 errors
```

`tests/NoJsonSchema.DapIntegration.Tests/` round-trips real DAP messages: `InitializeRequest`, `StoppedEvent`, `StackTraceResponse`, the `Checksum` value-object pair, polymorphic dispatch via `oneOf+discriminator`.

## Generated-code shape

The generator emits one file per type, one Formatter per type, and one namespace-wide Serializer:

```
Generated/
  User.g.cs                          ← POCO
  Address.g.cs
  Formatters/
    UserFormatter.g.cs               ← per-type UTF-8 parser/emitter + INoJsonFormatter<T> adapter
    AddressFormatter.g.cs
  MyAppModelsSerializer.g.cs         ← shared options/exception/tokenizer + Cache<T> dispatch
  _SetsRequiredMembersShim.g.cs      ← only when --use-required and TFM < net7
```

Hot-path details (commentary lives in [`Emit/SerializerTemplate.cs`](src/NoJsonSchema.Core/Emit/SerializerTemplate.cs)):

- **Tokenizer / Writer** are `ref struct`s with a `ref byte head` field. `Unsafe.Add` / `Unsafe.CopyBlockUnaligned` instead of span-indexed access — per-byte bounds checks elided on the hot path.
- **WriteString fast path**: bulk `Encoding.UTF8.GetBytes(chars, span)` for ASCII-safe runs, escape only on demand.
- **Property dispatch** is bucketed by UTF-8 byte length — `switch (__name.Length)` then `SequenceEqual` within bucket. Mismatches short-circuit fast.
- **Generic dispatch** routes through a per-`T` static `Cache<T>.Formatter` field — `Dictionary<Type, object>` lookup + `Unsafe.As<INoJsonFormatter<T>>` once per CLR generic instantiation. Subsequent calls are a single static-field load + one interface call.
- **`Throw*` helpers** are `[DoesNotReturn] [MethodImpl(NoInlining)]` so callers stay inlineable.

## Compatibility

| Target | Status |
|---|---|
| .NET 8 / 9 / 10 (Native AOT, trimmed) | ✅ |
| .NET Standard 2.0 / 2.1 (Core library) | ✅ |
| Unity 2022.3+ / 2023.x (IL2CPP, source generator loadable by bundled Roslyn 4.3) | ✅ |
| Generated code | targets C# 11 (`ref` fields), so net7+ runtime |

## Roadmap

- [ ] JSON Pointer-based error reporting in `lint`
- [ ] External `$ref` resolution (cross-document, http)
- [ ] `pattern` / numeric-bound runtime validation hooks
- [ ] `anyOf` / `if-then-else` composition
- [ ] OpenAPI request/response correlation (operation IDs)

## Project layout

```
src/
  NoJsonSchema.Core/             ← library: schema loader, IR, emitter
  NoJsonSchema.SourceGenerator/  ← Roslyn incremental source generator (Unity 2022.3+ compatible)
  NoJsonSchema.Cli/              ← `nojsonschema` dotnet tool
samples/
  Bench/                         ← BenchmarkDotNet vs System.Text.Json source-gen
  Dap/                           ← Real DAP schema → C# (495 files)
tests/
  NoJsonSchema.Core.Tests/
  NoJsonSchema.Roundtrip.Tests/
  NoJsonSchema.Snapshot.Tests/
  NoJsonSchema.SourceGenerator.Tests/
  NoJsonSchema.DapIntegration.Tests/
```

## Building from source

```sh
dotnet build
dotnet test
```

To pack release artifacts locally:

```sh
dotnet pack src/NoJsonSchema.Core/NoJsonSchema.Core.csproj          -c Release -o ./artifacts
dotnet pack src/NoJsonSchema.SourceGenerator/...                    -c Release -o ./artifacts
dotnet pack src/NoJsonSchema.Cli/NoJsonSchema.Cli.csproj            -c Release -o ./artifacts
```

CI publishes to NuGet on `v*` tags via [`.github/workflows/release.yml`](.github/workflows/release.yml).

## License

MIT. See [`LICENSE`](LICENSE).
