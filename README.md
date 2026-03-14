# ason-csharp

[![NuGet](https://img.shields.io/nuget/v/Ason.svg)](https://www.nuget.org/packages/Ason)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A high-performance [ASON](https://github.com/ason-lab/ason) (Array-Schema Object Notation) serialization/deserialization library for .NET — zero-copy, SIMD-accelerated, schema-driven data format designed for LLM interactions and large-scale data transmission.

[中文文档](README_CN.md)

## What is ASON?

ASON separates **schema** from **data**, eliminating repetitive keys found in JSON. The schema is declared once, and data rows carry only values:

```text
JSON (100 tokens):
{"users":[{"id":1,"name":"Alice","active":true},{"id":2,"name":"Bob","active":false}]}

ASON (~35 tokens, 65% saving):
[{id@int, name@str, active@bool}]:(1,Alice,true),(2,Bob,false)
```

| Aspect              | JSON         | ASON             |
| ------------------- | ------------ | ---------------- |
| Token efficiency    | 100%         | 30–70% ✓         |
| Key repetition      | Every object | Declared once ✓  |
| Human readable      | Yes          | Yes ✓            |
| Nested structs      | ✓            | ✓                |
| Type annotations    | No           | Optional ✓       |
| Serialization speed | 1x           | **~1.2–8x faster** ✓ |
| Data size           | 100%         | **40–60%** ✓     |

## Quick Start

Add the Ason NuGet package:

```bash
dotnet add package Ason
```

The published NuGet package ships a single package with assets for both `net8.0` and `net10.0`.

If your app targets a specific runtime, you can pin it explicitly in your project file:

```xml
<TargetFramework>net8.0</TargetFramework>
```

or:

```xml
<TargetFramework>net10.0</TargetFramework>
```

### Define a Schema Type

```csharp
using Ason;

record User(long Id, string Name, bool Active) : IAsonSchema
{
    static readonly string[] _names = ["id", "name", "active"];
    static readonly string?[] _types = ["int", "str", "bool"];
    public ReadOnlySpan<string> FieldNames => _names;
    public ReadOnlySpan<string?> FieldTypes => _types;
    public object?[] FieldValues => [Id, Name, Active];

    public static User FromFields(Dictionary<string, object?> m) =>
        new(Convert.ToInt64(m["id"]), (string)m["name"]!, Convert.ToBoolean(m["active"]));
}
```

### Serialize & Deserialize

```csharp
var user = new User(1, "Alice", true);

// Encode
var s = Ason.Ason.encode(user);
// => "{id,name,active}:(1,Alice,true)"

// Encode with type annotations
var typed = Ason.Ason.encodeTyped(user);
// => "{id@int,name@str,active@bool}:(1,Alice,true)"

// Decode
var u2 = Ason.Ason.decodeWith(s, User.FromFields);
// u2 == user ✓
```

### Vec Serialization (Schema-Driven)

For `List<T>`, ASON writes the schema **once** and emits each element as a compact tuple — the key advantage over JSON:

```csharp
var users = new List<User> {
    new(1, "Alice", true),
    new(2, "Bob", false),
};

var s = Ason.Ason.encode<User>(users);
// => "[{id,name,active}]:(1,Alice,true),(2,Bob,false)"

var users2 = Ason.Ason.decodeListWith(s, User.FromFields);
// users2.Count == 2 ✓
```

### Binary Format

```csharp
// Zero-copy binary encoding (BinaryPrimitives, no intermediate allocation)
var bin = Ason.Ason.encodeBinary(user);

var u3 = Ason.Ason.decodeBinaryWith(bin,
    new[] { "id", "name", "active" },
    new[] { FieldType.Int, FieldType.String, FieldType.Bool },
    User.FromFields);
```

### Pretty Format

```csharp
var pretty = Ason.Ason.encodePretty(user);
// => "{id, name, active}:(1, Alice, true)"

var prettyTyped = Ason.Ason.encodePrettyTyped(user);
// => "{id@int, name@str, active@bool}:(1, Alice, true)"
```

## Supported Types

| Type           | ASON Representation       | Example                  |
| -------------- | ------------------------- | ------------------------ |
| long (int64)   | Plain number              | `42`, `-100`             |
| double (f64)   | Decimal number            | `3.14`, `-0.5`           |
| bool           | Literal                   | `true`, `false`          |
| string         | Unquoted or quoted        | `Alice`, `"Carol Smith"` |
| Optional       | Value or empty            | `hello` or _(blank)_     |
| List\<T\>      | `[v1,v2,v3]`             | `[rust,go,python]`       |
| Nested struct  | `(field1,field2)`         | `(Engineering,500000)`   |

Native `Dictionary<K,V>` / map fields are intentionally unsupported in the current ASON format.
If you need keyed collections, model them explicitly as entry-list arrays such as:

```text
{attrs@[{key@str,value@int}]}:([(age,30),(score,95)])
```

### Nested Structs

```csharp
record Dept(string Title) : IAsonSchema { /* ... */ }
record Employee(string Name, Dept Dept) : IAsonSchema { /* ... */ }

// Schema reflects nesting:
// {name@str,dept@{title@str}}:(Alice,(Engineering))
```

### Optional Fields

```text
// With value@{id,label}:(1,hello)
// With null@{id,label}:(1,)
```

### Arrays

```text
{name,tags}:(Alice,[rust,go,python])
```

### Comments

```text
/* user list */
[{id@int, name@str, active@bool}]:(1,Alice,true),(2,Bob,false)
```

### Multiline Format

```text
[{id@int, name@str, active@bool}]:
  (1, Alice, true),
  (2, Bob, false),
  (3, "Carol Smith", true)
```

## API Reference

| Function                       | Description                                              |
| ------------------------------ | -------------------------------------------------------- |
| `Ason.encode(T)`               | Serialize struct → unannotated schema                    |
| `Ason.encodeTyped(T)`          | Serialize struct → annotated schema                      |
| `Ason.encode<T>(List<T>)`      | Serialize list → unannotated schema (written once)       |
| `Ason.encodeTyped<T>(List<T>)` | Serialize list → annotated schema                        |
| `Ason.decode(string)`          | Deserialize → field bag (`Dictionary<string, object?>`)  |
| `Ason.decodeWith<T>(s, fn)`    | Deserialize → typed T via factory                        |
| `Ason.decodeListWith<T>(s, fn)`| Deserialize → List\<T\> via factory                      |
| `Ason.encodeBinary(T)`         | Binary encode (zero-copy BinaryPrimitives)               |
| `Ason.decodeBinaryWith<T>(…)`  | Binary decode → typed T                                  |
| `Ason.encodePretty(T)`         | Pretty-format encode                                     |
| `Ason.encodePrettyTyped(T)`    | Pretty-format with type annotations                      |

## Benchmark Output

Run the bundled benchmark with:

```bash
dotnet run --project examples/Bench/Ason.Examples.Bench.csproj -c Release
```

Headline numbers::

```text
  Flat struct × 500 (8 fields, vec)
    Serialize:   JSON 16.22ms/60784B | ASON 10.11ms(1.6x)/28327B(46.6%) | BIN 4.92ms(3.3x)/37230B(61.2%)
    Deserialize: JSON    22.09ms | ASON     5.70ms(3.9x) | BIN     2.11ms(10.5x)
```


Actual timings vary by runtime, CPU, and whether you run `Debug` or `Release`.

## Why ASON Performs Well

1. **Zero key-hashing** — Schema parsed once; fields mapped by position index `O(1)`, no per-row key string hashing.
2. **Schema-driven parsing** — Deserializer knows expected types, enabling direct parsing. CPU branch prediction hits ~100%.
3. **Minimal allocation** — All rows share one schema reference. `ArrayPool`, `stackalloc`, `ReadOnlySpan<char>` everywhere.
4. **SIMD acceleration** — `SearchValues<char>` auto-selects SSE2/AVX2/AdvSimd for character scanning.
5. **Zero-copy decode** — Parsing operates directly on `ReadOnlySpan<char>`, no intermediate string allocation.
6. **Schema caching** — Encoder caches schema header strings per type; decoder caches parsed field name arrays.
7. **Zero-boxing `WriteValues`** — Direct typed field writes bypass `object?[]` allocation entirely.

### C# Performance Techniques Used

- `ArrayPool<char>` / `ArrayPool<byte>` for writer buffers — zero GC pressure
- `ThreadLocal` writer reuse for single-struct encode — no rent/return overhead
- Schema header caching via `ConcurrentDictionary<Type, string>`
- Decoded schema caching via `ConcurrentDictionary<int, string[]>`
- Zero-boxing `WriteValues` / `WriteBinaryValues` interface methods
- `stackalloc` for integer/float formatting
- `ReadOnlySpan<char>` for all parsing — no string copies
- `BinaryPrimitives` for little-endian binary I/O — direct memory operations
- `SearchValues<char>` (`.NET 8+`, package targets `net8.0` and `net10.0`) — hardware-accelerated character scanning
- `ref struct` for decoder state — fully stack-allocated
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on hot paths

## Examples

```bash
# Basic usage
dotnet run --project examples/Basic

# Complex nested structures, escaping, 5-level deep nesting
dotnet run --project examples/Complex

# Performance benchmark (ASON vs JSON)
dotnet run --project examples/Bench -c Release
```

If you have both target frameworks enabled locally, you can run a specific one:

```bash
dotnet run --project examples/Basic -f net8.0
dotnet run --project examples/Basic -f net10.0
```

## ASON Format Specification

See the full [ASON Spec](https://github.com/ason-lab/ason/blob/main/docs/ASON_SPEC.md) for syntax rules, BNF grammar, escape rules, type system, and LLM integration best practices.

### Syntax Quick Reference

| Element       | Schema                      | Data                |
| ------------- | --------------------------- | ------------------- |
| Object        | `{field1@type,field2@type}` | `(val1,val2)`       |
| Array         | `field@[type]`              | `[v1,v2,v3]`        |
| Object array  | `field@[{f1@type,f2@type}]` | `[(v1,v2),(v3,v4)]` |
| Nested object | `field@{f1@type,f2@type}`   | `(v1,(v3,v4))`      |
| Null          | —                           | _(blank)_           |
| Empty string  | —                           | `""`                |
| Comment       | —                           | `/* ... */`         |

## License

MIT

## Contributors

- [Athan](https://github.com/athxx)
