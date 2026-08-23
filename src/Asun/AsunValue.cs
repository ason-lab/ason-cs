using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Asun;

/// <summary>
/// Untyped ASUN value (null / bool / long / double / string / List&lt;AsunValue&gt;).
///
/// Used for cases where the schema is unknown at compile time, primarily for
/// conformance testing and schema-less consumers. The typed code paths in
/// <see cref="Encoder"/> / <see cref="Decoder"/> are not affected.
///
/// Performance notes:
///   * Value is a tagged class with no boxing for scalar payloads.
///   * Encoding goes through the same SIMD-accelerated quote checks
///     (<see cref="SimdHelper"/>) and number formatters as the typed encoder.
///   * Decoding does not allocate a Dictionary — only a List for arrays and a
///     string for the actual textual payload.
/// </summary>
public sealed class AsunValue : IEquatable<AsunValue>
{
    public enum Kind : byte { Null, Bool, Int, Double, String, Array }

    public Kind Tag { get; }
    public bool BoolValue { get; }
    public long IntValue { get; }
    public double DoubleValue { get; }
    public string StringValue { get; } = string.Empty;
    public IReadOnlyList<AsunValue> ArrayValue { get; } = Array.Empty<AsunValue>();

    private AsunValue(Kind tag) { Tag = tag; }
    private AsunValue(Kind tag, bool b) { Tag = tag; BoolValue = b; }
    private AsunValue(Kind tag, long i) { Tag = tag; IntValue = i; }
    private AsunValue(Kind tag, double d) { Tag = tag; DoubleValue = d; }
    private AsunValue(Kind tag, string s) { Tag = tag; StringValue = s; }
    private AsunValue(Kind tag, IReadOnlyList<AsunValue> a) { Tag = tag; ArrayValue = a; }

    public static readonly AsunValue Null = new(Kind.Null);
    public static AsunValue Of(bool b)   => new(Kind.Bool, b);
    public static AsunValue Of(long i)   => new(Kind.Int, i);
    public static AsunValue Of(double d) => new(Kind.Double, d);
    public static AsunValue Of(string s) => new(Kind.String, s ?? string.Empty);
    public static AsunValue Of(IReadOnlyList<AsunValue> a) => new(Kind.Array, a ?? Array.Empty<AsunValue>());

    // Numeric int/double cross-compare matches the conformance harness used
    // across other language ports.
    public bool Equals(AsunValue? other)
    {
        if (other is null) return false;
        if (Tag == Kind.Int && other.Tag == Kind.Double) return NumericEqual((double)IntValue, other.DoubleValue);
        if (Tag == Kind.Double && other.Tag == Kind.Int) return NumericEqual(DoubleValue, (double)other.IntValue);
        if (Tag != other.Tag) return false;
        return Tag switch
        {
            Kind.Null => true,
            Kind.Bool => BoolValue == other.BoolValue,
            Kind.Int => IntValue == other.IntValue,
            Kind.Double => NumericEqual(DoubleValue, other.DoubleValue),
            Kind.String => StringValue == other.StringValue,
            Kind.Array => ArrayEqual(ArrayValue, other.ArrayValue),
            _ => false,
        };
    }
    public override bool Equals(object? o) => Equals(o as AsunValue);
    public override int GetHashCode() => HashCode.Combine((byte)Tag, IntValue, DoubleValue, StringValue);

    private static bool ArrayEqual(IReadOnlyList<AsunValue> a, IReadOnlyList<AsunValue> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static bool NumericEqual(double a, double b)
    {
        if (a == b) return true;
        double diff = Math.Abs(a - b);
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        double tol = Math.Max(scale * 1e-12, 1e-12);
        return diff <= tol;
    }

    public string ToDiagnostic()
    {
        var sb = new StringBuilder();
        AppendDiagnostic(sb);
        return sb.ToString();
    }

    private void AppendDiagnostic(StringBuilder sb)
    {
        switch (Tag)
        {
            case Kind.Null: sb.Append("null"); break;
            case Kind.Bool: sb.Append(BoolValue ? "true" : "false"); break;
            case Kind.Int: sb.Append(IntValue.ToString(CultureInfo.InvariantCulture)); break;
            case Kind.Double: sb.Append(DoubleValue.ToString("R", CultureInfo.InvariantCulture)); break;
            case Kind.String:
                sb.Append('"');
                foreach (char c in StringValue)
                {
                    if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                }
                sb.Append('"');
                break;
            case Kind.Array:
                sb.Append('[');
                for (int i = 0; i < ArrayValue.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    ((AsunValue)ArrayValue[i]).AppendDiagnostic(sb);
                }
                sb.Append(']');
                break;
        }
    }
}

/// <summary>
/// Untyped ASUN encode / decode. Companion to <see cref="Encoder"/> /
/// <see cref="Decoder"/> for cases where the value shape is not a struct.
/// </summary>
public static class AsunValueCodec
{
    // Bounds recursion in the untyped decoder so deeply nested payloads raise an
    // error instead of overflowing the CLR stack (P0-2).
    private const int MaxDepth = 128;

    // ----- Encoder ---------------------------------------------------------

    public static string Encode(AsunValue value)
    {
        var w = new AsunWriter(64);
        try { EncodeInto(ref w, value); return w.ToString(); }
        finally { w.Dispose(); }
    }

    internal static void EncodeInto(ref AsunWriter w, AsunValue v)
    {
        switch (v.Tag)
        {
            case AsunValue.Kind.Null: return;
            case AsunValue.Kind.Bool: w.WriteBool(v.BoolValue); return;
            case AsunValue.Kind.Int: w.WriteInt(v.IntValue); return;
            case AsunValue.Kind.Double: w.WriteDouble(v.DoubleValue); return;
            case AsunValue.Kind.String: w.WriteString(v.StringValue); return;
            case AsunValue.Kind.Array:
                w.WriteChar('[');
                for (int i = 0; i < v.ArrayValue.Count; i++)
                {
                    if (i > 0) w.WriteChar(',');
                    EncodeInto(ref w, (AsunValue)v.ArrayValue[i]);
                }
                if (v.ArrayValue.Count > 0 && v.ArrayValue[^1].Tag == AsunValue.Kind.Null)
                    w.WriteChar(',');
                w.WriteChar(']');
                return;
        }
    }

    // ----- Decoder ---------------------------------------------------------

    public static AsunValue Decode(ReadOnlySpan<char> input)
    {
        int pos = 0;
        SkipWsStrict(input, ref pos);
        if (pos >= input.Length) return AsunValue.Null;

        AsunValue v;
        char c = input[pos];
        if (c == '(')
        {
            throw new AsunException("bare tuple is not a valid top-level value");
        }
        else if (c == '[')
        {
            v = ParseArray(input, ref pos, 0);
        }
        else if (c == '"')
        {
            v = AsunValue.Of(ParseQuoted(input, ref pos));
        }
        else
        {
            v = ParseTopPlain(input, ref pos);
        }

        SkipWsStrict(input, ref pos);
        if (pos < input.Length) throw AsunException.TrailingCharacters;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SkipWsStrict(ReadOnlySpan<char> s, ref int pos)
    {
        for (;;)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { pos++; continue; }
                break;
            }
            if (pos + 1 < s.Length && s[pos] == '/' && s[pos + 1] == '*')
            {
                pos += 2;
                bool closed = false;
                while (pos + 1 < s.Length)
                {
                    if (s[pos] == '*' && s[pos + 1] == '/') { pos += 2; closed = true; break; }
                    pos++;
                }
                if (!closed) throw new AsunException("unterminated comment");
                continue;
            }
            return;
        }
    }

    private static AsunValue ParseArray(ReadOnlySpan<char> s, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw AsunException.MaxDepthExceeded;
        pos++; // skip [
        var items = new List<AsunValue>();
        bool first = true;
        for (;;)
        {
            SkipWsStrict(s, ref pos);
            if (pos >= s.Length) throw new AsunException("unterminated array");
            if (s[pos] == ']') { pos++; break; }
            if (!first)
            {
                if (s[pos] != ',') throw AsunException.ExpectedComma;
                pos++;
                SkipWsStrict(s, ref pos);
                if (pos >= s.Length) throw new AsunException("unterminated array");
                if (s[pos] == ']') { pos++; break; }
            }
            first = false;
            // Sparse null: [,] / [1,,3]
            if (s[pos] == ',' || s[pos] == ']')
            {
                items.Add(AsunValue.Null);
                continue;
            }
            items.Add(ParseValueInner(s, ref pos, depth));
        }
        return AsunValue.Of(items);
    }

    private static AsunValue ParseValueInner(ReadOnlySpan<char> s, ref int pos, int depth)
    {
        SkipWsStrict(s, ref pos);
        if (pos >= s.Length) return AsunValue.Null;
        char c = s[pos];
        if (c == '[') return ParseArray(s, ref pos, depth + 1);
        if (c == '"') return AsunValue.Of(ParseQuoted(s, ref pos));
        if (c == '(')
        {
            pos++;
            SkipWsStrict(s, ref pos);
            if (pos < s.Length && s[pos] == ')') { pos++; return AsunValue.Null; }
            throw new AsunException("unexpected '(' in untyped value position");
        }

        int start = pos;
        while (pos < s.Length)
        {
            char b = s[pos];
            if (b == ',' || b == ']' || b == ')' || b == '}') break;
            if (b == '\\' && pos + 1 < s.Length) { pos += 2; continue; }
            pos++;
        }
        return ClassifyPlain(TrimAscii(s[start..pos]));
    }

    private static AsunValue ParseTopPlain(ReadOnlySpan<char> s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length)
        {
            char b = s[pos];
            if (b == ',' || b == ']' || b == ')' || b == '}') break;
            if (b == '\\' && pos + 1 < s.Length) { pos += 2; continue; }
            pos++;
        }
        return ClassifyPlain(TrimAscii(s[start..pos]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> TrimAscii(ReadOnlySpan<char> tok)
    {
        int i = 0, j = tok.Length;
        while (i < j) { char c = tok[i]; if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++; else break; }
        while (j > i) { char c = tok[j - 1]; if (c == ' ' || c == '\t' || c == '\n' || c == '\r') j--; else break; }
        return tok[i..j];
    }

    private static AsunValue ClassifyPlain(ReadOnlySpan<char> tok)
    {
        if (tok.Length == 0) return AsunValue.Of(string.Empty);
        if (tok.SequenceEqual("true"))  return AsunValue.Of(true);
        if (tok.SequenceEqual("false")) return AsunValue.Of(false);

        // Try integer (optional '-' followed by digits).
        int k = 0;
        bool neg = false;
        if (tok[0] == '-') { neg = true; k = 1; }
        if (k < tok.Length)
        {
            bool allDigits = true;
            for (int j = k; j < tok.Length; j++) { if (tok[j] < '0' || tok[j] > '9') { allDigits = false; break; } }
            if (allDigits)
            {
                ulong v = 0;
                ulong limit = neg ? (ulong)long.MaxValue + 1 : (ulong)long.MaxValue;
                bool overflow = false;
                for (int j = k; j < tok.Length; j++)
                {
                    int d = tok[j] - '0';
                    if (v > (limit - (ulong)d) / 10) { overflow = true; break; }
                    v = v * 10 + (ulong)d;
                }
                if (!overflow)
                {
                    long iv = neg ? (v == 0 ? 0L : -(long)(v - 1) - 1L) : (long)v;
                    return AsunValue.Of(iv);
                }
            }
        }

        // Try float: digits '.' digits, with optional signed exponent.
        if (LooksLikeFloat(tok))
        {
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                return AsunValue.Of(dv);
        }

        // Fallback: string with plain-token escape unwrap.
        return AsunValue.Of(UnescapePlain(tok));
    }

    private static bool LooksLikeFloat(ReadOnlySpan<char> s)
    {
        int i = 0;
        if (i < s.Length && s[i] == '-') i++;
        int beforeDot = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; beforeDot++; }
        if (beforeDot == 0) return false;

        bool hasDot = false;
        bool hasExp = false;
        if (i < s.Length && s[i] == '.')
        {
            hasDot = true;
            i++;
            int afterDot = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; afterDot++; }
            if (afterDot == 0) return false;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            hasExp = true;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            int expDigits = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; expDigits++; }
            if (expDigits == 0) return false;
        }
        return (hasDot || hasExp) && i == s.Length;
    }

    private static string UnescapePlain(ReadOnlySpan<char> tok)
    {
        // Fast path: no backslash.
        int bs = tok.IndexOf('\\');
        if (bs < 0) return tok.ToString();

        var sb = new StringBuilder(tok.Length);
        sb.Append(tok[..bs]);
        for (int j = bs; j < tok.Length; j++)
        {
            char c = tok[j];
            if (c == '\\' && j + 1 < tok.Length)
            {
                char esc = tok[j + 1];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); j++; continue;
                    case 'r': sb.Append('\r'); j++; continue;
                    case 't': sb.Append('\t'); j++; continue;
                    case 'b': sb.Append('\b'); j++; continue;
                    case 'f': sb.Append('\f'); j++; continue;
                    case '\\': sb.Append('\\'); j++; continue;
                    case '"': sb.Append('"'); j++; continue;
                    case ',': sb.Append(','); j++; continue;
                    case '(': sb.Append('('); j++; continue;
                    case ')': sb.Append(')'); j++; continue;
                    case '[': sb.Append('['); j++; continue;
                    case ']': sb.Append(']'); j++; continue;
                    case '{': sb.Append('{'); j++; continue;
                    case '}': sb.Append('}'); j++; continue;
                    case '@': sb.Append('@'); j++; continue;
                    case ':': sb.Append(':'); j++; continue;
                    case 'u':
                    {
                        // tok[j]=='\\', tok[j+1]=='u'; hex starts at j+2.
                        int p = j + 2;
                        AppendUnicodeEscape(tok, ref p, sb);
                        j = p - 1; // for-loop's j++ advances past the last consumed char
                        continue;
                    }
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Read exactly four hex digits at absolute offset `at` in `s`. Rejects short
    // input and non-hex chars (int.Parse would accept '+', spaces, etc.).
    private static int Hex4(ReadOnlySpan<char> s, int at)
    {
        if (at + 4 > s.Length) throw AsunException.InvalidUnicodeEscape;
        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            char c = s[at + k];
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else throw AsunException.InvalidUnicodeEscape;
            v = (v << 4) | d;
        }
        return v;
    }

    // Handle a \uXXXX escape (pos points just past the 'u'). Combines a valid
    // high+low surrogate pair into one code point; rejects lone/unpaired
    // surrogates and bad hex rather than emitting a broken char (P2-7).
    private static void AppendUnicodeEscape(ReadOnlySpan<char> s, ref int pos, StringBuilder sb)
    {
        int hi = Hex4(s, pos);
        pos += 4;
        if (hi >= 0xD800 && hi <= 0xDBFF)
        {
            if (pos + 6 > s.Length || s[pos] != '\\' || s[pos + 1] != 'u')
                throw AsunException.InvalidUnicodeEscape;
            int lo = Hex4(s, pos + 2);
            if (lo < 0xDC00 || lo > 0xDFFF) throw AsunException.InvalidUnicodeEscape;
            pos += 6;
            int cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
            sb.Append(char.ConvertFromUtf32(cp));
            return;
        }
        if (hi >= 0xDC00 && hi <= 0xDFFF) throw AsunException.InvalidUnicodeEscape;
        sb.Append((char)hi);
    }

    private static string ParseQuoted(ReadOnlySpan<char> s, ref int pos)
    {
        pos++; // skip "
        int start = pos;
        // Fast path: scan to closing " or backslash. If we hit " first, no escapes.
        int rel = SimdHelper.IndexOfQuoteOrBackslash(s[pos..]);
        if (rel < 0) throw AsunException.UnclosedString;
        if (s[pos + rel] == '"')
        {
            string fast = s.Slice(start, rel).ToString();
            pos += rel + 1;
            return fast;
        }

        var sb = new StringBuilder();
        if (rel > 0) sb.Append(s.Slice(start, rel));
        pos += rel;

        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '"') { pos++; return sb.ToString(); }
            if (c == '\\')
            {
                pos++;
                if (pos >= s.Length) throw AsunException.UnclosedString;
                char esc = s[pos++];
                switch (esc)
                {
                    case '"':  sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case 'b':  sb.Append('\b'); break;
                    case 'f':  sb.Append('\f'); break;
                    case ',':  sb.Append(','); break;
                    case '(':  sb.Append('('); break;
                    case ')':  sb.Append(')'); break;
                    case '[':  sb.Append('['); break;
                    case ']':  sb.Append(']'); break;
                    case '{':  sb.Append('{'); break;
                    case '}':  sb.Append('}'); break;
                    case '@':  sb.Append('@'); break;
                    case ':':  sb.Append(':'); break;
                    case 'u':
                        AppendUnicodeEscape(s, ref pos, sb);
                        break;
                    default: throw new AsunException($"invalid escape: \\{esc}");
                }
            }
            else { sb.Append(c); pos++; }
        }
        throw AsunException.UnclosedString;
    }
}
