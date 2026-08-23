using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Asun;

/// <summary>
/// High-performance ASUN text decoder with schema caching.
/// </summary>
public static class Decoder
{
    // Cache parsed schema field names to avoid re-parsing identical schema headers.
    // Keyed by the exact schema header substring (not a hash) so distinct headers
    // that happen to collide cannot poison one another's cached fields (P0-1).
    private const int MaxCachedSchemas = 512;
    private static readonly ConcurrentDictionary<string, string[]> _schemaCache = new();

    // Bound the cache so an attacker feeding endless distinct schemas cannot grow
    // it without limit (P1-6): once full, drop everything and start fresh.
    internal static void CacheSchema(string key, string[] fields)
    {
        if (_schemaCache.Count >= MaxCachedSchemas)
            _schemaCache.Clear();
        _schemaCache.TryAdd(key, fields);
    }

    /// <summary>Decode ASUN text into a field bag (Dictionary&lt;string, object?&gt;).</summary>
    public static Dictionary<string, object?> Decode(ReadOnlySpan<char> input)
    {
        var d = new AsunDecoder(input, _schemaCache);
        d.SkipWs();
        var result = d.ParseSingleStruct();
        d.SkipWs();
        if (d.Pos < d.Len)
        {
            for (int i = d.Pos; i < d.Len; i++)
            {
                char c = input[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    throw AsunException.TrailingCharacters;
            }
        }
        return result;
    }

    /// <summary>Decode ASUN text into a typed object using a factory.</summary>
    public static T DecodeWith<T>(ReadOnlySpan<char> input, Func<Dictionary<string, object?>, T> factory)
    {
        return factory(Decode(input));
    }

    /// <summary>Decode ASUN text into a list of field bags.</summary>
    public static List<Dictionary<string, object?>> DecodeList(ReadOnlySpan<char> input)
    {
        var d = new AsunDecoder(input, _schemaCache);
        d.SkipWs();
        return d.ParseVecStruct();
    }

    /// <summary>Decode ASUN text into a list of typed objects.</summary>
    public static List<T> DecodeListWith<T>(ReadOnlySpan<char> input, Func<Dictionary<string, object?>, T> factory)
    {
        var d = new AsunDecoder(input, _schemaCache);
        d.SkipWs();
        return d.ParseVecStructWith(factory);
    }
}

/// <summary>Internal decoder state — ref struct for zero-alloc stack usage.</summary>
internal ref struct AsunDecoder
{
    // Maximum structural nesting depth. Bounds recursion so deeply nested,
    // untrusted payloads raise an error instead of overflowing the CLR stack (P0-2).
    private const int MaxDepth = 128;

    private readonly ReadOnlySpan<char> _input;
    private readonly ConcurrentDictionary<string, string[]>? _schemaCache;
    internal readonly int Len;
    internal int Pos;
    private int _depth;

    public AsunDecoder(ReadOnlySpan<char> input, ConcurrentDictionary<string, string[]>? schemaCache = null)
    {
        _input = input;
        _schemaCache = schemaCache;
        Len = input.Length;
        Pos = 0;
        _depth = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnterDepth()
    {
        if (++_depth > MaxDepth) throw AsunException.MaxDepthExceeded;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitDepth() => _depth--;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => Pos < Len ? _input[Pos] : '\0';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Next()
    {
        if (Pos >= Len) throw AsunException.Eof;
        return _input[Pos++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SkipWs()
    {
        while (Pos < Len)
        {
            char c = _input[Pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++;
            else break;
        }
    }

    internal void SkipWsAndComments()
    {
        for (;;)
        {
            SkipWs();
            if (Pos + 1 < Len && _input[Pos] == '/' && _input[Pos + 1] == '*')
            {
                Pos += 2;
                while (Pos + 1 < Len)
                {
                    if (_input[Pos] == '*' && _input[Pos + 1] == '/') { Pos += 2; break; }
                    Pos++;
                }
            }
            else break;
        }
    }

    // Schema parsing with caching
    internal string[] ParseSchema()
    {
        EnterDepth();
        try { return ParseSchemaInner(); }
        finally { ExitDepth(); }
    }

    private string[] ParseSchemaInner()
    {
        int schemaStart = Pos;
        if (Next() != '{') throw AsunException.ExpectedOpenBrace;

        // Try cache lookup: find end of schema header first. The scan must be
        // quote/escape-aware so a '}' inside a quoted field name does not end the
        // header early and let two different schemas share a cache slot (P0-1).
        int braceDepth = 1;
        int scanPos = Pos;
        bool inString = false;
        while (scanPos < Len && braceDepth > 0)
        {
            char c = _input[scanPos];
            if (inString)
            {
                if (c == '\\') scanPos++; // skip escaped char
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            scanPos++;
        }
        // scanPos now points right after the closing '}'
        int schemaEnd = scanPos;
        // Key on the exact header text; equality on the key is the poison check.
        string key = _input[schemaStart..schemaEnd].ToString();

        if (_schemaCache != null && _schemaCache.TryGetValue(key, out var cached))
        {
            // Skip past the schema we already parsed
            Pos = schemaEnd;
            return cached;
        }

        // Parse schema fields normally
        Pos = schemaStart + 1; // back to after '{'
        var fields = new List<string>(8);
        for (;;)
        {
            SkipWs();
            if (Peek() == '}') { Pos++; break; }
            if (fields.Count > 0) { if (Next() != ',') throw AsunException.ExpectedComma; SkipWs(); }
            string name;
            if (Peek() == '"')
            {
                name = ParseQuotedString();
            }
            else
            {
                int start = Pos;
                int idx = SimdHelper.IndexOfSchemaDelimiter(_input[Pos..]);
                if (idx >= 0) Pos += idx; else Pos = Len;
                name = _input[start..Pos].ToString();
            }
            SkipWs();

            // Validate and skip optional type annotation / structural marker
            if (Pos < Len && _input[Pos] == '@')
            {
                Pos++;
                SkipWs();
                ValidateSchemaAnnotation();
            }
            fields.Add(name);
        }
        var result = fields.ToArray();
        if (_schemaCache != null) Decoder.CacheSchema(key, result);
        return result;
    }

    private void ValidateSchemaAnnotation()
    {
        if (Pos >= Len) throw new AsunException("expected schema type after '@'");
        char tc = _input[Pos];
        if (tc == '{')
        {
            _ = ParseSchema();
            return;
        }
        if (tc == '[')
        {
            Pos++;
            SkipWs();
            if (Pos < Len && _input[Pos] == ']')
            {
                Pos++;
                return;
            }
            if (Pos < Len && _input[Pos] == '{')
            {
                _ = ParseSchema();
            }
            else
            {
                ValidateSchemaScalarType();
            }
            SkipWs();
            if (Pos >= Len || _input[Pos] != ']') throw new AsunException("expected ']' in array type annotation");
            Pos++;
            return;
        }
        ValidateSchemaScalarType();
    }

    private void ValidateSchemaScalarType()
    {
        int start = Pos;
        while (Pos < Len)
        {
            char c = _input[Pos];
            if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t') break;
            Pos++;
        }
        if (start == Pos) throw new AsunException("expected schema type after '@'");
        var token = _input[start..Pos].ToString();
        if (token.EndsWith("?")) token = token[..^1];
        if (token is "int" or "str" or "float" or "bool") return;
        throw new AsunException($"unsupported schema type '{token}'; use int, str, float, or bool");
    }

    private void SkipBalanced(char open, char close)
    {
        int depth = 0;
        while (Pos < Len)
        {
            char c = _input[Pos++];
            if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) return; }
        }
        throw AsunException.Eof;
    }

    // Struct parsing
    internal Dictionary<string, object?> ParseSingleStruct()
    {
        EnterDepth();
        try { return ParseSingleStructInner(); }
        finally { ExitDepth(); }
    }

    private Dictionary<string, object?> ParseSingleStructInner()
    {
        SkipWsAndComments();
        if (Pos < Len && _input[Pos] == '[' && Pos + 1 < Len && _input[Pos + 1] == '{')
        {
            throw new AsunException("expected struct, got vec. Use DecodeList instead.");
        }
        var fields = ParseSchema();
        SkipWsAndComments();
        if (Next() != ':') throw AsunException.ExpectedColon;
        SkipWsAndComments();
        return ParseTupleAsMap(fields);
    }

    internal List<Dictionary<string, object?>> ParseVecStruct()
    {
        Pos++; // skip [
        var fields = ParseSchema();
        SkipWs();
        if (Next() != ']') throw AsunException.ExpectedCloseBracket;
        SkipWs();
        if (Next() != ':') throw AsunException.ExpectedColon;

        var result = new List<Dictionary<string, object?>>();
        for (;;)
        {
            SkipWs();
            if (Pos >= Len) break;
            char c = _input[Pos];
            if (c == ',') { Pos++; SkipWs(); if (Pos >= Len || _input[Pos] != '(') break; }
            if (_input[Pos] != '(') break;
            result.Add(ParseTupleAsMap(fields));
        }
        return result;
    }

    /// <summary>Optimized: parse vec directly into typed list, reusing one Dictionary across all rows.</summary>
    internal List<T> ParseVecStructWith<T>(Func<Dictionary<string, object?>, T> factory)
    {
        Pos++; // skip [
        var fields = ParseSchema();
        SkipWs();
        if (Next() != ']') throw AsunException.ExpectedCloseBracket;
        SkipWs();
        if (Next() != ':') throw AsunException.ExpectedColon;

        var result = new List<T>();
        // Reuse a single Dictionary across all rows to reduce allocation
        var map = new Dictionary<string, object?>(fields.Length);

        for (;;)
        {
            SkipWs();
            if (Pos >= Len) break;
            char c = _input[Pos];
            if (c == ',') { Pos++; SkipWs(); if (Pos >= Len || _input[Pos] != '(') break; }
            if (_input[Pos] != '(') break;
            ParseTupleIntoMap(fields, map);
            result.Add(factory(map));
            map.Clear();
        }
        return result;
    }

    /// <summary>Parse a tuple directly into an existing dictionary (avoids allocation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ParseTupleIntoMap(string[] fields, Dictionary<string, object?> map)
    {
        Pos++; // skip (
        int fieldCount = fields.Length;
        for (int i = 0; i < fieldCount; i++)
        {
            SkipWs();
            char c = _input[Pos];
            if (c == ')') break;
            if (i > 0)
            {
                if (c == ',')
                {
                    Pos++;
                    SkipWs();
                    if (_input[Pos] == ')') { map[fields[i]] = null; continue; }
                }
                else break;
            }
            map[fields[i]] = ParseValueFast();
        }
        // Skip remaining fields
        SkipRemainingTuple();
        SkipWs();
        if (Pos < Len && _input[Pos] == ')') Pos++;
    }

    private Dictionary<string, object?> ParseTupleAsMap(string[] fields)
    {
        var map = new Dictionary<string, object?>(fields.Length);
        Pos++; // skip (
        int fieldCount = fields.Length;
        for (int i = 0; i < fieldCount; i++)
        {
            SkipWs();
            char c = _input[Pos];
            if (c == ')') break;
            if (i > 0)
            {
                if (c == ',')
                {
                    Pos++;
                    SkipWs();
                    if (_input[Pos] == ')') { map[fields[i]] = null; continue; }
                }
                else break;
            }
            map[fields[i]] = ParseValueFast();
        }
        SkipRemainingTuple();
        SkipWs();
        if (Pos < Len && _input[Pos] == ')') Pos++;
        return map;
    }

    private void SkipRemainingTuple()
    {
        SkipWs();
        while (Pos < Len && _input[Pos] != ')')
        {
            if (_input[Pos] == ',')
            {
                Pos++;
                SkipWs();
                if (Pos < Len && _input[Pos] == ')') break;
            }
            if (Pos < Len && _input[Pos] != ')') { SkipValue(); SkipWs(); }
        }
    }

    private void SkipValue()
    {
        if (Pos >= Len) return;
        char c = _input[Pos];
        switch (c)
        {
            case '(': SkipBalanced('(', ')'); break;
            case '[': SkipBalanced('[', ']'); break;
            case '<': throw AsunException.UnsupportedMap;
            case '"':
                Pos++;
                while (Pos < Len)
                {
                    char ch = _input[Pos];
                    if (ch == '\\') { Pos += 2; }
                    else if (ch == '"') { Pos++; return; }
                    else Pos++;
                }
                throw AsunException.UnclosedString;
            default:
                while (Pos < Len && _input[Pos] != ',' && _input[Pos] != ')' && _input[Pos] != ']') Pos++;
                break;
        }
    }

    // Fast value parsing with inlined common paths
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ParseValueFast()
    {
        if (Pos >= Len) return null;
        char c = _input[Pos];
        if (c == ',' || c == ')' || c == ']') return null;

        // Fast path: number (very common in ASUN data)
        if ((c >= '0' && c <= '9') || c == '-') return ParseNumber();

        // Fast path: quoted string
        if (c == '"') return ParseQuotedString();

        // Fast path: bool
        if (c == 't' && Pos + 4 <= Len && _input[Pos + 1] == 'r' && _input[Pos + 2] == 'u' && _input[Pos + 3] == 'e')
        {
            if (Pos + 4 >= Len || IsDelimiter(_input[Pos + 4])) { Pos += 4; return true; }
        }
        if (c == 'f' && Pos + 5 <= Len && _input[Pos + 1] == 'a' && _input[Pos + 2] == 'l' && _input[Pos + 3] == 's' && _input[Pos + 4] == 'e')
        {
            if (Pos + 5 >= Len || IsDelimiter(_input[Pos + 5])) { Pos += 5; return false; }
        }

        if (c == '(') return ParseTupleValue();
        if (c == '[') return ParseArray();
        if (c == '<') throw AsunException.UnsupportedMap;
        if (c == '{') return ParseSingleStruct();
        return ParsePlainValue();
    }

    // Legacy entry point (used by tests/generic path)
    internal object? ParseAnyValue() => ParseValueFast();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDelimiter(char c) =>
        c == ',' || c == ')' || c == ']' || c == ' ' || c == '\t' || c == '\n' || c == '\r';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object ParseNumber()
    {
        int start = Pos;
        bool negative = false;
        if (_input[Pos] == '-') { negative = true; Pos++; }
        // Accumulate as a negative magnitude so the full 64-bit range, including
        // long.MinValue, is representable and overflow is detected (P1-5).
        long intVal = 0;
        int digits = 0;
        while (Pos < Len)
        {
            int d = _input[Pos] - '0';
            if ((uint)d > 9) break;
            if (intVal < (long.MinValue + d) / 10) throw AsunException.IntegerOutOfRange;
            intVal = intVal * 10 - d;
            Pos++;
            digits++;
        }
        if (digits == 0) throw AsunException.InvalidNumber;
        if (Pos < Len && _input[Pos] == '.')
        {
            Pos = start;
            return ParseFloat();
        }
        if (Pos < Len && (_input[Pos] == 'e' || _input[Pos] == 'E'))
        {
            Pos = start;
            return ParseFloat();
        }
        if (negative) return intVal;
        if (intVal == long.MinValue) throw AsunException.IntegerOutOfRange;
        return -intVal;
    }

    private double ParseFloat()
    {
        int start = Pos;
        if (Pos < Len && _input[Pos] == '-') Pos++;
        while (Pos < Len && _input[Pos] >= '0' && _input[Pos] <= '9') Pos++;
        if (Pos < Len && _input[Pos] == '.')
        {
            Pos++;
            while (Pos < Len && _input[Pos] >= '0' && _input[Pos] <= '9') Pos++;
        }
        if (Pos < Len && (_input[Pos] == 'e' || _input[Pos] == 'E'))
        {
            Pos++;
            if (Pos < Len && (_input[Pos] == '+' || _input[Pos] == '-')) Pos++;
            while (Pos < Len && _input[Pos] >= '0' && _input[Pos] <= '9') Pos++;
        }
        return double.Parse(_input[start..Pos], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private string ParseQuotedString()
    {
        Pos++; // skip "
        int start = Pos;

        // SIMD fast scan for " or backslash 
        int idx = SimdHelper.IndexOfQuoteOrBackslash(_input[Pos..]);
        if (idx >= 0 && _input[Pos + idx] == '"')
        {
            // No escapes — zero-copy substring
            string result = _input[start..(Pos + idx)].ToString();
            Pos += idx + 1;
            return result;
        }

        // Slow path with escapes
        var buf = new DefaultInterpolatedStringHandler(0, 0);
        int scan = idx >= 0 ? Pos + idx : Len;
        if (scan > start) buf.AppendFormatted(_input[start..scan]);
        Pos = scan;

        while (Pos < Len)
        {
            char ch = _input[Pos];
            if (ch == '"') { Pos++; return buf.ToStringAndClear(); }
            if (ch == '\\')
            {
                Pos++;
                if (Pos >= Len) throw AsunException.UnclosedString;
                char esc = _input[Pos++];
                switch (esc)
                {
                    case '"': buf.AppendLiteral("\""); break;
                    case '\\': buf.AppendLiteral("\\"); break;
                    case 'n': buf.AppendLiteral("\n"); break;
                    case 'r': buf.AppendLiteral("\r"); break;
                    case 't': buf.AppendLiteral("\t"); break;
                    case ',': buf.AppendLiteral(","); break;
                    case '(': buf.AppendLiteral("("); break;
                    case ')': buf.AppendLiteral(")"); break;
                    case '[': buf.AppendLiteral("["); break;
                    case ']': buf.AppendLiteral("]"); break;
                    case 'u':
                        AppendUnicodeEscape(ref buf);
                        break;
                    default: throw new AsunException($"invalid escape: \\{esc}");
                }
            }
            else { buf.AppendFormatted(ch); Pos++; }
        }
        throw AsunException.UnclosedString;
    }

    // Read exactly four hex digits at absolute offset `at`. Rejects short input
    // and any non-hex character (int.Parse would otherwise accept '+'/'-'/spaces).
    private int Hex4(int at)
    {
        if (at + 4 > Len) throw AsunException.InvalidUnicodeEscape;
        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            char c = _input[at + k];
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else throw AsunException.InvalidUnicodeEscape;
            v = (v << 4) | d;
        }
        return v;
    }

    // Handle a \uXXXX escape (Pos points just past the 'u'). Combines a valid
    // high+low surrogate pair into one code point and rejects lone/unpaired
    // surrogates and bad hex, rather than emitting a broken char (P2-7).
    private void AppendUnicodeEscape(ref DefaultInterpolatedStringHandler buf)
    {
        int hi = Hex4(Pos);
        Pos += 4;
        if (hi >= 0xD800 && hi <= 0xDBFF)
        {
            if (Pos + 6 > Len || _input[Pos] != '\\' || _input[Pos + 1] != 'u')
                throw AsunException.InvalidUnicodeEscape;
            int lo = Hex4(Pos + 2);
            if (lo < 0xDC00 || lo > 0xDFFF) throw AsunException.InvalidUnicodeEscape;
            Pos += 6;
            int cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
            buf.AppendFormatted(char.ConvertFromUtf32(cp));
            return;
        }
        if (hi >= 0xDC00 && hi <= 0xDFFF) throw AsunException.InvalidUnicodeEscape;
        buf.AppendFormatted((char)hi);
    }

    private string ParsePlainValue()
    {
        int start = Pos;
        while (Pos < Len)
        {
            char c = _input[Pos];
            if (c == ',' || c == ')' || c == ']') break;
            if (c == '\\') Pos += 2; else Pos++;
        }
        var raw = _input[start..Pos].Trim();
        if (raw.Contains('\\'))
        {
            var sb = new DefaultInterpolatedStringHandler(0, 0);
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '\\')
                {
                    i++;
                    if (i >= raw.Length) throw AsunException.Eof;
                    char e = raw[i++];
                    switch (e)
                    {
                        case ',': sb.AppendLiteral(","); break;
                        case '(': sb.AppendLiteral("("); break;
                        case ')': sb.AppendLiteral(")"); break;
                        case '[': sb.AppendLiteral("["); break;
                        case ']': sb.AppendLiteral("]"); break;
                        case '"': sb.AppendLiteral("\""); break;
                        case '\\': sb.AppendLiteral("\\"); break;
                        case 'n': sb.AppendLiteral("\n"); break;
                        case 't': sb.AppendLiteral("\t"); break;
                        default: throw new AsunException($"invalid escape: \\{e}");
                    }
                }
                else { sb.AppendFormatted(raw[i++]); }
            }
            return sb.ToStringAndClear();
        }
        return raw.ToString();
    }

    private List<object?> ParseArray()
    {
        EnterDepth();
        try { return ParseArrayInner(); }
        finally { ExitDepth(); }
    }

    private List<object?> ParseArrayInner()
    {
        Pos++; // skip [
        SkipWs();
        if (Pos < Len && _input[Pos] == ']') { Pos++; return []; }

        var items = new List<object?>();
        bool first = true;
        while (Pos < Len)
        {
            SkipWs();
            if (Peek() == ']') { Pos++; return items; }
            if (!first)
            {
                if (_input[Pos] == ',')
                {
                    Pos++;
                    SkipWs();
                    if (Pos < Len && _input[Pos] == ']') { Pos++; return items; }
                }
                else break;
            }
            first = false;
            items.Add(ParseValueFast());
        }
        SkipWs();
        if (Pos < Len && _input[Pos] == ']') Pos++;
        return items;
    }

    private List<object?> ParseTupleValue()
    {
        EnterDepth();
        try { return ParseTupleValueInner(); }
        finally { ExitDepth(); }
    }

    private List<object?> ParseTupleValueInner()
    {
        Pos++; // skip (
        var items = new List<object?>();
        bool first = true;
        while (Pos < Len)
        {
            SkipWs();
            if (Peek() == ')') { Pos++; break; }
            if (!first)
            {
                if (_input[Pos] == ',')
                {
                    Pos++;
                    SkipWs();
                    if (Peek() == ')') { Pos++; break; }
                }
                else break;
            }
            first = false;
            items.Add(ParseValueFast());
        }
        return items;
    }
}
