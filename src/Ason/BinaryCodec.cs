using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ason;

/// <summary>
/// ASON binary codec. Zero-copy decoding from ReadOnlySpan&lt;byte&gt;.
/// Wire: bool=1B, int=8B LE, double=8B LE, string=u32LE len+UTF8, list=u32LE count+elements.
/// </summary>
public static class BinaryCodec
{
    public static byte[] EncodeBinary(IAsonSchema value)
    {
        var w = new BinWriter(256);
        value.WriteBinaryValues(ref w);
        return w.ToArray();
    }

    public static byte[] EncodeBinary<T>(IReadOnlyList<T> values) where T : IAsonSchema
    {
        var w = new BinWriter(values.Count * 64 + 32);
        w.WriteU32((uint)values.Count);
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
                throw AsonException.UnsupportedMap;
            case IAsonSchema schema:
                schema.WriteBinaryValues(ref w);
                break;
            case System.Collections.IList list:
                if (list.Count > 0 && list[0] is IAsonSchema)
                {
                    w.WriteU32((uint)list.Count);
                    for (int i = 0; i < list.Count; i++)
                        ((IAsonSchema)list[i]!).WriteBinaryValues(ref w);
                }
                else
                {
                    w.WriteU32((uint)list.Count);
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
        uint count = r.ReadU32();
        var result = new List<T>((int)count);
        for (uint c = 0; c < count; c++)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteU32(uint v)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), v);
        _pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteI64(long v)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_pos), v);
        _pos += 8;
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
        EnsureCapacity(4 + maxBytes);
        int written = Encoding.UTF8.GetBytes(s, _buf.AsSpan(_pos + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), (uint)written);
        _pos += 4 + written;
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
    private void Ensure(int n) { if (_pos + n > _data.Length) throw AsonException.Eof; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadU8() { Ensure(1); return _data[_pos++]; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadU32() { Ensure(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadI64() { Ensure(8); long v = BinaryPrimitives.ReadInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadF64() { Ensure(8); double v = BinaryPrimitives.ReadDoubleLittleEndian(_data[_pos..]); _pos += 8; return v; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadU8() != 0;

    public string ReadString()
    {
        uint len = ReadU32();
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
                uint count = ReadU32();
                var list = new List<object>((int)count);
                for (uint i = 0; i < count; i++) list.Add(ReadI64());
                return list;
            }
            case FieldType.ListDouble:
            {
                uint count = ReadU32();
                var list = new List<object>((int)count);
                for (uint i = 0; i < count; i++) list.Add(ReadF64());
                return list;
            }
            case FieldType.ListString:
            {
                uint count = ReadU32();
                var list = new List<object>((int)count);
                for (uint i = 0; i < count; i++) list.Add(ReadString());
                return list;
            }
            case FieldType.ListBool:
            {
                uint count = ReadU32();
                var list = new List<object>((int)count);
                for (uint i = 0; i < count; i++) list.Add(ReadBool());
                return list;
            }
            default: throw new AsonException($"unknown field type: {type}");
        }
    }
}
