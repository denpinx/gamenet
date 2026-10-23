using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace GameNet.Core;

/// <summary>Growable little-endian binary writer. No reflection, no allocation per call.</summary>
public sealed class ByteWriter
{
    private byte[] _buffer;
    private int _position;

    public ByteWriter(int capacity = 512) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _position;
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _position);

    public void Reset() => _position = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int extra)
    {
        if (_position + extra <= _buffer.Length) return;
        var newSize = Math.Max(_buffer.Length * 2, _position + extra);
        Array.Resize(ref _buffer, newSize);
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUShort(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    public void WriteShort(short value) => WriteUShort((ushort)value);

    public void WriteUInt(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteInt(int value) => WriteUInt((uint)value);

    public void WriteFloat(float value) => WriteUInt(BitConverter.SingleToUInt32Bits(value));

    public void WriteVarUInt(uint value)
    {
        Ensure(5);
        while (value >= 0x80)
        {
            _buffer[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[_position++] = (byte)value;
    }

    public void WriteVarInt(int value) => WriteVarUInt(ZigZag(value));

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();

    public void CopyTo(Span<byte> destination)
    {
        Written.CopyTo(destination);
    }

    public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));
    public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);
}

/// <summary>Read-only cursor over a payload. Throws on underflow instead of returning garbage.</summary>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public ByteReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _span.Length - _position;
    public bool End => _position >= _span.Length;

    private void Need(int count)
    {
        if (_position + count > _span.Length)
            throw new InvalidOperationException($"payload underflow: need {count} at {_position}, length {_span.Length}");
    }

    public byte ReadByte()
    {
        Need(1);
        return _span[_position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUShort()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_span.Slice(_position, 2));
        _position += 2;
        return value;
    }

    public short ReadShort() => (short)ReadUShort();

    public uint ReadUInt()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_span.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public int ReadInt() => (int)ReadUInt();

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadUInt());

    public uint ReadVarUInt()
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidOperationException("varint too long");
        }
    }

    public int ReadVarInt() => ByteWriter.UnZigZag(ReadVarUInt());

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Need(count);
        var slice = _span.Slice(_position, count);
        _position += count;
        return slice;
    }

    public ReadOnlySpan<byte> ReadRemaining()
    {
        var slice = _span[_position..];
        _position = _span.Length;
        return slice;
    }
}
