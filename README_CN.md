# ason-csharp

[![NuGet](https://img.shields.io/nuget/v/Ason.svg)](https://www.nuget.org/packages/Ason)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

高性能 [ASON](https://github.com/ason-lab/ason)（Array-Schema Object Notation）.NET 序列化/反序列化库 — 零拷贝、SIMD 加速、模式驱动的数据格式，专为 LLM 交互和大规模数据传输设计。

[English](README.md)

## 什么是 ASON？

ASON 将**模式（Schema）**与**数据**分离，消除 JSON 中重复的键名。模式只声明一次，数据行只包含值：

```text
JSON (100 tokens):
{"users":[{"id":1,"name":"Alice","active":true},{"id":2,"name":"Bob","active":false}]}

ASON (~35 tokens, 节省 65%):
[{id@int, name@str, active@bool}]:(1,Alice,true),(2,Bob,false)
```

| 方面         | JSON         | ASON             |
| ------------ | ------------ | ---------------- |
| Token 效率   | 100%         | 30–70% ✓         |
| 键名重复     | 每个对象都有 | 只声明一次 ✓     |
| 人类可读     | 是           | 是 ✓             |
| 嵌套结构     | ✓            | ✓                |
| 类型注解     | 无           | 可选 ✓           |
| 序列化速度   | 1x           | **~1.2–9x 更快** ✓ |
| 数据体积     | 100%         | **40–60%** ✓     |

## 快速上手

添加 Ason NuGet 包：

```bash
dotnet add package Ason
```

发布出来的 NuGet 包是单包多目标，同时包含 `net8.0` 和 `net10.0` 资产。

如果你的应用要固定目标框架，可以在项目文件里显式指定：

```xml
<TargetFramework>net8.0</TargetFramework>
```

或者：

```xml
<TargetFramework>net10.0</TargetFramework>
```

### 定义模式类型

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

### 序列化与反序列化

```csharp
var user = new User(1, "Alice", true);

// 编码
var s = Ason.Ason.encode(user);
// => "{id,name,active}:(1,Alice,true)"

// 带类型注解编码
var typed = Ason.Ason.encodeTyped(user);
// => "{id@int,name@str,active@bool}:(1,Alice,true)"

// 解码
var u2 = Ason.Ason.decodeWith(s, User.FromFields);
// u2 == user ✓
```

### Vec 序列化（模式驱动）

对于 `List<T>`，ASON 只写一次模式，每个元素以紧凑元组输出 — 这是相比 JSON 的关键优势：

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

### 二进制格式

```csharp
// 零拷贝二进制编码（BinaryPrimitives，无中间分配）
var bin = Ason.Ason.encodeBinary(user);

var u3 = Ason.Ason.decodeBinaryWith(bin,
    new[] { "id", "name", "active" },
    new[] { FieldType.Int, FieldType.String, FieldType.Bool },
    User.FromFields);
```

### 美化格式

```csharp
var pretty = Ason.Ason.encodePretty(user);
// => "{id, name, active}:(1, Alice, true)"

var prettyTyped = Ason.Ason.encodePrettyTyped(user);
// => "{id@int, name@str, active@bool}:(1, Alice, true)"
```

## 支持的类型

| 类型          | ASON 表示              | 示例                   |
| ------------- | ---------------------- | ---------------------- |
| long (int64)  | 纯数字                 | `42`、`-100`           |
| double (f64)  | 小数                   | `3.14`、`-0.5`         |
| bool          | 字面量                 | `true`、`false`        |
| string        | 不带引号或带引号       | `Alice`、`"Carol Smith"` |
| 可选类型      | 值或空                 | `hello` 或 _(空白)_    |
| List\<T\>     | `[v1,v2,v3]`          | `[rust,go,python]`     |
| 嵌套结构      | `(field1,field2)`      | `(Engineering,500000)` |

当前 ASON 格式刻意不支持原生 `Dictionary<K,V>` / map 字段。
如果你需要键值集合，请显式建模成 entry-list 数组，例如：

```text
{attrs@[{key@str,value@int}]}:([(age,30),(score,95)])
```

### 嵌套结构

```csharp
record Dept(string Title) : IAsonSchema { /* ... */ }
record Employee(string Name, Dept Dept) : IAsonSchema { /* ... */ }

// 模式反映嵌套关系：
// {name@str,dept@{title@str}}:(Alice,(Engineering))
```

### 可选字段

```text
// 有值：   {id,label}:(1,hello)
// 空值：   {id,label}:(1,)
```

### 数组

```text
{name,tags}:(Alice,[rust,go,python])
```

### 注释

```text
/* 用户列表 */
[{id@int, name@str, active@bool}]:(1,Alice,true),(2,Bob,false)
```

### 多行格式

```text
[{id@int, name@str, active@bool}]:
  (1, Alice, true),
  (2, Bob, false),
  (3, "Carol Smith", true)
```

## API 参考

| 函数                             | 描述                              |
| -------------------------------- | --------------------------------- |
| `Ason.encode(T)`                 | 序列化结构体 → 无类型注解模式     |
| `Ason.encodeTyped(T)`            | 序列化结构体 → 带类型注解模式     |
| `Ason.encode<T>(List<T>)`        | 序列化列表 → 模式只写一次        |
| `Ason.encodeTyped<T>(List<T>)`   | 序列化列表 → 带注解模式          |
| `Ason.decode(string)`            | 反序列化 → 字段袋（`Dictionary<string, object?>`） |
| `Ason.decodeWith<T>(s, fn)`      | 反序列化 → 通过工厂函数生成 T    |
| `Ason.decodeListWith<T>(s, fn)`  | 反序列化 → List\<T\>             |
| `Ason.encodeBinary(T)`           | 二进制编码（零拷贝 BinaryPrimitives）|
| `Ason.decodeBinaryWith<T>(…)`    | 二进制解码 → 类型化 T            |
| `Ason.encodePretty(T)`           | 美化格式编码                      |
| `Ason.encodePrettyTyped(T)`      | 美化格式 + 类型注解               |

## Bench 输出

通过下面命令运行自带 benchmark：

```bash
dotnet run --project examples/Bench/Ason.Examples.Bench.csproj -c Release -f net10.0
```

关键结果：

```text
  Flat struct × 500 (8 fields, vec)
    Serialize:   JSON 16.22ms/60784B | ASON 10.11ms(1.6x)/28327B(46.6%) | BIN 4.92ms(3.3x)/37230B(61.2%)
    Deserialize: JSON    22.09ms | ASON     5.70ms(3.9x) | BIN     2.11ms(10.5x)
```

具体耗时会随运行时、CPU、以及 `Debug/Release` 模式而变化。

## 为什么 ASON 表现更好？

1. **零键哈希** — 模式只解析一次；数据字段按位置索引 `O(1)` 映射，无逐行键字符串哈希。
2. **模式驱动解析** — 反序列化器预知每个字段的期望类型，可直接解析。CPU 分支预测命中率 ~100%。
3. **最小化内存分配** — 所有行共享一个模式引用。全程使用 `ArrayPool`、`stackalloc`、`ReadOnlySpan<char>`。
4. **SIMD 加速** — `SearchValues<char>` 自动选择 SSE2/AVX2/AdvSimd 进行字符扫描。
5. **零拷贝解码** — 解析直接操作 `ReadOnlySpan<char>`，无中间字符串分配。
6. **模式缓存** — 编码器缓存每个类型的模式头字符串；解码器缓存解析后的字段名数组。
7. **零装箱 `WriteValues`** — 直接类型化字段写入，完全绕过 `object?[]` 分配。

### C# 使用的性能技术

- `ArrayPool<char>` / `ArrayPool<byte>` 用于写入缓冲区 — 零 GC 压力
- `ThreadLocal` 写入器复用 — 避免每次调用的租借/归还开销
- 模式头缓存通过 `ConcurrentDictionary<Type, string>`
- 解码模式缓存通过 `ConcurrentDictionary<int, string[]>`
- 零装箱 `WriteValues` / `WriteBinaryValues` 接口方法
- `stackalloc` 用于整数/浮点数格式化
- `ReadOnlySpan<char>` 用于所有解析 — 无字符串拷贝
- `BinaryPrimitives` 用于小端序二进制 I/O — 直接内存操作
- `SearchValues<char>`（.NET 8+，包同时目标 `net8.0` 和 `net10.0`）— 硬件加速字符扫描
- `ref struct` 用于解码器状态 — 完全栈分配
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` 用于热路径

## 示例

```bash
# 基本用法
dotnet run --project examples/Basic -f net10.0

# 复杂嵌套结构、转义、5层深度嵌套
dotnet run --project examples/Complex -f net10.0

# 性能基准测试（ASON vs JSON）
dotnet run --project examples/Bench -c Release -f net10.0
```

如果你本地同时启用了多个目标框架，也可以显式指定：

```bash
dotnet run --project examples/Basic -f net8.0
dotnet run --project examples/Basic -f net10.0
```

## ASON 格式规范

请参阅完整的 [ASON 规范](https://github.com/ason-lab/ason/blob/main/docs/ASON_SPEC_CN.md)，了解语法规则、BNF 文法、转义规则、类型系统和 LLM 集成最佳实践。

### 语法快速参考

| 元素     | 模式                        | 数据                |
| -------- | --------------------------- | ------------------- |
| 对象     | `{field1@type,field2@type}` | `(val1,val2)`       |
| 数组     | `field@[type]`              | `[v1,v2,v3]`        |
| 对象数组 | `field@[{f1@type,f2@type}]` | `[(v1,v2),(v3,v4)]` |
| 嵌套对象 | `field@{f1@type,f2@type}`   | `(v1,(v3,v4))`      |
| 空值     | —                           | _(空白)_            |
| 空字符串 | —                           | `""`                |
| 注释     | —                           | `/* ... */`         |

## 许可证

MIT

## Contributors

- [Athan](https://github.com/athxx)
