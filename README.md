# NoJsonSchema

Generate C# types and zero-dependency UTF-8 JSON parsers/emitters from JSON Schema.

- **Core** (`NoJsonSchema.Core`): JSON Schema → IR → C# emit. Depends on `System.Text.Json` internally.
- **CLI** (`nojsonschema`, dotnet tool): drives the generator from the command line.
- **Generated code**: BCL only — no `System.Text.Json`, no reflection. AOT / IL2CPP friendly.

## Status

Work in progress.

| Milestone | Status | Description |
|---|---|---|
| M1 | ✅ | Project scaffold, CI, smoke tests |
| M2 | ✅ | JSON Schema document loader (STJ-based, internal) |
| M3 | ✅ | `$ref` resolution + IR construction |
| M4 | ✅ | Minimal emitter: object / string / number / bool / array |
| M5 | ✅ | string enum, format (date-time/uuid), schema-level `additionalProperties:false` |
| M6 | ✅ | `allOf` inheritance, `const` properties (oneOf+discriminator deferred) |
| M7 | ✅ | Speed optimizations (UTF-8 ASCII fast path, length-bucketed dispatch) |
| M8 | ⬜ | CLI polish, `dotnet tool pack`, docs |
| M9 | ✅ | DAP sample (Debug Adapter Protocol) end-to-end |

## Benchmarks

Mid-sized DTO (8 properties, 1 nested object, 2 arrays) on Apple M4 / .NET 10, ShortRun job. Baseline is the corresponding **`System.Text.Json` source generator**.

| | NoJsonSchema | STJ source-gen | Ratio | Memory |
|---|---:|---:|---|---|
| **Serialize** | 211.1 ns | 271.4 ns | **0.78× (22% faster)** | same (568 B) |
| **Deserialize** | 416.3 ns | 565.1 ns | **0.74× (26% faster)** | 64% (856 B vs 1328 B) |

The generated tokenizer / writer use `ref byte` fields + `Unsafe.Add` / `Unsafe.CopyBlockUnaligned` instead of span-indexed access — this elides per-byte bounds checks on the hot path. The tokenizer exposes a `Read*` / `TryRead*` only surface (no separate `Read() + Get*` state machine).

See [`samples/Bench`](samples/Bench) for the benchmark project and the exact schema.

## End-to-end: Debug Adapter Protocol

`samples/Dap/` consumes the real [Debug Adapter Protocol schema](https://github.com/microsoft/debug-adapter-protocol/blob/main/debugAdapterProtocol.json) (192 definitions, 226 `$ref`s, 112 `allOf`s, 83 `enum`s, 14 DAP `_enum` extensions).

```bash
nojsonschema generate \
  -i samples/Dap/debugAdapterProtocol.json \
  -o samples/Dap/Generated \
  -n Dap
# → 495 .cs files generated, compiles with 0 errors
```

`tests/NoJsonSchema.DapIntegration.Tests/` covers round-tripping of real DAP messages: `InitializeRequest`, `StoppedEvent`, `StackTraceResponse`, the `Checksum` / `ChecksumAlgorithm` enum pair, and generic dispatch via the namespace-wide `DapSerializer<T>`.

## Building

```bash
dotnet build
dotnet test
```

## CLI usage (planned)

```bash
dotnet tool install -g nojsonschema

nojsonschema generate \
  --input schema.json \
  --output Generated/ \
  --namespace MyApp \
  --type-style record
```
