using Ason;
using A = Ason.Ason;

Console.WriteLine("=== ASON C# Complex Examples ===\n");

// 1. Nested struct
Console.WriteLine("1. Nested struct:");
var emp = Decoder.DecodeWith("{id,name,dept@{title},active}:(1,Alice,(Manager),true)", CEmployee.FromFields);
Console.WriteLine($"   {emp}\n");

// 2. Vec with nested structs
Console.WriteLine("2. Vec with nested structs:");
var input = "[{id@int,name@str,dept@{title@str},active@bool}]:\n  (1, Alice, (Manager), true),\n  (2, Bob, (Engineer), false),\n  (3, \"Carol Smith\", (Director), true)";
var employees = Decoder.DecodeListWith(input, CEmployee.FromFields);
foreach (var e in employees) Console.WriteLine($"   {e}");

// 3. Nested struct roundtrip
Console.WriteLine("\n3. Nested struct roundtrip:");
var nested = new CNested("Alice", new CAddress("NYC", 10001));
var s = A.encode(nested);
Console.WriteLine($"   serialized: {s}");
var n2 = Decoder.DecodeWith(s, CNested.FromFields);
Console.WriteLine("   ✓ roundtrip OK");

// 4. Escaped strings
Console.WriteLine("\n4. Escaped strings:");
var note = new CNote("say \"hi\", then (wave)\nnewline");
s = A.encode(note);
Console.WriteLine($"   serialized: {s}");
var note2 = Decoder.DecodeWith(s, CNote.FromFields);
Console.WriteLine("   ✓ escape roundtrip OK");

// 5. Float fields
Console.WriteLine("\n5. Float fields:");
var m = new CMeasurement(2, 95.0, "score");
s = A.encode(m);
Console.WriteLine($"   serialized: {s}");
var m2 = Decoder.DecodeWith(s, CMeasurement.FromFields);
Console.WriteLine("   ✓ float roundtrip OK");

// 6. Negative numbers
Console.WriteLine("\n6. Negative numbers:");
var n = new CNums(-42, -3.15, -9223372036854775806);
s = A.encode(n);
Console.WriteLine($"   serialized: {s}");
var n3 = Decoder.DecodeWith(s, CNums.FromFields);
Console.WriteLine("   ✓ negative roundtrip OK");

// 7. 5-level deep struct
Console.WriteLine("\n7. Five-level nesting (Company>Division>Team>Project>Task):");
var company = new CCompany("MegaCorp", 2000, 500.5, true,
    [new CDivision("Engineering", "SF", 200,
        [new CTeam("Backend", "Alice", 12,
            [new CProject("API v3", 250.0, true,
                [new CTask(1, "Design", 1, true, 40.0),
                 new CTask(2, "Implement", 1, false, 120.0),
                 new CTask(3, "Test", 2, false, 30.0)])])])],
    new List<string> { "tech", "public" });
s = A.encode(company);
Console.WriteLine($"   serialized ({s.Length} bytes)");

var bin = A.encodeBinary(company);
// Build JSON manually (System.Text.Json can't serialize ReadOnlySpan interface props)
var jsonStr = $"{{\"name\":\"MegaCorp\",\"founded\":2000,\"revenue_m\":500.5,\"public\":true,\"divisions\":[{{\"name\":\"Engineering\",\"location\":\"SF\",\"headcount\":200,\"teams\":[{{\"name\":\"Backend\",\"lead\":\"Alice\",\"size\":12,\"projects\":[{{\"name\":\"API v3\",\"budget\":250,\"active\":true,\"tasks\":[{{\"id\":1,\"title\":\"Design\",\"priority\":1,\"done\":true,\"hours\":40}},{{\"id\":2,\"title\":\"Implement\",\"priority\":1,\"done\":false,\"hours\":120}},{{\"id\":3,\"title\":\"Test\",\"priority\":2,\"done\":false,\"hours\":30}}]}}]}}]}}],\"tags\":[\"tech\",\"public\"]}}";
Console.WriteLine($"   ASON text: {s.Length} B | ASON bin: {bin.Length} B | JSON: {jsonStr.Length} B");
Console.WriteLine($"   TEXT vs JSON: {(1.0 - (double)s.Length / jsonStr.Length) * 100:F0}% smaller");
Console.WriteLine($"   BIN vs JSON:  {(1.0 - (double)bin.Length / jsonStr.Length) * 100:F0}% smaller");

// 8. Pretty format
Console.WriteLine("\n8. Pretty format:");
var pretty = A.encodePrettyTyped(company);
foreach (var line in pretty.Split('\n')) Console.WriteLine($"   {line}");

Console.WriteLine("\n=== All complex examples passed! ===");

// ===========================================================================
// Data types
// ===========================================================================
record CDept(string Title) : IAsonSchema
{
    static readonly string[] _n = ["title"];
    static readonly string?[] _t = ["str"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Title];
    public static CDept FromFields(Dictionary<string, object?> m) => new((string)m["title"]!);
    public override string ToString() => $"Department{{title={Title}}}";
}

record CEmployee(long Id, string Name, CDept Dept, bool Active) : IAsonSchema
{
    static readonly string[] _n = ["id", "name", "dept", "active"];
    static readonly string?[] _t = ["int", "str", null, "bool"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Id, Name, Dept, Active];
    public static CEmployee FromFields(Dictionary<string, object?> m)
    {
        var d = m["dept"];
        CDept dept;
        if (d is List<object?> list) dept = new CDept(list[0]?.ToString() ?? "");
        else dept = new CDept(d?.ToString() ?? "");
        return new CEmployee(Convert.ToInt64(m["id"]), (string)m["name"]!, dept, Convert.ToBoolean(m["active"]));
    }
    public override string ToString() => $"Employee{{id={Id}, name={Name}, dept={Dept}, active={Active}}}";
}

record CAddress(string City, long Zip) : IAsonSchema
{
    static readonly string[] _n = ["city", "zip"];
    static readonly string?[] _t = ["str", "int"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [City, Zip];
    public static CAddress FromFields(Dictionary<string, object?> m) =>
        new((string)m["city"]!, Convert.ToInt64(m["zip"]));
}

record CNested(string Name, CAddress Addr) : IAsonSchema
{
    static readonly string[] _n = ["name", "addr"];
    static readonly string?[] _t = ["str", null];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Name, Addr];
    public static CNested FromFields(Dictionary<string, object?> m)
    {
        var a = m["addr"];
        CAddress addr;
        if (a is List<object?> list) addr = new CAddress((string)list[0]!, Convert.ToInt64(list[1]));
        else addr = new CAddress("", 0);
        return new CNested((string)m["name"]!, addr);
    }
}

record CNote(string Text) : IAsonSchema
{
    static readonly string[] _n = ["text"];
    static readonly string?[] _t = ["str"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Text];
    public static CNote FromFields(Dictionary<string, object?> m) => new((string)m["text"]!);
}

record CMeasurement(long Id, double Value, string Label) : IAsonSchema
{
    static readonly string[] _n = ["id", "value", "label"];
    static readonly string?[] _t = ["int", "float", "str"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Id, Value, Label];
    public static CMeasurement FromFields(Dictionary<string, object?> m) =>
        new(Convert.ToInt64(m["id"]), Convert.ToDouble(m["value"]), (string)m["label"]!);
}

record CNums(long A, double B, long C) : IAsonSchema
{
    static readonly string[] _n = ["a", "b", "c"];
    static readonly string?[] _t = ["int", "float", "int"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [A, B, C];
    public static CNums FromFields(Dictionary<string, object?> m) =>
        new(Convert.ToInt64(m["a"]), Convert.ToDouble(m["b"]), Convert.ToInt64(m["c"]));
}

record CTask(long Id, string Title, long Priority, bool Done, double Hours) : IAsonSchema
{
    static readonly string[] _n = ["id", "title", "priority", "done", "hours"];
    static readonly string?[] _t = ["int", "str", "int", "bool", "float"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Id, Title, Priority, Done, Hours];
}

record CProject(string Name, double Budget, bool Active, List<CTask> Tasks) : IAsonSchema
{
    static readonly string[] _n = ["name", "budget", "active", "tasks"];
    static readonly string?[] _t = ["str", "float", "bool", null];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Name, Budget, Active, Tasks];
}

record CTeam(string Name, string Lead, long Size, List<CProject> Projects) : IAsonSchema
{
    static readonly string[] _n = ["name", "lead", "size", "projects"];
    static readonly string?[] _t = ["str", "str", "int", null];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Name, Lead, Size, Projects];
}

record CDivision(string Name, string Location, long Headcount, List<CTeam> Teams) : IAsonSchema
{
    static readonly string[] _n = ["name", "location", "headcount", "teams"];
    static readonly string?[] _t = ["str", "str", "int", null];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Name, Location, Headcount, Teams];
}

record CCompany(string Name, long Founded, double RevenueM, bool Public, List<CDivision> Divisions, List<string> Tags) : IAsonSchema
{
    static readonly string[] _n = ["name", "founded", "revenue_m", "public", "divisions", "tags"];
    static readonly string?[] _t = ["str", "int", "float", "bool", null, null];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Name, Founded, RevenueM, Public, Divisions, Tags];
}
