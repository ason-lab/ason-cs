using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Asun;

/// <summary>
/// ASUN binary codec. Zero-copy decoding from ReadOnlySpan&lt;byte&gt;.
/// Wire (LEB128 varint; matches the Rust reference): bool=1B, int=zigzag+LEB128 varint,
/// double=8B LE, string=uvarint len+UTF8, list=uvarint count+elements.
/// </summary>
public static class BinaryCodec
{
    public static byte[] EncodeBinary(IAsunSchema value)
    {
        var w = new BinWriter(256);
        value.WriteBinaryValues(ref w);
        return w.ToArray();
    }

    public static byte[] EncodeBinary<T>(IReadOnlyList<T> values) where T : IAsunSchema
    {
        var w = new BinWriter(values.Count * 64 + 32);
        w.WriteUvarint((ulong)values.Count);
        for (int i = 0; i < values.Count; i++)
            values[i].WriteBinaryValues(ref w);
        return w.ToArray();
    }

    public static void WriteBinaryValue(ref BinWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteU8(0); break;
            case bool b: w.WriteBool(b); break;
            case int i: w.WriteI64(i); break;
            case long l: w.WriteI64(l); break;
            case float f: w.WriteF64(f); break;
            case double d: w.WriteF64(d); break;
            case string s: w.WriteString(s); break;
            case System.Collections.IDictionary:
                throw AsunException.UnsupportedMap;
            case IAsunSchema schema:
                schema.WriteBinaryValues(ref w);
                break;
            case System.Collections.IList list:
                if (list.Count > 0 && list[0] is IAsunSchema)
                {
                    w.WriteUvarint((ulong)list.Count);
                    for (int i = 0; i < list.Count; i++)
                        ((IAsunSchema)list[i]!).WriteBinaryValues(ref w);
                }
                else
                {
                    w.WriteUvarint((ulong)list.Count);
                    for (int i = 0; i < list.Count; i++)
                        WriteBinaryValue(ref w, list[i]);
                }
                break;
            default: w.WriteString(v.ToString() ?? ""); break;
        }
    }

    public static T DecodeBinaryWith<T>(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<string> fields,
        ReadOnlySpan<FieldType> types,
        Func<Dictionary<string, object?>, T> factory)
    {
        var r = new BinReader(data);
        var map = new Dictionary<string, object?>(fields.Length);
        for (int i = 0; i < fields.Length; i++)
            map[fields[i]] = r.ReadTyped(types[i]);
        return factory(map);
    }

    public static List<T> DecodeBinaryListWith<T>(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<string> fields,
        ReadOnlySpan<FieldType> types,
        Func<Dictionary<string, object?>, T> factory)
    {
        var r = new BinReader(data);
        ulong count = r.ReadUvarint();
        var result = new List<T>((int)count);
        for (ulong c = 0; c < count; c++)
        {
            var map = new Dictionary<string, object?>(fields.Length);
            for (int i = 0; i < fields.Length; i++)
                map[fields[i]] = r.ReadTyped(types[i]);
            result.Add(factory(map));
        }
        return result;
    }
}

public enum FieldType
{
    Bool, Int, Double, String,
    OptionalInt, OptionalDouble, OptionalString, OptionalBool,
    ListInt, ListDouble, ListString, ListBool
}

public struct BinWriter
{
    private byte[] _buf;
    private int _pos;

    public BinWriter(int capacity) { _buf = ArrayPool<byte>.Shared.Rent(capacity); _pos = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int extra)
    {
        if (_pos + extra <= _buf.Length) return;
        int newLen = _buf.Length * 2;
        while (newLen < _pos + extra) newLen *= 2;
        var newBuf = ArrayPool<byte>.Shared.Rent(newLen);
        _buf.AsSpan(0, _pos).CopyTo(newBuf);
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = newBuf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteU8(byte v) { EnsureCapacity(1); _buf[_pos++] = v; }

    /// LEB128 unsigned varint.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUvarint(ulong v)
    {
        EnsureCapacity(10);
        while (v >= 0x80)
        {
            _buf[_pos++] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        _buf[_pos++] = (byte)v;
    }

    /// zigzag + LEB128 signed varint.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteI64(long v)
    {
        ulong zz = (ulong)((v << 1) ^ (v >> 63));
        WriteUvarint(zz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteF64(double v)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(_pos), v);
        _pos += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool v) => WriteU8(v ? (byte)1 : (byte)0);

    public void WriteString(ReadOnlySpan<char> s)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(s.Length);
        // uvarint length prefix is at most 5 bytes for a 32-bit byte count.
        EnsureCapacity(5 + maxBytes);
        int byteCount = Encoding.UTF8.GetByteCount(s);
        WriteUvarint((ulong)byteCount);
        int written = Encoding.UTF8.GetBytes(s, _buf.AsSpan(_pos));
        _pos += written;
    }

    public byte[] ToArray()
    {
        var result = _buf.AsSpan(0, _pos).ToArray();
        ArrayPool<byte>.Shared.Return(_buf);
        return result;
    }
}

internal ref struct BinReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BinReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int n) { if (_pos + n > _data.Length) throw AsunException.Eof; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadU8() { Ensure(1); return _data[_pos++]; }

    /// LEB128 unsigned varint.
    public ulong ReadUvarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _data.Length) throw AsunException.Eof;
            byte b = _data[_pos++];
            if (shift >= 64) throw new AsunException("binary decode: varint overflow");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    /// zigzag + LEB128 signed varint.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadI64()
    {
        ulong v = ReadUvarint();
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadF64() { Ensure(8); double v = BinaryPrimitives.ReadDoubleLittleEndian(_data[_pos..]); _pos += 8; return v; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadU8() != 0;

    public string ReadString()
    {
        ulong len = ReadUvarint();
        Ensure((int)len);
        string result = Encoding.UTF8.GetString(_data.Slice(_pos, (int)len));
        _pos += (int)len;
        return result;
    }

    public object? ReadTyped(FieldType type)
    {
        switch (type)
        {
            case FieldType.Bool: return ReadBool();
            case FieldType.Int: return ReadI64();
            case FieldType.Double: return ReadF64();
            case FieldType.String: return ReadString();
            case FieldType.OptionalInt: return ReadU8() == 0 ? null : (object)ReadI64();
            case FieldType.OptionalDouble: return ReadU8() == 0 ? null : (object)ReadF64();
            case FieldType.OptionalString: return ReadU8() == 0 ? null : ReadString();
            case FieldType.OptionalBool: return ReadU8() == 0 ? null : (object)ReadBool();
            case FieldType.ListInt:
            {
                ulong count = ReadUvarint();
                var list = new List<object>((int)count);
                for (ulong i = 0; i < count; i++) list.Add(ReadI64());
                return list;
            }
            case FieldType.ListDouble:
            {
                ulong count = ReadUvarint();
                var list = new List<object>((int)count);
                for (ulong i = 0; i < count; i++) list.Add(ReadF64());
                return list;
            }
            case FieldType.ListString:
            {
                ulong count = ReadUvarint();
                var list = new List<object>((int)count);
                for (ulong i = 0; i < count; i++) list.Add(ReadString());
                return list;
            }
            case FieldType.ListBool:
            {
                ulong count = ReadUvarint();
                var list = new List<object>((int)count);
                for (ulong i = 0; i < count; i++) list.Add(ReadBool());
                return list;
            }
            default: throw new AsunException($"unknown field type: {type}");
        }
    }
}
