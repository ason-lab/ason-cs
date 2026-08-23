using Asun;
using Xunit;

namespace Asun.Tests;

/// <summary>
/// Regression tests for the hardening fixes: P0-1 (schema cache poisoning),
/// P0-2 (recursion depth), P1-4 (float round-trip / -0.0), P1-5 (integer
/// overflow), P2-7 (Unicode surrogate handling).
/// </summary>
public class SecurityFixesTests
{
    // P0-2: deeply nested untyped input must throw, not overflow the stack.
    [Fact]
    public void DeepNestingBounded()
    {
        var s = new string('[', 100_000) + new string(']', 100_000);
        var ex = Assert.Throws<AsunException>(() => AsunValueCodec.Decode(s));
        Assert.Contains("depth", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestingWithinLimitOk()
    {
        var s = new string('[', 100) + new string(']', 100);
        var v = AsunValueCodec.Decode(s);
        Assert.Equal(AsunValue.Kind.Array, v.Tag);
    }

    // P1-5: integer overflow in the tuple-value fast path must throw rather than
    // silently wrap around a 64-bit accumulator.
    [Fact]
    public void IntOverflowRejected()
    {
        // 2^63 overflows a signed 64-bit target.
        Assert.Throws<AsunException>(() => Decoder.Decode("{v}:(9223372036854775808)"));
        // -(2^63)-1 underflows.
        Assert.Throws<AsunException>(() => Decoder.Decode("{v}:(-9223372036854775809)"));
    }

    [Fact]
    public void IntegerBoundsOk()
    {
        Assert.Equal(long.MaxValue, Decoder.Decode("{v}:(9223372036854775807)")["v"]);
        Assert.Equal(long.MinValue, Decoder.Decode("{v}:(-9223372036854775808)")["v"]);
    }

    // P1-4: -0.0 keeps its sign; fractional doubles round-trip through encode/decode.
    [Fact]
    public void NegativeZeroPreserved()
    {
        Assert.Equal("-0.0", AsunValueCodec.Encode(AsunValue.Of(-0.0)));
    }

    [Fact]
    public void DoubleRoundTrip()
    {
        double[] vals = { 0.1, 0.2, 0.3, 8.61, 2.675, 9.95, 1.15, 123456789.123456789, -0.3 };
        foreach (var v in vals)
        {
            string enc = AsunValueCodec.Encode(AsunValue.Of(v));
            var back = AsunValueCodec.Decode(enc);
            // "R" formatting guarantees the exact same double parses back.
            Assert.Equal(v, back.DoubleValue);
        }
    }

    // P2-7: \uXXXX must combine surrogate pairs and reject lone/unpaired ones.
    [Fact]
    public void SurrogatePairsCombined()
    {
        var v = AsunValueCodec.Decode("\"\\uD83D\\uDE00\""); // 😀
        Assert.Equal("\U0001F600", v.StringValue);
    }

    [Fact]
    public void RawAstralCharOk()
    {
        var v = AsunValueCodec.Decode("\"\U0001F600\"");
        Assert.Equal("\U0001F600", v.StringValue);
    }

    [Fact]
    public void LoneSurrogatesRejected()
    {
        Assert.Throws<AsunException>(() => AsunValueCodec.Decode("\"\\uD800\""));
        Assert.Throws<AsunException>(() => AsunValueCodec.Decode("\"\\uDC00\""));
        Assert.Throws<AsunException>(() => AsunValueCodec.Decode("\"\\uD83Dabc\""));
        Assert.Throws<AsunException>(() => AsunValueCodec.Decode("\"\\uD800A\""));
        // Bad hex digits must be rejected (int.Parse accepted '+'/whitespace).
        Assert.Throws<AsunException>(() => AsunValueCodec.Decode("\"\\u00ZZ\""));
    }

    // P0-1: two distinct schema headers must not share a cached field list even
    // if a field name contains a brace-like character in a quoted name.
    [Fact]
    public void SchemaCacheNotPoisonedByQuotedBrace()
    {
        // A quoted field name containing '}' must not truncate the header scan.
        var a = Decoder.Decode("{\"a}b\",c}:(1,2)");
        Assert.True(a.ContainsKey("a}b"));
        Assert.True(a.ContainsKey("c"));

        // A different header parses to its own fields, not the cached ones above.
        var b = Decoder.Decode("{x,y}:(3,4)");
        Assert.True(b.ContainsKey("x"));
        Assert.True(b.ContainsKey("y"));
        Assert.False(b.ContainsKey("a}b"));
    }
}
