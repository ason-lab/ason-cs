using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ason;
using AEncoder = Ason.Encoder;
using ADecoder = Ason.Decoder;

internal static class Program
{
    static void Main()
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            ASON vs JSON Comprehensive Benchmark              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"System: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} | .NET {Environment.Version}");
        Console.WriteLine("Iterations per test: 100");

        WarmUp();

        Console.WriteLine();
        PrintSection("Section 1: Flat Struct (schema-driven vec)", 47);
        Console.WriteLine();
        foreach (var count in new[] { 100, 500, 1000, 5000 })
        {
            BenchFlat(count, 100).Print();
            Console.WriteLine();
        }

        PrintSection("Section 2: All-Types Struct (16 fields)", 48);
        Console.WriteLine();
        foreach (var count in new[] { 100, 500 })
        {
            BenchAllTypes(count, 100).Print();
            Console.WriteLine();
        }

        PrintSection("Section 3: 5-Level Deep Nesting (Company hierarchy)", 60);
        Console.WriteLine();
        foreach (var count in new[] { 10, 50, 100 })
        {
            BenchDeep(count, 50).Print();
            Console.WriteLine();
        }

        PrintSection("Section 4: Single Struct Roundtrip (10000x)", 48);
        Console.WriteLine();
        var (asonFlat, jsonFlat) = BenchSingleRoundtrip(10_000);
        Console.WriteLine($"  Flat:  ASON {asonFlat,8:F2}ms | JSON {jsonFlat,8:F2}ms | ratio {jsonFlat / asonFlat:F2}x");
        var (asonDeep, jsonDeep) = BenchDeepSingleRoundtrip(10_000);
        Console.WriteLine($"  Deep:  ASON {asonDeep,8:F2}ms | JSON {jsonDeep,8:F2}ms | ratio {jsonDeep / asonDeep:F2}x");

        Console.WriteLine();
        PrintSection("Section 5: Large Payload (10k records)", 48);
        Console.WriteLine();
        Console.WriteLine("  (10 iterations for large payload)");
        BenchFlat(10_000, 10).Print();

        Console.WriteLine();
        PrintSection("Section 6: Annotated vs Unannotated Schema (deserialize)", 64);
        Console.WriteLine();
        {
            var users = DataGen.GenerateUsers(1000);
            var untyped = AEncoder.Encode<BUser>(users);
            var typed = AEncoder.EncodeTyped<BUser>(users);
            const int deIters = 200;

            var untypedMs = MeasureMs(() =>
            {
                for (int i = 0; i < deIters; i++) ADecoder.DecodeListWith(untyped, BUser.FromFields);
            });
            var typedMs = MeasureMs(() =>
            {
                for (int i = 0; i < deIters; i++) ADecoder.DecodeListWith(typed, BUser.FromFields);
            });

            Console.WriteLine($"  Flat struct × 1000 ({deIters} iters, deserialize only)");
            Console.WriteLine($"    Unannotated: {untypedMs,8:F2}ms  ({Utf8Bytes(untyped)} B)");
            Console.WriteLine($"    Annotated:   {typedMs,8:F2}ms  ({Utf8Bytes(typed)} B)");
            Console.WriteLine($"    Ratio: {untypedMs / typedMs:F3}x (unannotated / annotated)");
        }

        Console.WriteLine();
        PrintSection("Section 7: Annotated vs Unannotated Schema (serialize)", 62);
        Console.WriteLine();
        {
            var users = DataGen.GenerateUsers(1000);
            const int serIters = 200;
            string untyped = "";
            string typed = "";

            var untypedMs = MeasureMs(() =>
            {
                for (int i = 0; i < serIters; i++) untyped = AEncoder.Encode<BUser>(users);
            });
            var typedMs = MeasureMs(() =>
            {
                for (int i = 0; i < serIters; i++) typed = AEncoder.EncodeTyped<BUser>(users);
            });

            Console.WriteLine($"  Flat struct × 1000 ({serIters} iters, serialize only)");
            Console.WriteLine($"    Unannotated: {untypedMs,8:F2}ms  ({Utf8Bytes(untyped)} B)");
            Console.WriteLine($"    Annotated:   {typedMs,8:F2}ms  ({Utf8Bytes(typed)} B)");
            Console.WriteLine($"    Ratio: {untypedMs / typedMs:F3}x (unannotated / annotated)");
        }

        Console.WriteLine();
        PrintSection("Section 8: Throughput Summary", 48);
        Console.WriteLine();
        {
            var users = DataGen.GenerateUsers(1000);
            var json = JsonSerializer.Serialize(users);
            var ason = AEncoder.Encode<BUser>(users);
            const int throughputIters = 100;

            var jsonSerSecs = MeasureMs(() =>
            {
                for (int i = 0; i < throughputIters; i++) JsonSerializer.Serialize(users);
            }) / 1000.0;
            var asonSerSecs = MeasureMs(() =>
            {
                for (int i = 0; i < throughputIters; i++) AEncoder.Encode<BUser>(users);
            }) / 1000.0;
            var jsonDeSecs = MeasureMs(() =>
            {
                for (int i = 0; i < throughputIters; i++) JsonSerializer.Deserialize<List<BUser>>(json);
            }) / 1000.0;
            var asonDeSecs = MeasureMs(() =>
            {
                for (int i = 0; i < throughputIters; i++) ADecoder.DecodeListWith(ason, BUser.FromFields);
            }) / 1000.0;

            var totalRecords = 1000.0 * throughputIters;
            Console.WriteLine($"  Serialize throughput (1000 records × {throughputIters} iters):");
            Console.WriteLine($"    JSON: {totalRecords / jsonSerSecs:F0} records/s");
            Console.WriteLine($"    ASON: {totalRecords / asonSerSecs:F0} records/s");
            Console.WriteLine($"    Speed: {jsonSerSecs / asonSerSecs:F2}x");
            Console.WriteLine("  Deserialize throughput:");
            Console.WriteLine($"    JSON: {totalRecords / jsonDeSecs:F0} records/s");
            Console.WriteLine($"    ASON: {totalRecords / asonDeSecs:F0} records/s");
            Console.WriteLine($"    Speed: {jsonDeSecs / asonDeSecs:F2}x");
        }

        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    Benchmark Complete                        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    }

    static void WarmUp()
    {
        Console.WriteLine("Warming up...");
        var users = DataGen.GenerateUsers(100);
        var companies = DataGen.GenerateCompanies(10);
        var userJson = JsonSerializer.Serialize(users);
        var userAson = AEncoder.Encode<BUser>(users);
        var userBin = BinaryCodec.EncodeBinary<BUser>(users);
        var companyJson = JsonSerializer.Serialize(companies);
        var companyAson = AEncoder.Encode<BCompany>(companies);
        var companyBin = BinaryCodec.EncodeBinary<BCompany>(companies);

        for (int i = 0; i < 200; i++)
        {
            JsonSerializer.Serialize(users);
            AEncoder.Encode<BUser>(users);
            BinaryCodec.EncodeBinary<BUser>(users);
            JsonSerializer.Deserialize<List<BUser>>(userJson);
            ADecoder.DecodeListWith(userAson, BUser.FromFields);
            BinaryCodec.DecodeBinaryListWith(userBin, BUser.Fields, BUser.BinaryTypes, BUser.FromFields);
        }

        for (int i = 0; i < 100; i++)
        {
            JsonSerializer.Serialize(companies);
            AEncoder.Encode<BCompany>(companies);
            BinaryCodec.EncodeBinary<BCompany>(companies);
            JsonSerializer.Deserialize<List<BCompany>>(companyJson);
            ADecoder.DecodeListWith(companyAson, BCompany.FromFields);
            BenchDecode.DecodeCompanyListBinary(companyBin);
        }
        Console.WriteLine("Warmup complete.");
    }

    static BenchResult BenchFlat(int count, int iterations)
    {
        var users = DataGen.GenerateUsers(count);
        var json = JsonSerializer.Serialize(users);
        var ason = AEncoder.Encode<BUser>(users);
        var bin = BinaryCodec.EncodeBinary<BUser>(users);

        var jsonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Serialize(users);
        });
        var asonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) AEncoder.Encode<BUser>(users);
        });
        var binSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BinaryCodec.EncodeBinary<BUser>(users);
        });
        var jsonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Deserialize<List<BUser>>(json);
        });
        var asonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) ADecoder.DecodeListWith(ason, BUser.FromFields);
        });
        var binDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BinaryCodec.DecodeBinaryListWith(bin, BUser.Fields, BUser.BinaryTypes, BUser.FromFields);
        });

        return new BenchResult(
            $"Flat struct × {count} (8 fields, vec)",
            jsonSer, asonSer, binSer,
            jsonDe, asonDe, binDe,
            Utf8Bytes(json), Utf8Bytes(ason), bin.Length);
    }

    static BenchResult BenchAllTypes(int count, int iterations)
    {
        var items = DataGen.GenerateAllTypes(count);
        var json = JsonSerializer.Serialize(items);
        var ason = AEncoder.Encode<BAllTypes>(items);
        var bin = BinaryCodec.EncodeBinary<BAllTypes>(items);

        var jsonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Serialize(items);
        });
        var asonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) AEncoder.Encode<BAllTypes>(items);
        });
        var binSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BinaryCodec.EncodeBinary<BAllTypes>(items);
        });
        var jsonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Deserialize<List<BAllTypes>>(json);
        });
        var asonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) ADecoder.DecodeListWith(ason, BAllTypes.FromFields);
        });
        var binDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BinaryCodec.DecodeBinaryListWith(bin, BAllTypes.Fields, BAllTypes.BinaryTypes, BAllTypes.FromFields);
        });

        return new BenchResult(
            $"All-types struct × {count} (16 fields, vec)",
            jsonSer, asonSer, binSer,
            jsonDe, asonDe, binDe,
            Utf8Bytes(json), Utf8Bytes(ason), bin.Length);
    }

    static BenchResult BenchDeep(int count, int iterations)
    {
        var companies = DataGen.GenerateCompanies(count);
        var json = JsonSerializer.Serialize(companies);
        var ason = AEncoder.Encode<BCompany>(companies);
        var bin = BinaryCodec.EncodeBinary<BCompany>(companies);

        var jsonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Serialize(companies);
        });
        var asonSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) AEncoder.Encode<BCompany>(companies);
        });
        var binSer = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BinaryCodec.EncodeBinary<BCompany>(companies);
        });
        var jsonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) JsonSerializer.Deserialize<List<BCompany>>(json);
        });
        var asonDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) ADecoder.DecodeListWith(ason, BCompany.FromFields);
        });
        var binDe = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++) BenchDecode.DecodeCompanyListBinary(bin);
        });

        return new BenchResult(
            $"5-level deep × {count} (Company>Division>Team>Project>Task)",
            jsonSer, asonSer, binSer,
            jsonDe, asonDe, binDe,
            Utf8Bytes(json), Utf8Bytes(ason), bin.Length);
    }

    static (double asonMs, double jsonMs) BenchSingleRoundtrip(int iterations)
    {
        var user = DataGen.GenerateUsers(1)[0];

        var asonMs = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var text = AEncoder.Encode(user);
                ADecoder.DecodeWith(text, BUser.FromFields);
            }
        });
        var jsonMs = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var text = JsonSerializer.Serialize(user);
                JsonSerializer.Deserialize<BUser>(text);
            }
        });

        return (asonMs, jsonMs);
    }

    static (double asonMs, double jsonMs) BenchDeepSingleRoundtrip(int iterations)
    {
        var company = DataGen.GenerateCompanies(1)[0];

        var asonMs = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var text = AEncoder.Encode(company);
                ADecoder.DecodeWith(text, BCompany.FromFields);
            }
        });
        var jsonMs = MeasureMs(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var text = JsonSerializer.Serialize(company);
                JsonSerializer.Deserialize<BCompany>(text);
            }
        });

        return (asonMs, jsonMs);
    }

    static double MeasureMs(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    static string FormatRatio(double baseValue, double targetValue)
    {
        if (targetValue <= 0) return "infx";
        var ratio = (baseValue / targetValue).ToString("0.0");
        if (ratio.EndsWith(".0")) ratio = ratio[..^2];
        return ratio + "x";
    }

    static string FormatPercent(int part, int whole)
    {
        if (whole <= 0) return "0%";
        var pct = ((double)part * 100.0 / whole).ToString("0.0");
        if (pct.EndsWith(".0")) pct = pct[..^2];
        return pct + "%";
    }

    static void PrintSection(string title, int width)
    {
        var line = new string('─', width - 2);
        Console.WriteLine($"┌{line}┐");
        Console.WriteLine($"│ {title.PadRight(width - 4)} │");
        Console.WriteLine($"└{line}┘");
    }

    static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    static class DataGen
    {
        static readonly string[] Names = ["Alice", "Bob", "Carol", "David", "Eve", "Frank", "Grace", "Hank"];
        static readonly string[] Roles = ["engineer", "designer", "manager", "analyst"];
        static readonly string[] Cities = ["NYC", "LA", "Chicago", "Houston", "Phoenix"];
        static readonly string[] Locs = ["NYC", "London", "Tokyo", "Berlin"];

        public static List<BUser> GenerateUsers(int n)
        {
            var list = new List<BUser>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new BUser(
                    i,
                    Names[i % Names.Length],
                    $"{Names[i % Names.Length].ToLower()}@example.com",
                    25 + i % 40,
                    50.0 + i % 50 + 0.5,
                    i % 3 != 0,
                    Roles[i % Roles.Length],
                    Cities[i % Cities.Length]));
            }
            return list;
        }

        public static List<BAllTypes> GenerateAllTypes(int n)
        {
            var list = new List<BAllTypes>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new BAllTypes(
                    i % 2 == 0,
                    i * 1000,
                    i * 100_000L,
                    i * 0.25 + 0.5,
                    i * 1.5 + 1.25,
                    $"item_{i}",
                    $"label_{i % 7}",
                    i % 3 != 0,
                    i % 2 == 0 ? i : null,
                    null,
                    i % 4 == 0 ? $"note_{i}" : null,
                    i % 5 == 0 ? true : null,
                    new List<long> { i, i + 1, i + 2 },
                    new List<double> { i * 1.1, i * 1.2 },
                    new List<string> { $"tag{i % 5}", $"cat{i % 3}" },
                    new List<bool> { i % 2 == 0, i % 3 == 0 }));
            }
            return list;
        }

        public static List<BCompany> GenerateCompanies(int n)
        {
            var list = new List<BCompany>(n);
            for (int i = 0; i < n; i++)
            {
                var divisions = new List<BDivision>(2);
                for (int d = 0; d < 2; d++)
                {
                    var teams = new List<BTeam>(2);
                    for (int t = 0; t < 2; t++)
                    {
                        var projects = new List<BProject>(3);
                        for (int p = 0; p < 3; p++)
                        {
                            var tasks = new List<BTask>(4);
                            for (int tk = 0; tk < 4; tk++)
                            {
                                tasks.Add(new BTask(i * 100 + d * 10 + t * 5 + tk, $"Task_{tk}", tk % 3 + 1, tk % 2 == 0, 2.0 + tk * 1.5));
                            }
                            projects.Add(new BProject($"Proj_{t}_{p}", 100.0 + p * 50.5, p % 2 == 0, tasks));
                        }
                        teams.Add(new BTeam($"Team_{i}_{d}_{t}", Names[t % 4], 5 + t * 2, projects));
                    }
                    divisions.Add(new BDivision($"Div_{i}_{d}", Locs[d % Locs.Length], 50 + d * 20, teams));
                }
                list.Add(new BCompany($"Corp_{i}", 1990 + i % 35, 10.0 + i * 5.5, i % 2 == 0, divisions, new List<string> { "enterprise", "tech", $"sector_{i % 5}" }));
            }
            return list;
        }
    }

    record BUser(long Id, string Name, string Email, long Age, double Score, bool Active, string Role, string City) : IAsonSchema
    {
        public static readonly string[] Fields = ["id", "name", "email", "age", "score", "active", "role", "city"];
        public static readonly string?[] Types = ["int", "str", "str", "int", "float", "bool", "str", "str"];
        public static readonly FieldType[] BinaryTypes = [FieldType.Int, FieldType.String, FieldType.String, FieldType.Int, FieldType.Double, FieldType.Bool, FieldType.String, FieldType.String];

        ReadOnlySpan<string> IAsonSchema.FieldNames => Fields;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Id, Name, Email, Age, Score, Active, Role, City];

        public static BUser FromFields(Dictionary<string, object?> m) =>
            new(Convert.ToInt64(m["id"]), (string)m["name"]!, (string)m["email"]!,
                Convert.ToInt64(m["age"]), Convert.ToDouble(m["score"]),
                Convert.ToBoolean(m["active"]), (string)m["role"]!, (string)m["city"]!);
    }

    record BAllTypes(
        bool B,
        int I32v,
        long I64v,
        double F64v,
        double F64b,
        string S,
        string Label,
        bool Enabled,
        long? OptSome,
        long? OptNone,
        string? Note,
        bool? FlagOpt,
        List<long> VecInt,
        List<double> VecFloat,
        List<string> VecStr,
        List<bool> VecBool) : IAsonSchema
    {
        public static readonly string[] Fields = ["b", "i32v", "i64v", "f64v", "f64b", "s", "label", "enabled", "opt_some", "opt_none", "note", "flag_opt", "vec_int", "vec_float", "vec_str", "vec_bool"];
        public static readonly string?[] Types = ["bool", "int", "int", "float", "float", "str", "str", "bool", "int?", "int?", "str?", "bool?", "[int]", "[float]", "[str]", "[bool]"];
        public static readonly FieldType[] BinaryTypes = [FieldType.Bool, FieldType.Int, FieldType.Int, FieldType.Double, FieldType.Double, FieldType.String, FieldType.String, FieldType.Bool, FieldType.OptionalInt, FieldType.OptionalInt, FieldType.OptionalString, FieldType.OptionalBool, FieldType.ListInt, FieldType.ListDouble, FieldType.ListString, FieldType.ListBool];

        ReadOnlySpan<string> IAsonSchema.FieldNames => Fields;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [B, I32v, I64v, F64v, F64b, S, Label, Enabled, OptSome, OptNone, Note, FlagOpt, VecInt, VecFloat, VecStr, VecBool];

        void IAsonSchema.WriteBinaryValues(ref BinWriter bw)
        {
            bw.WriteBool(B);
            bw.WriteI64(I32v);
            bw.WriteI64(I64v);
            bw.WriteF64(F64v);
            bw.WriteF64(F64b);
            bw.WriteString(S);
            bw.WriteString(Label);
            bw.WriteBool(Enabled);

            if (OptSome.HasValue) { bw.WriteU8(1); bw.WriteI64(OptSome.Value); } else bw.WriteU8(0);
            if (OptNone.HasValue) { bw.WriteU8(1); bw.WriteI64(OptNone.Value); } else bw.WriteU8(0);
            if (Note is not null) { bw.WriteU8(1); bw.WriteString(Note); } else bw.WriteU8(0);
            if (FlagOpt.HasValue) { bw.WriteU8(1); bw.WriteBool(FlagOpt.Value); } else bw.WriteU8(0);

            bw.WriteU32((uint)VecInt.Count);
            foreach (var v in VecInt) bw.WriteI64(v);

            bw.WriteU32((uint)VecFloat.Count);
            foreach (var v in VecFloat) bw.WriteF64(v);

            bw.WriteU32((uint)VecStr.Count);
            foreach (var v in VecStr) bw.WriteString(v);

            bw.WriteU32((uint)VecBool.Count);
            foreach (var v in VecBool) bw.WriteBool(v);
        }

        public static BAllTypes FromFields(Dictionary<string, object?> m) =>
            new(
                Convert.ToBoolean(m["b"]),
                Convert.ToInt32(m["i32v"]),
                Convert.ToInt64(m["i64v"]),
                Convert.ToDouble(m["f64v"]),
                Convert.ToDouble(m["f64b"]),
                (string)m["s"]!,
                (string)m["label"]!,
                Convert.ToBoolean(m["enabled"]),
                BenchDecode.AsNullableLong(m["opt_some"]),
                BenchDecode.AsNullableLong(m["opt_none"]),
                m["note"] as string,
                BenchDecode.AsNullableBool(m["flag_opt"]),
                BenchDecode.AsLongList(m["vec_int"]),
                BenchDecode.AsDoubleList(m["vec_float"]),
                BenchDecode.AsStringList(m["vec_str"]),
                BenchDecode.AsBoolList(m["vec_bool"]));
    }

    record BTask(long Id, string Title, long Priority, bool Done, double Hours) : IAsonSchema
    {
        static readonly string[] Names = ["id", "title", "priority", "done", "hours"];
        static readonly string?[] Types = ["int", "str", "int", "bool", "float"];
        ReadOnlySpan<string> IAsonSchema.FieldNames => Names;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Id, Title, Priority, Done, Hours];

        public static BTask FromFields(Dictionary<string, object?> m) =>
            new(Convert.ToInt64(m["id"]), (string)m["title"]!, Convert.ToInt64(m["priority"]), Convert.ToBoolean(m["done"]), Convert.ToDouble(m["hours"]));

        public static BTask FromValue(object? value)
        {
            if (value is Dictionary<string, object?> map) return FromFields(map);
            var list = value as List<object?> ?? [];
            return new(
                list.Count > 0 && list[0] is not null ? Convert.ToInt64(list[0]) : 0,
                list.Count > 1 ? list[1]?.ToString() ?? "" : "",
                list.Count > 2 && list[2] is not null ? Convert.ToInt64(list[2]) : 0,
                list.Count > 3 && list[3] is not null && Convert.ToBoolean(list[3]),
                list.Count > 4 && list[4] is not null ? Convert.ToDouble(list[4]) : 0.0);
        }
    }

    record BProject(string Name, double Budget, bool Active, List<BTask> Tasks) : IAsonSchema
    {
        static readonly string[] Names = ["name", "budget", "active", "tasks"];
        static readonly string?[] Types = ["str", "float", "bool", null];
        ReadOnlySpan<string> IAsonSchema.FieldNames => Names;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Name, Budget, Active, Tasks];

        public static BProject FromFields(Dictionary<string, object?> m) =>
            new((string)m["name"]!, Convert.ToDouble(m["budget"]), Convert.ToBoolean(m["active"]), BenchDecode.AsObjectList(m["tasks"]).ConvertAll(BTask.FromValue));

        public static BProject FromValue(object? value)
        {
            if (value is Dictionary<string, object?> map) return FromFields(map);
            var list = value as List<object?> ?? [];
            return new(
                list.Count > 0 ? list[0]?.ToString() ?? "" : "",
                list.Count > 1 && list[1] is not null ? Convert.ToDouble(list[1]) : 0.0,
                list.Count > 2 && list[2] is not null && Convert.ToBoolean(list[2]),
                list.Count > 3 ? BenchDecode.AsObjectList(list[3]).ConvertAll(BTask.FromValue) : []);
        }
    }

    record BTeam(string Name, string Lead, long Size, List<BProject> Projects) : IAsonSchema
    {
        static readonly string[] Names = ["name", "lead", "size", "projects"];
        static readonly string?[] Types = ["str", "str", "int", null];
        ReadOnlySpan<string> IAsonSchema.FieldNames => Names;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Name, Lead, Size, Projects];

        public static BTeam FromFields(Dictionary<string, object?> m) =>
            new((string)m["name"]!, (string)m["lead"]!, Convert.ToInt64(m["size"]), BenchDecode.AsObjectList(m["projects"]).ConvertAll(BProject.FromValue));

        public static BTeam FromValue(object? value)
        {
            if (value is Dictionary<string, object?> map) return FromFields(map);
            var list = value as List<object?> ?? [];
            return new(
                list.Count > 0 ? list[0]?.ToString() ?? "" : "",
                list.Count > 1 ? list[1]?.ToString() ?? "" : "",
                list.Count > 2 && list[2] is not null ? Convert.ToInt64(list[2]) : 0,
                list.Count > 3 ? BenchDecode.AsObjectList(list[3]).ConvertAll(BProject.FromValue) : []);
        }
    }

    record BDivision(string Name, string Location, long Headcount, List<BTeam> Teams) : IAsonSchema
    {
        static readonly string[] Names = ["name", "location", "headcount", "teams"];
        static readonly string?[] Types = ["str", "str", "int", null];
        ReadOnlySpan<string> IAsonSchema.FieldNames => Names;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Name, Location, Headcount, Teams];

        public static BDivision FromFields(Dictionary<string, object?> m) =>
            new((string)m["name"]!, (string)m["location"]!, Convert.ToInt64(m["headcount"]), BenchDecode.AsObjectList(m["teams"]).ConvertAll(BTeam.FromValue));

        public static BDivision FromValue(object? value)
        {
            if (value is Dictionary<string, object?> map) return FromFields(map);
            var list = value as List<object?> ?? [];
            return new(
                list.Count > 0 ? list[0]?.ToString() ?? "" : "",
                list.Count > 1 ? list[1]?.ToString() ?? "" : "",
                list.Count > 2 && list[2] is not null ? Convert.ToInt64(list[2]) : 0,
                list.Count > 3 ? BenchDecode.AsObjectList(list[3]).ConvertAll(BTeam.FromValue) : []);
        }
    }

    record BCompany(string Name, long Founded, double RevenueM, bool Public, List<BDivision> Divisions, List<string> Tags) : IAsonSchema
    {
        static readonly string[] Names = ["name", "founded", "revenue_m", "public", "divisions", "tags"];
        static readonly string?[] Types = ["str", "int", "float", "bool", null, "[str]"];
        ReadOnlySpan<string> IAsonSchema.FieldNames => Names;
        ReadOnlySpan<string?> IAsonSchema.FieldTypes => Types;
        object?[] IAsonSchema.FieldValues => [Name, Founded, RevenueM, Public, Divisions, Tags];

        public static BCompany FromFields(Dictionary<string, object?> m) =>
            new((string)m["name"]!, Convert.ToInt64(m["founded"]), Convert.ToDouble(m["revenue_m"]), Convert.ToBoolean(m["public"]), BenchDecode.AsObjectList(m["divisions"]).ConvertAll(BDivision.FromValue), BenchDecode.AsStringList(m["tags"]));
    }

    static class BenchDecode
    {
        public static List<object?> AsObjectList(object? value) => value as List<object?> ?? [];
        public static List<string> AsStringList(object? value) => AsObjectList(value).ConvertAll(v => v?.ToString() ?? "");
        public static List<long> AsLongList(object? value) => AsObjectList(value).ConvertAll(v => v is null ? 0L : Convert.ToInt64(v));
        public static List<double> AsDoubleList(object? value) => AsObjectList(value).ConvertAll(v => v is null ? 0.0 : Convert.ToDouble(v));
        public static List<bool> AsBoolList(object? value) => AsObjectList(value).ConvertAll(v => v is not null && Convert.ToBoolean(v));
        public static long? AsNullableLong(object? value) => value is null ? null : Convert.ToInt64(value);
        public static bool? AsNullableBool(object? value) => value is null ? null : Convert.ToBoolean(value);

        public static List<BCompany> DecodeCompanyListBinary(byte[] data)
        {
            var reader = new BenchBinReader(data);
            var count = reader.ReadU32();
            var result = new List<BCompany>((int)count);
            for (uint i = 0; i < count; i++) result.Add(ReadCompany(ref reader));
            return result;
        }

        static BCompany ReadCompany(ref BenchBinReader reader)
        {
            var name = reader.ReadString();
            var founded = reader.ReadI64();
            var revenueM = reader.ReadF64();
            var isPublic = reader.ReadBool();

            var divisionCount = reader.ReadU32();
            var divisions = new List<BDivision>((int)divisionCount);
            for (uint i = 0; i < divisionCount; i++) divisions.Add(ReadDivision(ref reader));

            var tagCount = reader.ReadU32();
            var tags = new List<string>((int)tagCount);
            for (uint i = 0; i < tagCount; i++) tags.Add(reader.ReadString());

            return new BCompany(name, founded, revenueM, isPublic, divisions, tags);
        }

        static BDivision ReadDivision(ref BenchBinReader reader)
        {
            var name = reader.ReadString();
            var location = reader.ReadString();
            var headcount = reader.ReadI64();

            var teamCount = reader.ReadU32();
            var teams = new List<BTeam>((int)teamCount);
            for (uint i = 0; i < teamCount; i++) teams.Add(ReadTeam(ref reader));

            return new BDivision(name, location, headcount, teams);
        }

        static BTeam ReadTeam(ref BenchBinReader reader)
        {
            var name = reader.ReadString();
            var lead = reader.ReadString();
            var size = reader.ReadI64();

            var projectCount = reader.ReadU32();
            var projects = new List<BProject>((int)projectCount);
            for (uint i = 0; i < projectCount; i++) projects.Add(ReadProject(ref reader));

            return new BTeam(name, lead, size, projects);
        }

        static BProject ReadProject(ref BenchBinReader reader)
        {
            var name = reader.ReadString();
            var budget = reader.ReadF64();
            var active = reader.ReadBool();

            var taskCount = reader.ReadU32();
            var tasks = new List<BTask>((int)taskCount);
            for (uint i = 0; i < taskCount; i++) tasks.Add(ReadTask(ref reader));

            return new BProject(name, budget, active, tasks);
        }

        static BTask ReadTask(ref BenchBinReader reader) =>
            new(reader.ReadI64(), reader.ReadString(), reader.ReadI64(), reader.ReadBool(), reader.ReadF64());

        ref struct BenchBinReader
        {
            ReadOnlySpan<byte> _data;
            int _pos;

            public BenchBinReader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _pos = 0;
            }

            public uint ReadU32()
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
                _pos += 4;
                return value;
            }

            public long ReadI64()
            {
                var value = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos, 8));
                _pos += 8;
                return value;
            }

            public double ReadF64()
            {
                var value = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_pos, 8));
                _pos += 8;
                return value;
            }

            public bool ReadBool() => _data[_pos++] != 0;

            public string ReadString()
            {
                var len = (int)ReadU32();
                var value = Encoding.UTF8.GetString(_data.Slice(_pos, len));
                _pos += len;
                return value;
            }
        }
    }

    readonly record struct BenchResult(
        string Name,
        double JsonSerMs,
        double AsonSerMs,
        double BinSerMs,
        double JsonDeMs,
        double AsonDeMs,
        double BinDeMs,
        int JsonBytes,
        int AsonBytes,
        int BinBytes)
    {
        public void Print()
        {
            Console.WriteLine($"  {Name}");
            Console.WriteLine(
                $"    Serialize:   JSON {JsonSerMs:F2}ms/{JsonBytes}B | ASON {AsonSerMs:F2}ms({Program.FormatRatio(JsonSerMs, AsonSerMs)})/{AsonBytes}B({Program.FormatPercent(AsonBytes, JsonBytes)}) | BIN {BinSerMs:F2}ms({Program.FormatRatio(JsonSerMs, BinSerMs)})/{BinBytes}B({Program.FormatPercent(BinBytes, JsonBytes)})");
            Console.WriteLine(
                $"    Deserialize: JSON {JsonDeMs,8:F2}ms | ASON {AsonDeMs,8:F2}ms({Program.FormatRatio(JsonDeMs, AsonDeMs)}) | BIN {BinDeMs,8:F2}ms({Program.FormatRatio(JsonDeMs, BinDeMs)})");
        }
    }
}
