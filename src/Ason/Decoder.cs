using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ason;

/// <summary>
/// High-performance ASON text decoder with schema caching.
/// </summary>
public static class Decoder
{
    // Cache parsed schema field names to avoid re-parsing identical schema headers
    private static readonly ConcurrentDictionary<int, string[]> _schemaCache = new();

    /// <summary>Decode ASON text into a field bag (Dictionary&lt;string, object?&gt;).</summary>
    public static Dictionary<string, object?> Decode(ReadOnlySpan<char> input)
    {
        var d = new AsonDecoder(input, _schemaCache);
        d.SkipWs();
        var result = d.ParseSingleStruct();
        d.SkipWs();
        if (d.Pos < d.Len)
        {
            for (int i = d.Pos; i < d.Len; i++)
            {
                char c = input[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    throw AsonException.TrailingCharacters;
            }
        }
        return result;
    }

    /// <summary>Decode ASON text into a typed object using a factory.</summary>
    public static T DecodeWith<T>(ReadOnlySpan<char> input, Func<Dictionary<string, object?>, T> factory)
    {
        return factory(Decode(input));
    }

    /// <summary>Decode ASON text into a list of field bags.</summary>
    public static List<Dictionary<string, object?>> DecodeList(ReadOnlySpan<char> input)
    {
        var d = new AsonDecoder(input, _schemaCache);
        d.SkipWs();
        return d.ParseVecStruct();
    }

    /// <summary>Decode ASON text into a list of typed objects.</summary>
    public static List<T> DecodeListWith<T>(ReadOnlySpan<char> input, Func<Dictionary<string, object?>, T> factory)
    {
        var d = new AsonDecoder(input, _schemaCache);
        d.SkipWs();
        return d.ParseVecStructWith(factory);
    }
}

/// <summary>Internal decoder state — ref struct for zero-alloc stack usage.</summary>
internal ref struct AsonDecoder
{
    private readonly ReadOnlySpan<char> _input;
    private readonly ConcurrentDictionary<int, string[]>? _schemaCache;
    internal readonly int Len;
    internal int Pos;

    public AsonDecoder(ReadOnlySpan<char> input, ConcurrentDictionary<int, string[]>? schemaCache = null)
    {
        _input = input;
        _schemaCache = schemaCache;
        Len = input.Length;
        Pos = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => Pos < Len ? _input[Pos] : '\0';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Next()
    {
        if (Pos >= Len) throw AsonException.Eof;
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
        int schemaStart = Pos;
        if (Next() != '{') throw AsonException.ExpectedOpenBrace;

        // Try cache lookup: find end of schema header first
        int braceDepth = 1;
        int scanPos = Pos;
        while (scanPos < Len && braceDepth > 0)
        {
            char c = _input[scanPos];
            if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            scanPos++;
        }
        // scanPos now points right after the closing '}'
        int schemaEnd = scanPos;
        int hash = string.GetHashCode(_input[schemaStart..schemaEnd]);

        if (_schemaCache != null && _schemaCache.TryGetValue(hash, out var cached))
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
            if (fields.Count > 0) { if (Next() != ',') throw AsonException.ExpectedComma; SkipWs(); }

            int start = Pos;
            int idx = SimdHelper.IndexOfSchemaDelimiter(_input[Pos..]);
            if (idx >= 0) Pos += idx; else Pos = Len;
            string name = _input[start..Pos].ToString();
            SkipWs();

            // Skip optional type annotation / structural marker
            if (Pos < Len && _input[Pos] == '@')
            {
                Pos++;
                SkipWs();
                if (Pos < Len)
                {
                    char tc = _input[Pos];
                    if (tc == '{') SkipBalanced('{', '}');
                    else if (tc == '[') SkipBalanced('[', ']');
                    else
                    {
                        while (Pos < Len)
                        {
                            char c = _input[Pos];
                            if (c == ',' || c == '}' || c == ' ' || c == '\t') break;
                            Pos++;
                        }
                    }
                }
            }
            fields.Add(name);
        }
        var result = fields.ToArray();
        _schemaCache?.TryAdd(hash, result);
        return result;
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
        throw AsonException.Eof;
    }

    // Struct parsing
    internal Dictionary<string, object?> ParseSingleStruct()
    {
        SkipWsAndComments();
        if (Pos < Len && _input[Pos] == '[' && Pos + 1 < Len && _input[Pos + 1] == '{')
        {
            throw new AsonException("expected struct, got vec. Use DecodeList instead.");
        }
        var fields = ParseSchema();
        SkipWsAndComments();
        if (Next() != ':') throw AsonException.ExpectedColon;
        SkipWsAndComments();
        return ParseTupleAsMap(fields);
    }

    internal List<Dictionary<string, object?>> ParseVecStruct()
    {
        Pos++; // skip [
        var fields = ParseSchema();
        SkipWs();
        if (Next() != ']') throw AsonException.ExpectedCloseBracket;
        SkipWs();
        if (Next() != ':') throw AsonException.ExpectedColon;

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
        if (Next() != ']') throw AsonException.ExpectedCloseBracket;
        SkipWs();
        if (Next() != ':') throw AsonException.ExpectedColon;

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
            case '<': throw AsonException.UnsupportedMap;
            case '"':
                Pos++;
                while (Pos < Len)
                {
                    char ch = _input[Pos];
                    if (ch == '\\') { Pos += 2; }
                    else if (ch == '"') { Pos++; return; }
                    else Pos++;
                }
                throw AsonException.UnclosedString;
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

        // Fast path: number (very common in ASON data)
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
        if (c == '<') throw AsonException.UnsupportedMap;
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
        long intVal = 0;
        int digits = 0;
        while (Pos < Len)
        {
            int d = _input[Pos] - '0';
            if ((uint)d > 9) break;
            intVal = intVal * 10 + d;
            Pos++;
            digits++;
        }
        if (digits == 0) throw AsonException.InvalidNumber;
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
        return negative ? -intVal : intVal;
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
                if (Pos >= Len) throw AsonException.UnclosedString;
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
                        if (Pos + 4 > Len) throw AsonException.InvalidUnicodeEscape;
                        int cp = int.Parse(_input.Slice(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        buf.AppendFormatted((char)cp);
                        Pos += 4;
                        break;
                    default: throw new AsonException($"invalid escape: \\{esc}");
                }
            }
            else { buf.AppendFormatted(ch); Pos++; }
        }
        throw AsonException.UnclosedString;
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
                    if (i >= raw.Length) throw AsonException.Eof;
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
                        default: throw new AsonException($"invalid escape: \\{e}");
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
