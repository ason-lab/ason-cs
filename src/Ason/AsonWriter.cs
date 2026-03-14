using System.Buffers;
using System.Runtime.CompilerServices;

namespace Ason;

/// <summary>
/// High-performance value writer for ASON encoding.
/// Uses ArrayPool to avoid allocations. All writes go directly to a char buffer.
/// </summary>
public struct AsonWriter : IDisposable
{
    private char[] _buf;
    private int _pos;

    /// <summary>True if no buffer has been allocated yet.</summary>
    public readonly bool IsEmpty => _buf == null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AsonWriter(int initialCapacity = 256)
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
        if (double.IsFinite(v) && v == Math.Truncate(v))
        {
            WriteInt((long)v);
            WriteSpan(".0");
            return;
        }
        if (double.IsFinite(v))
        {
            double v10 = v * 10;
            if (v10 == Math.Truncate(v10) && Math.Abs(v10) < 1e15)
            {
                long vi = (long)v10;
                long intPart = Math.Abs(vi) / 10;
                long frac = Math.Abs(vi) % 10;
                if (vi < 0) WriteChar('-');
                WriteInt(intPart);
                WriteChar('.');
                WriteChar((char)('0' + frac));
                return;
            }
            double v100 = v * 100;
            if (v100 == Math.Truncate(v100) && Math.Abs(v100) < 1e15)
            {
                long vi = (long)v100;
                long intPart = Math.Abs(vi) / 100;
                long frac = Math.Abs(vi) % 100;
                if (vi < 0) WriteChar('-');
                WriteInt(intPart);
                WriteChar('.');
                long d1 = frac / 10;
                long d2 = frac % 10;
                WriteChar((char)('0' + d1));
                if (d2 != 0) WriteChar((char)('0' + d2));
                return;
            }
        }
        Span<char> tmp = stackalloc char[32];
        v.TryFormat(tmp, out int written);
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
        if (s[0] == ' ' || s[^1] == ' ') return true;
        if (s.SequenceEqual("true") || s.SequenceEqual("false")) return true;
        if (SimdHelper.ContainsAnySpecial(s)) return true;

        int start = 0;
        if (s[0] == '-') start = 1;
        if (start < s.Length)
        {
            bool couldBeNumber = true;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || c == '.')) { couldBeNumber = false; break; }
            }
            if (couldBeNumber) return true;
        }
        return false;
    }

    private void WriteEscaped(ReadOnlySpan<char> s)
    {
        EnsureCapacity(s.Length * 2 + 2);
        WriteChar('"');
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '"': WriteSpan("\\\""); break;
                case '\\': WriteSpan("\\\\"); break;
                case '\n': WriteSpan("\\n"); break;
                case '\r': WriteSpan("\\r"); break;
                case '\t': WriteSpan("\\t"); break;
                case ',': WriteSpan("\\,"); break;
                case '(': WriteSpan("\\("); break;
                case ')': WriteSpan("\\)"); break;
                case '[': WriteSpan("\\["); break;
                case ']': WriteSpan("\\]"); break;
                default: WriteChar(s[i]); break;
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
                throw AsonException.UnsupportedMap;
            case IAsonSchema schema:
                WriteChar('(');
                schema.WriteValues(ref this);
                WriteChar(')');
                break;
            case System.Collections.IList list:
                if (list.Count > 0 && list[0] is IAsonSchema)
                {
                    WriteChar('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) WriteChar(',');
                        WriteChar('(');
                        ((IAsonSchema)list[i]!).WriteValues(ref this);
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
