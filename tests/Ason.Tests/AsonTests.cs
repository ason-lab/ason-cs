using Ason;
using Xunit;

namespace Ason.Tests;

// Test data classes
public record User(long Id, string Name, bool Active) : IAsonSchema
{
    private static readonly string[] _names = ["id", "name", "active"];
    private static readonly string?[] _types = ["int", "str", "bool"];
    public ReadOnlySpan<string> FieldNames => _names;
    public ReadOnlySpan<string?> FieldTypes => _types;
    public object?[] FieldValues => [Id, Name, Active];

    public static User FromMap(Dictionary<string, object?> m) => new(
        Convert.ToInt64(m["id"]), (string)m["name"]!, Convert.ToBoolean(m["active"]));
}

public record Dept(string Title) : IAsonSchema
{
    private static readonly string[] _names = ["title"];
    private static readonly string?[] _types = ["str"];
    public ReadOnlySpan<string> FieldNames => _names;
    public ReadOnlySpan<string?> FieldTypes => _types;
    public object?[] FieldValues => [Title];

    public static Dept FromMap(Dictionary<string, object?> m) => new((string)m["title"]!);
}

public record Employee(string Name, Dept Dept, bool Active) : IAsonSchema
{
    private static readonly string[] _names = ["name", "dept", "active"];
    private static readonly string?[] _types = ["str", null, "bool"];
    public ReadOnlySpan<string> FieldNames => _names;
    public ReadOnlySpan<string?> FieldTypes => _types;
    public object?[] FieldValues => [Name, Dept, Active];

    public static Employee FromMap(Dictionary<string, object?> m)
    {
        var d = m["dept"];
        Dept dept;
        if (d is Dictionary<string, object?> dm) dept = Dept.FromMap(dm);
        else if (d is List<object?> list) dept = new Dept(list[0]?.ToString() ?? "");
        else dept = new Dept(d?.ToString() ?? "");
        return new Employee((string)m["name"]!, dept, Convert.ToBoolean(m["active"]));
    }
}

public class EncodeTests
{
    [Fact]
    public void SingleStructUnannotated()
    {
        var u = new User(1, "Alice", true);
        Assert.Equal("{id,name,active}:(1,Alice,true)", Encoder.Encode(u));
    }

    [Fact]
    public void SingleStructTyped()
    {
        var u = new User(1, "Alice", true);
        Assert.Equal("{id:int,name:str,active:bool}:(1,Alice,true)", Encoder.EncodeTyped(u));
    }

    [Fact]
    public void VecOfStructs()
    {
        var users = new List<User> { new(1, "Alice", true), new(2, "Bob", false) };
        Assert.Equal("[{id,name,active}]:(1,Alice,true),(2,Bob,false)", Encoder.Encode<User>(users));
    }

    [Fact]
    public void VecTyped()
    {
        var users = new List<User> { new(1, "Alice", true), new(2, "Bob", false) };
        Assert.Equal("[{id:int,name:str,active:bool}]:(1,Alice,true),(2,Bob,false)", Encoder.EncodeTyped<User>(users));
    }

    [Fact]
    public void NestedStruct()
    {
        var e = new Employee("Alice", new Dept("Engineering"), true);
        Assert.Equal("{name,dept:{title},active}:(Alice,(Engineering),true)", Encoder.Encode(e));
    }

    [Fact]
    public void NestedStructTyped()
    {
        var e = new Employee("Alice", new Dept("Engineering"), true);
        Assert.Equal("{name:str,dept:{title:str},active:bool}:(Alice,(Engineering),true)", Encoder.EncodeTyped(e));
    }

    [Fact]
    public void EscapedString()
    {
        var u = new User(1, "hello, world", true);
        var s = Encoder.Encode(u);
        Assert.Contains("\"", s);
    }

    [Fact]
    public void NegativeNumbers()
    {
        var m = new NumStruct(-42, -3.15);
        var result = Encoder.Encode(new List<NumStruct> { m });
        Assert.Contains("-42", result);
        Assert.Contains("-3.15", result);
    }
}

public record NumStruct(long A, double B) : IAsonSchema
{
    private static readonly string[] _names = ["a", "b"];
    private static readonly string?[] _types = ["int", "float"];
    public ReadOnlySpan<string> FieldNames => _names;
    public ReadOnlySpan<string?> FieldTypes => _types;
    public object?[] FieldValues => [(object)A, (object)B];
}

public record MatrixPart(long Id, double Score)
{
    public static MatrixPart FromMap(Dictionary<string, object?> m) => new(
        Convert.ToInt64(m["id"]),
        Convert.ToDouble(m["score"]));
}

public record MatrixNoOverlap(long Foo, string? Bar)
{
    public static MatrixNoOverlap FromMap(Dictionary<string, object?> m) => new(
        m.TryGetValue("foo", out var foo) && foo is not null ? Convert.ToInt64(foo) : 0L,
        m.TryGetValue("bar", out var bar) ? bar as string : null);
}

public record MatrixNestedOptional(string Name, string? Nick)
{
    public static MatrixNestedOptional FromMap(Dictionary<string, object?> m) => new(
        (string)m["name"]!,
        m.TryGetValue("nick", out var nick) ? nick as string : null);
}

public record MatrixUserNestedOptional(long Id, MatrixNestedOptional Profile)
{
    public static MatrixUserNestedOptional FromMap(Dictionary<string, object?> m)
    {
        var p = m["profile"];
        MatrixNestedOptional profile;
        if (p is Dictionary<string, object?> pm)
        {
            profile = MatrixNestedOptional.FromMap(pm);
        }
        else if (p is List<object?> list)
        {
            profile = new MatrixNestedOptional(
                list.Count > 0 ? (list[0]?.ToString() ?? "") : "",
                list.Count > 1 ? list[1] as string : null);
        }
        else
        {
            profile = new MatrixNestedOptional("", null);
        }
        return new MatrixUserNestedOptional(Convert.ToInt64(m["id"]), profile);
    }
}

public class DecodeTests
{
    [Fact]
    public void SingleStruct()
    {
        var u = Decoder.DecodeWith("{id,name,active}:(1,Alice,true)", User.FromMap);
        Assert.Equal(1L, u.Id);
        Assert.Equal("Alice", u.Name);
        Assert.True(u.Active);
    }

    [Fact]
    public void TypedSchema()
    {
        var u = Decoder.DecodeWith("{id:int,name:str,active:bool}:(1,Alice,true)", User.FromMap);
        Assert.Equal(1L, u.Id);
        Assert.Equal("Alice", u.Name);
        Assert.True(u.Active);
    }

    [Fact]
    public void VecOfStructs()
    {
        var users = Decoder.DecodeListWith("[{id,name,active}]:(1,Alice,true),(2,Bob,false)", User.FromMap);
        Assert.Equal(2, users.Count);
        Assert.Equal("Alice", users[0].Name);
        Assert.Equal("Bob", users[1].Name);
    }

    [Fact]
    public void Multiline()
    {
        var users = Decoder.DecodeListWith(
            "[{id:int,name:str,active:bool}]:\n  (1, Alice, true),\n  (2, Bob, false)", User.FromMap);
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public void QuotedString()
    {
        var u = Decoder.DecodeWith("{id,name,active}:(1,\"Carol Smith\",true)", User.FromMap);
        Assert.Equal("Carol Smith", u.Name);
    }

    [Fact]
    public void OptionalFieldWithValue()
    {
        var m = Decoder.Decode("{id,label}:(1,hello)");
        Assert.Equal(1L, m["id"]);
        Assert.Equal("hello", m["label"]);
    }

    [Fact]
    public void OptionalFieldWithNull()
    {
        var m = Decoder.Decode("{id,label}:(2,)");
        Assert.Equal(2L, m["id"]);
        Assert.Null(m["label"]);
    }

    [Fact]
    public void ArrayField()
    {
        var m = Decoder.Decode("{name,tags}:(Alice,[rust,go])");
        var tags = (List<object?>)m["tags"]!;
        Assert.Equal(2, tags.Count);
        Assert.Equal("rust", tags[0]);
        Assert.Equal("go", tags[1]);
    }

    [Fact]
    public void NestedStruct()
    {
        var m = Decoder.Decode("{name,dept:{title}}:(Alice,(Manager))");
        Assert.Equal("Alice", m["name"]);
    }

    [Fact]
    public void FloatField()
    {
        var m = Decoder.Decode("{id,value}:(1,95.5)");
        Assert.Equal(95.5, m["value"]);
    }

    [Fact]
    public void NegativeNumber()
    {
        var m = Decoder.Decode("{a,b}:(-42,-3.15)");
        Assert.Equal(-42L, m["a"]);
        Assert.Equal(-3.15, m["b"]);
    }

    [Fact]
    public void CommentStripping()
    {
        var m = Decoder.Decode("/* users */ {id,name,active}:(1,Alice,true)");
        Assert.Equal(1L, m["id"]);
    }

    [Fact]
    public void TrailingComma()
    {
        var users = Decoder.DecodeListWith(
            "[{id,name,active}]:(1,Alice,true),(2,Bob,false),", User.FromMap);
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public void MatrixP1TypedPartialOverlap()
    {
        var dst = Decoder.DecodeWith(
            "{id:int,name:str,score:float,active:bool}:(42,Alice,9.5,true)",
            MatrixPart.FromMap);
        Assert.Equal(42L, dst.Id);
        Assert.Equal(9.5, dst.Score);
    }

    [Fact]
    public void MatrixP1UntypedPartialOverlap()
    {
        var dst = Decoder.DecodeWith(
            "{id,name,score,active}:(42,Alice,9.5,true)",
            MatrixPart.FromMap);
        Assert.Equal(42L, dst.Id);
        Assert.Equal(9.5, dst.Score);
    }

    [Fact]
    public void MatrixP2TypedNoOverlapDefaults()
    {
        var dst = Decoder.DecodeWith(
            "{id:int,name:str}:(42,Alice)",
            MatrixNoOverlap.FromMap);
        Assert.Equal(0L, dst.Foo);
        Assert.Null(dst.Bar);
    }

    [Fact]
    public void MatrixP2UntypedNoOverlapDefaults()
    {
        var dst = Decoder.DecodeWith(
            "{id,name}:(42,Alice)",
            MatrixNoOverlap.FromMap);
        Assert.Equal(0L, dst.Foo);
        Assert.Null(dst.Bar);
    }

    [Fact]
    public void MatrixN4TypedNestedOptionalSubset()
    {
        var dst = Decoder.DecodeListWith(
            "[{id:int,profile:{name:str,nick:str?,score:float?},active:bool}]:(1,(Alice,ally,9.5),true),(2,(Bob,,),false)",
            MatrixUserNestedOptional.FromMap);
        Assert.Equal(2, dst.Count);
        Assert.Equal("Alice", dst[0].Profile.Name);
        Assert.Equal("ally", dst[0].Profile.Nick);
        Assert.Equal("Bob", dst[1].Profile.Name);
        Assert.Null(dst[1].Profile.Nick);
    }

    [Fact]
    public void MatrixN4UntypedNestedOptionalSubset()
    {
        var dst = Decoder.DecodeListWith(
            "[{id,profile:{name,nick,score},active}]:(1,(Alice,ally,9.5),true),(2,(Bob,,),false)",
            MatrixUserNestedOptional.FromMap);
        Assert.Equal(2, dst.Count);
        Assert.Equal("ally", dst[0].Profile.Nick);
        Assert.Null(dst[1].Profile.Nick);
    }
}

public class RoundtripTests
{
    [Fact]
    public void SingleStruct()
    {
        var u = new User(42, "Bob", false);
        var s = Encoder.Encode(u);
        var u2 = Decoder.DecodeWith(s, User.FromMap);
        Assert.Equal(u, u2);
    }

    [Fact]
    public void TypedRoundtrip()
    {
        var u = new User(42, "Bob", false);
        var s = Encoder.EncodeTyped(u);
        var u2 = Decoder.DecodeWith(s, User.FromMap);
        Assert.Equal(u, u2);
    }

    [Fact]
    public void NestedStruct()
    {
        var e = new Employee("Alice", new Dept("Eng"), true);
        var s = Encoder.Encode(e);
        var e2 = Decoder.DecodeWith(s, Employee.FromMap);
        Assert.Equal(e, e2);
    }
}

public class BinaryTests
{
    [Fact]
    public void EncodeDecodeStruct()
    {
        var u = new User(1, "Alice", true);
        var bin = BinaryCodec.EncodeBinary(u);
        var u2 = BinaryCodec.DecodeBinaryWith(bin,
            new[] { "id", "name", "active" },
            new[] { FieldType.Int, FieldType.String, FieldType.Bool },
            User.FromMap);
        Assert.Equal(u, u2);
    }

    [Fact]
    public void EncodeDecodeVec()
    {
        var users = new List<User> { new(1, "Alice", true), new(2, "Bob", false) };
        var bin = BinaryCodec.EncodeBinary<User>(users);
        var users2 = BinaryCodec.DecodeBinaryListWith(bin,
            new[] { "id", "name", "active" },
            new[] { FieldType.Int, FieldType.String, FieldType.Bool },
            User.FromMap);
        Assert.Equal(2, users2.Count);
        Assert.Equal(users[0], users2[0]);
        Assert.Equal(users[1], users2[1]);
    }

    [Fact]
    public void BinarySmallerThanJson()
    {
        var u = new User(1, "Alice", true);
        var bin = BinaryCodec.EncodeBinary(u);
        var json = "{\"id\":1,\"name\":\"Alice\",\"active\":true}";
        Assert.True(bin.Length < json.Length);
    }
}

public class PrettyTests
{
    [Fact]
    public void SimpleStruct()
    {
        var u = new User(1, "Alice", true);
        Assert.Equal("{id, name, active}:(1, Alice, true)", PrettyPrinter.EncodePretty(u));
    }

    [Fact]
    public void TypedSimple()
    {
        var u = new User(1, "Alice", true);
        Assert.Equal("{id:int, name:str, active:bool}:(1, Alice, true)", PrettyPrinter.EncodePrettyTyped(u));
    }

    [Fact]
    public void ArrayHasNewlines()
    {
        var users = new List<User> { new(1, "Alice", true), new(2, "Bob", false) };
        var p = PrettyPrinter.EncodePretty(users);
        Assert.Contains("\n", p);
    }

    [Fact]
    public void PrettyRoundtrip()
    {
        var u = new User(1, "Alice", true);
        var p = PrettyPrinter.EncodePretty(u);
        var u2 = Decoder.DecodeWith(p, User.FromMap);
        Assert.Equal(u, u2);
    }
}
