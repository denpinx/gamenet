namespace GameNet.Core;

public enum MessageType : byte
{
    SnapshotChunk = 1,
    RpcRequest = 2,
    RpcResponse = 3,
    Subscribe = 4,
    BindRequest = 5,
    BindResponse = 6,
    Input = 7,
    Ping = 8,
    Resync = 9,
    Unsubscribe = 10,
    CollectionRegistry = 11,
    RpcCall = 12,
    RpcCallResponse = 13,
}

public enum EntityKind : byte
{
    Player = 0,
    Npc = 1,
    Prop = 2,
    Projectile = 3,
}

public enum DeltaOp : byte
{
    Update = 0,
    Add = 1,
    Remove = 2,
}

/// <summary>Replicated entity state. Field mask bits are the wire contract; never renumber.</summary>
public struct EntityState
{
    public int Id;
    public EntityKind Kind;
    public float X;
    public float Y;
    public float VX;
    public float VY;
    public float Rot;
    public short Health;
    public byte Team;
    public byte Flags;

    public bool SameAs(in EntityState other) =>
        Id == other.Id &&
        Kind == other.Kind &&
        BitConverter.SingleToInt32Bits(X) == BitConverter.SingleToInt32Bits(other.X) &&
        BitConverter.SingleToInt32Bits(Y) == BitConverter.SingleToInt32Bits(other.Y) &&
        BitConverter.SingleToInt32Bits(VX) == BitConverter.SingleToInt32Bits(other.VX) &&
        BitConverter.SingleToInt32Bits(VY) == BitConverter.SingleToInt32Bits(other.VY) &&
        BitConverter.SingleToInt32Bits(Rot) == BitConverter.SingleToInt32Bits(other.Rot) &&
        Health == other.Health &&
        Team == other.Team &&
        Flags == other.Flags;

    public static EntityState Create(int id, EntityKind kind, float x, float y, float vx = 0, float vy = 0,
        float rot = 0, short health = 100, byte team = 0, byte flags = 0) => new()
    {
        Id = id, Kind = kind, X = x, Y = y, VX = vx, VY = vy, Rot = rot, Health = health, Team = team, Flags = flags,
    };
}

/// <summary>Field-level codec: full state for spawns, mask+changed fields for updates.</summary>
public static class EntityCodec
{
    public const ulong FKind = 1UL << 0;
    public const ulong FX = 1UL << 1;
    public const ulong FY = 1UL << 2;
    public const ulong FVX = 1UL << 3;
    public const ulong FVY = 1UL << 4;
    public const ulong FRot = 1UL << 5;
    public const ulong FHealth = 1UL << 6;
    public const ulong FTeam = 1UL << 7;
    public const ulong FFlags = 1UL << 8;

    public static ulong DiffMask(in EntityState previous, in EntityState current)
    {
        ulong mask = 0;
        if (previous.Kind != current.Kind) mask |= FKind;
        if (BitConverter.SingleToInt32Bits(previous.X) != BitConverter.SingleToInt32Bits(current.X)) mask |= FX;
        if (BitConverter.SingleToInt32Bits(previous.Y) != BitConverter.SingleToInt32Bits(current.Y)) mask |= FY;
        if (BitConverter.SingleToInt32Bits(previous.VX) != BitConverter.SingleToInt32Bits(current.VX)) mask |= FVX;
        if (BitConverter.SingleToInt32Bits(previous.VY) != BitConverter.SingleToInt32Bits(current.VY)) mask |= FVY;
        if (BitConverter.SingleToInt32Bits(previous.Rot) != BitConverter.SingleToInt32Bits(current.Rot)) mask |= FRot;
        if (previous.Health != current.Health) mask |= FHealth;
        if (previous.Team != current.Team) mask |= FTeam;
        if (previous.Flags != current.Flags) mask |= FFlags;
        return mask;
    }

    public static void WriteFull(ref ByteWriter writer, in EntityState state)
    {
        writer.WriteByte((byte)state.Kind);
        writer.WriteFloat(state.X);
        writer.WriteFloat(state.Y);
        writer.WriteFloat(state.VX);
        writer.WriteFloat(state.VY);
        writer.WriteFloat(state.Rot);
        writer.WriteShort(state.Health);
        writer.WriteByte(state.Team);
        writer.WriteByte(state.Flags);
    }

    public static EntityState ReadFull(ref ByteReader reader) => new()
    {
        Kind = (EntityKind)reader.ReadByte(),
        X = reader.ReadFloat(),
        Y = reader.ReadFloat(),
        VX = reader.ReadFloat(),
        VY = reader.ReadFloat(),
        Rot = reader.ReadFloat(),
        Health = reader.ReadShort(),
        Team = reader.ReadByte(),
        Flags = reader.ReadByte(),
    };

    public static void WriteDelta(ref ByteWriter writer, in EntityState state, ulong mask)
    {
        if ((mask & FKind) != 0) writer.WriteByte((byte)state.Kind);
        if ((mask & FX) != 0) writer.WriteFloat(state.X);
        if ((mask & FY) != 0) writer.WriteFloat(state.Y);
        if ((mask & FVX) != 0) writer.WriteFloat(state.VX);
        if ((mask & FVY) != 0) writer.WriteFloat(state.VY);
        if ((mask & FRot) != 0) writer.WriteFloat(state.Rot);
        if ((mask & FHealth) != 0) writer.WriteShort(state.Health);
        if ((mask & FTeam) != 0) writer.WriteByte(state.Team);
        if ((mask & FFlags) != 0) writer.WriteByte(state.Flags);
    }

    public static void ReadDelta(ref ByteReader reader, in EntityState state, ulong mask, out EntityState patch)
    {
        patch = state;
        if ((mask & FKind) != 0) patch.Kind = (EntityKind)reader.ReadByte();
        if ((mask & FX) != 0) patch.X = reader.ReadFloat();
        if ((mask & FY) != 0) patch.Y = reader.ReadFloat();
        if ((mask & FVX) != 0) patch.VX = reader.ReadFloat();
        if ((mask & FVY) != 0) patch.VY = reader.ReadFloat();
        if ((mask & FRot) != 0) patch.Rot = reader.ReadFloat();
        if ((mask & FHealth) != 0) patch.Health = reader.ReadShort();
        if ((mask & FTeam) != 0) patch.Team = reader.ReadByte();
        if ((mask & FFlags) != 0) patch.Flags = reader.ReadByte();
    }

    public static void ApplyFields(ref EntityState target, in EntityState patch, ulong mask)
    {
        if ((mask & FKind) != 0) target.Kind = patch.Kind;
        if ((mask & FX) != 0) target.X = patch.X;
        if ((mask & FY) != 0) target.Y = patch.Y;
        if ((mask & FVX) != 0) target.VX = patch.VX;
        if ((mask & FVY) != 0) target.VY = patch.VY;
        if ((mask & FRot) != 0) target.Rot = patch.Rot;
        if ((mask & FHealth) != 0) target.Health = patch.Health;
        if ((mask & FTeam) != 0) target.Team = patch.Team;
        if ((mask & FFlags) != 0) target.Flags = patch.Flags;
    }
}

public struct EntityDelta
{
    public int Id;
    public DeltaOp Op;
    public EntityState State;
    public ulong Mask;
}

public struct ChunkEntry
{
    public int Id;
    public DeltaOp Op;
    public ulong Mask;
    public EntityState State;
}

public sealed class SnapshotChunk
{
    public byte Collection;
    public uint Version;
    public uint ServerTimeMs;
    public int Index;
    public int Count;
    public List<ChunkEntry> Entries = new();
}

/// <summary>Packs a tick's deltas into MTU-sized chunks; never splits one entity across chunks.</summary>
public static class SnapshotCodec
{
    public const int MaxChunkPayload = 1100;
    private const int HeaderAllowance = 20;

    public static List<byte[]> Build(uint version, List<EntityDelta> deltas, uint serverTimeMs = 0, byte collection = 0)
    {
        var encoded = new List<byte[]>(deltas.Count);
        foreach (var delta in deltas)
        {
            var w = new ByteWriter(40);
            w.WriteVarInt(delta.Id);
            w.WriteByte((byte)delta.Op);
            switch (delta.Op)
            {
                case DeltaOp.Add:
                    EntityCodec.WriteFull(ref w, in delta.State);
                    break;
                case DeltaOp.Update:
                    w.WriteVarUInt((uint)delta.Mask);
                    EntityCodec.WriteDelta(ref w, in delta.State, delta.Mask);
                    break;
                case DeltaOp.Remove:
                    break;
            }
            encoded.Add(w.ToArray());
        }

        var groups = new List<List<byte[]>>();
        var current = new List<byte[]>();
        var currentSize = 0;
        foreach (var entry in encoded)
        {
            if (current.Count > 0 && currentSize + entry.Length + HeaderAllowance > MaxChunkPayload)
            {
                groups.Add(current);
                current = new List<byte[]>();
                currentSize = 0;
            }
            current.Add(entry);
            currentSize += entry.Length;
        }
        groups.Add(current);

        var chunks = new List<byte[]>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
            chunks.Add(WriteChunk(collection, version, serverTimeMs, i, groups.Count, groups[i]));
        return chunks;
    }

    private static byte[] WriteChunk(byte collection, uint version, uint serverTimeMs, int index, int count, List<byte[]> entries)
    {
        var w = new ByteWriter(256);
        w.WriteByte((byte)MessageType.SnapshotChunk);
        w.WriteByte(collection);
        w.WriteVarUInt(version);
        w.WriteUInt(serverTimeMs);
        w.WriteByte((byte)index);
        w.WriteByte((byte)count);
        w.WriteVarUInt((uint)entries.Count);
        foreach (var entry in entries) w.WriteBytes(entry);
        return w.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SnapshotChunk chunk)
    {
        chunk = null!;
        var reader = new ByteReader(payload);
        if (reader.ReadByte() != (byte)MessageType.SnapshotChunk) return false;

        var result = new SnapshotChunk
        {
            Collection = reader.ReadByte(),
            Version = reader.ReadVarUInt(),
            ServerTimeMs = reader.ReadUInt(),
            Index = reader.ReadByte(),
            Count = reader.ReadByte(),
        };
        var entryCount = (int)reader.ReadVarUInt();
        result.Entries.Capacity = entryCount;
        for (var i = 0; i < entryCount; i++)
        {
            var id = reader.ReadVarInt();
            var op = (DeltaOp)reader.ReadByte();
            var entry = new ChunkEntry { Id = id, Op = op };
            switch (op)
            {
                case DeltaOp.Add:
                {
                    var state = EntityCodec.ReadFull(ref reader);
                    state.Id = id;
                    entry.State = state;
                    entry.Mask = ulong.MaxValue;
                    break;
                }
                case DeltaOp.Update:
                {
                    var mask = reader.ReadVarUInt();
                    EntityCodec.ReadDelta(ref reader, default, mask, out var patch);
                    patch.Id = id;
                    entry.Mask = mask;
                    entry.State = patch;
                    break;
                }
            }
            result.Entries.Add(entry);
        }
        chunk = result;
        return true;
    }
}
