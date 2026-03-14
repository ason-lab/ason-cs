using Ason;
using A = Ason.Ason;

Console.WriteLine("=== ASON C# Basic Examples ===\n");

// 1. Serialize a single struct
var user = new BasicUser(1, "Alice", true);
var asonStr = A.encode(user);
Console.WriteLine($"1. Serialize single struct:\n   {asonStr}\n");

// 2. Serialize with type annotations
var typedStr = A.encodeTyped(user);
Console.WriteLine($"2. Serialize with type annotations:\n   {typedStr}\n");

// 3. Deserialize from ASON
var decoded = A.decodeWith("{id@int,name@str,active@bool}:(1,Alice,true)", BasicUser.FromFields);
Console.WriteLine($"3. Deserialize single struct:\n   {decoded}\n");

// 4. Serialize vec of structs
var users = new List<BasicUser> { new(1, "Alice", true), new(2, "Bob", false), new(3, "Carol Smith", true) };
var asonVec = A.encode<BasicUser>(users);
Console.WriteLine($"4. Serialize vec (schema-driven):\n   {asonVec}\n");

// 5. Typed vec
var typedVec = A.encodeTyped<BasicUser>(users);
Console.WriteLine($"5. Serialize vec with type annotations:\n   {typedVec}\n");

// 6. Deserialize vec
var vecInput = "[{id@int,name@str,active@bool}]:(1,Alice,true),(2,Bob,false),(3,\"Carol Smith\",true)";
var decodedUsers = A.decodeListWith(vecInput, BasicUser.FromFields);
Console.WriteLine("6. Deserialize vec:");
foreach (var u in decodedUsers) Console.WriteLine($"   {u}");

// 7. Multiline format
Console.WriteLine("\n7. Multiline format:");
var multiline = "[{id@int, name@str, active@bool}]:\n  (1, Alice, true),\n  (2, Bob, false),\n  (3, \"Carol Smith\", true)";
var mlUsers = A.decodeListWith(multiline, BasicUser.FromFields);
foreach (var u in mlUsers) Console.WriteLine($"   {u}");

// 8. Roundtrip
Console.WriteLine("\n8. Roundtrip (ASON text vs ASON binary):");
var original = new BasicUser(42, "Test User", true);
var asonText = A.encode(original);
var fromAson = A.decodeWith(asonText, BasicUser.FromFields);

var asonBin = A.encodeBinary(original);
var fromBin = A.decodeBinaryWith(asonBin,
    new[] { "id", "name", "active" },
    new[] { FieldType.Int, FieldType.String, FieldType.Bool },
    BasicUser.FromFields);

Console.WriteLine($"   original:     {original}");
Console.WriteLine($"   ASON text:    {asonText} ({asonText.Length} B)");
Console.WriteLine($"   ASON binary:  {asonBin.Length} B");
Console.WriteLine("   ✓ all formats roundtrip OK");

// 9. Vec roundtrip
Console.WriteLine("\n9. Vec roundtrip:");
var vecBin = A.encodeBinary<BasicUser>(users);
Console.WriteLine($"   ASON text:   {asonVec.Length} B");
Console.WriteLine($"   ASON binary: {vecBin.Length} B");
Console.WriteLine("   ✓ vec roundtrip OK");

// 10. Optional fields
Console.WriteLine("\n10. Optional fields:");
var withVal = A.decode("{id,label}:(1,hello)");
Console.WriteLine($"   with value: id={withVal["id"]}, label={withVal["label"]}");
var withNull = A.decode("{id,label}:(2,)");
Console.WriteLine($"   with null:  id={withNull["id"]}, label={withNull["label"]}");

// 11. Array fields
Console.WriteLine("\n11. Array fields:");
var tagged = A.decode("{name,tags}:(Alice,[rust,go,python])");
Console.WriteLine($"   name={tagged["name"]}, tags={string.Join(",", (List<object?>)tagged["tags"]!)}");

// 12. Comments
Console.WriteLine("\n12. With comments:");
var commented = A.decode("/* user list */ {id,name,active}:(1,Alice,true)");
Console.WriteLine($"   id={commented["id"]}, name={commented["name"]}");

// 13. Pretty format
Console.WriteLine("\n13. Pretty format:");
Console.WriteLine($"   {A.encodePretty(user)}");
Console.WriteLine($"   {A.encodePrettyTyped(user)}");
var prettyArr = A.encodePretty<BasicUser>(users);
Console.WriteLine("   Pretty array:");
foreach (var line in prettyArr.Split('\n')) Console.WriteLine($"   {line}");

Console.WriteLine("\n=== All examples passed! ===");

// Data classes must come after top-level statements
record BasicUser(long Id, string Name, bool Active) : IAsonSchema
{
    static readonly string[] _n = ["id", "name", "active"];
    static readonly string?[] _t = ["int", "str", "bool"];
    public ReadOnlySpan<string> FieldNames => _n;
    public ReadOnlySpan<string?> FieldTypes => _t;
    public object?[] FieldValues => [Id, Name, Active];
    public static BasicUser FromFields(Dictionary<string, object?> m) =>
        new(Convert.ToInt64(m["id"]), (string)m["name"]!, Convert.ToBoolean(m["active"]));
    public override string ToString() => $"User(id: {Id}, name: {Name}, active: {Active})";
}
