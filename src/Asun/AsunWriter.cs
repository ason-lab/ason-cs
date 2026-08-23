using System.Buffers;
using System.Runtime.CompilerServices;

namespace Asun;

/// <summary>
/// High-performance value writer for ASUN encoding.
/// Uses ArrayPool to avoid allocations. All writes go directly to a char buffer.
/// </summary>
public struct AsunWriter : IDisposable
{
    private char[] _buf;
    private int _pos;

    /// <summary>True if no buffer has been allocated yet.</summary>
    public readonly bool IsEmpty => _buf == null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AsunWriter(int initialCapacity = 256)
    {
        _buf = ArrayPool<char>.Shared.Rent(initialCapacity);
        _pos = 0;
    }

    /// <summary>Reset position to reuse buffer without rent/return.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset() => _pos = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int extra)
    {
        if (_pos + extra <= _buf.Length) return;
        int newLen = _buf.Length * 2;
        while (newLen < _pos + extra) newLen *= 2;
        var newBuf = ArrayPool<char>.Shared.Rent(newLen);
        _buf.AsSpan(0, _pos).CopyTo(newBuf);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = newBuf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteChar(char c)
    {
        EnsureCapacity(1);
        _buf[_pos++] = c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpan(ReadOnlySpan<char> s)
    {
        EnsureCapacity(s.Length);
        s.CopyTo(_buf.AsSpan(_pos));
        _pos += s.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool v)
    {
        if (v) WriteSpan("true"); else WriteSpan("false");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt(long v)
    {
        Span<char> tmp = stackalloc char[20];
        v.TryFormat(tmp, out int written);
        EnsureCapacity(written);
        tmp[..written].CopyTo(_buf.AsSpan(_pos));
        _pos += written;
    }

    public void WriteDouble(double v)
    {
        // Preserve the sign of negative zero, which the integer fast-path below
        // would otherwise flatten to "0.0" (P1-4).
        if (v == 0.0 && double.IsNegative(v))
        {
            WriteSpan("-0.0");
            return;
        }
        // Whole finite values render as "<int>.0". The prior x10/x100 fast-paths
        // produced non-shortest / non-round-tripping output for values such as
        // 0.1 or 2.675, so everything else goes through the round-trip formatter.
        if (double.IsFinite(v) && v == Math.Truncate(v) && Math.Abs(v) < 1e16)
        {
            WriteInt((long)v);
            WriteSpan(".0");
            return;
        }
        // "R" (round-trip) yields the shortest string that parses back to the
        // exact same double.
        Span<char> tmp = stackalloc char[32];
        v.TryFormat(tmp, out int written, "R", System.Globalization.CultureInfo.InvariantCulture);
        EnsureCapacity(written);
        tmp[..written].CopyTo(_buf.AsSpan(_pos));
        _pos += written;
    }

    public void WriteString(ReadOnlySpan<char> s)
    {
        if (NeedsQuoting(s))
            WriteEscaped(s);
        else
        {
            EnsureCapacity(s.Length);
            s.CopyTo(_buf.AsSpan(_pos));
            _pos += s.Length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool NeedsQuoting(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return true;
        char f = s[0], e = s[^1];
        if (f == ' ' || f == '\t' || f == '\n' || f == '\r') return true;
        if (e == ' ' || e == '\t' || e == '\n' || e == '\r') return true;
        if (s.SequenceEqual("true") || s.SequenceEqual("false")) return true;
        if (SimdHelper.ContainsAnySpecial(s)) return true;
        // Any other ASCII control char (0x00-0x1F) or DEL (0x7F) forces quoting.
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c < 0x20 || c == 0x7f) return true;
        }

        // Block-comment lookalike forces quoting (e.g. "a/*b", "/*").
        for (int k = 0; k + 1 < s.Length; k++)
        {
            if (s[k] == '/' && s[k + 1] == '*') return true;
        }

        if (TokenLooksLikeNumber(s)) return true;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TokenLooksLikeNumber(ReadOnlySpan<char> s)
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
        return i == s.Length && (beforeDot > 0 || hasDot || hasExp);
    }

    private void WriteEscaped(ReadOnlySpan<char> s)
    {
        EnsureCapacity(s.Length * 6 + 2);
        Span<char> hex = stackalloc char[6];
        hex[0] = '\\';
        hex[1] = 'u';
        WriteChar('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"':  WriteSpan("\\\""); break;
                case '\\': WriteSpan("\\\\"); break;
                case '\n': WriteSpan("\\n"); break;
                case '\r': WriteSpan("\\r"); break;
                case '\t': WriteSpan("\\t"); break;
                case '\b': WriteSpan("\\b"); break;
                case '\f': WriteSpan("\\f"); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        const string h = "0123456789abcdef";
                        hex[2] = h[(c >> 12) & 0xF];
                        hex[3] = h[(c >> 8) & 0xF];
                        hex[4] = h[(c >> 4) & 0xF];
                        hex[5] = h[c & 0xF];
                        WriteSpan(hex);
                    }
                    else
                    {
                        WriteChar(c);
                    }
                    break;
            }
        }
        WriteChar('"');
    }

    public void WriteValue(object? v)
    {
        switch (v)
        {
            case null: break;
            case bool b: WriteBool(b); break;
            case int i: WriteInt(i); break;
            case long l: WriteInt(l); break;
            case float f: WriteDouble(f); break;
            case double d: WriteDouble(d); break;
            case string s: WriteString(s); break;
            case System.Collections.IDictionary:
                throw AsunException.UnsupportedMap;
            case IAsunSchema schema:
                WriteChar('(');
                schema.WriteValues(ref this);
                WriteChar(')');
                break;
            case System.Collections.IList list:
                if (list.Count > 0 && list[0] is IAsunSchema)
                {
                    WriteChar('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) WriteChar(',');
                        WriteChar('(');
                        ((IAsunSchema)list[i]!).WriteValues(ref this);
                        WriteChar(')');
                    }
                    WriteChar(']');
                }
                else
                {
                    WriteChar('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) WriteChar(',');
                        WriteValue(list[i]);
                    }
                    if (list.Count > 0 && list[list.Count - 1] is null)
                        WriteChar(',');
                    WriteChar(']');
                }
                break;
            default: WriteString(v.ToString()); break;
        }
    }

    public override readonly string ToString() => new(_buf, 0, _pos);

    public readonly ReadOnlySpan<char> Written => _buf.AsSpan(0, _pos);

    public void Dispose()
    {
        if (_buf != null)
        {
            ArrayPool<char>.Shared.Return(_buf);
            _buf = null!;
        }
    }
}
