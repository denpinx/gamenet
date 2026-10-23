using GameNet.Core;
using GameNet.Transport;
using Xunit;

namespace GameNet.Tests;

/// <summary>
/// Parameterised invariants: the same properties checked across value/field/load matrices.
/// Test *cases* grow fast here, but each row is a real round-trip assertion, not a duplicate.
/// </summary>
public class MatrixTests
{
    public static IEnumerable<object[]> VarIntValues()
    {
        int[] values =
        {
            0, 1, 2, 63, 127, 128, 129, 255, 256, 300, 16383, 16384,
            1 << 20, 1 << 27, int.MaxValue, -1, -2, -63, -64, -127, -128, -300, int.MinValue,
        };
        foreach (var value in values) yield return new object[] { value };
    }

    [Theory]
    [MemberData(nameof(VarIntValues))]
    public void VarInt_RoundTrips(int value)
    {
        var writer = new ByteWriter();
        writer.WriteVarInt(value);
        var reader = new ByteReader(writer.Written);
        Assert.Equal(value, reader.ReadVarInt());
        Assert.True(reader.End);
    }

    public static IEnumerable<object[]> FieldMasks()
    {
        for (var bit = 0; bit < 9; bit++) yield return new object[] { 1UL << bit };
        yield return new object[] { 0UL };
        yield return new object[] { 0x1FFUL };
        yield return new object[] { 0x0F0UL };
        yield return new object[] { 0x101UL };
    }

    [Theory]
    [MemberData(nameof(FieldMasks))]
    public void EntityDelta_AppliesOnlyMaskedFields(ulong mask)
    {
        var previous = EntityState.Create(7, EntityKind.Player, 1, 2, 3, 4, 5, 100, 1, 0);
        var current = EntityState.Create(7, EntityKind.Npc, 9, 8, 7, 6, 5, 50, 2, 3);

        var writer = new ByteWriter();
        writer.WriteVarInt(current.Id);
        writer.WriteVarUInt((uint)mask);
        EntityCodec.WriteDelta(ref writer, current, mask);
        // Delta carries only masked fields; unmasked bytes must not be written.
        Assert.Equal(1 + VarIntSize(mask) + MaskedPayloadSize(mask), writer.Length); // id(1) + mask + fields

        var reader = new ByteReader(writer.Written);
        Assert.Equal(current.Id, reader.ReadVarInt());
        var readMask = reader.ReadVarUInt();
        EntityCodec.ReadDelta(ref reader, previous, readMask, out var patch);
        var result = previous;
        EntityCodec.ApplyFields(ref result, patch, readMask);

        Assert.Equal(Has(mask, EntityCodec.FKind) ? current.Kind : previous.Kind, result.Kind);
        Assert.Equal(Has(mask, EntityCodec.FX) ? current.X : previous.X, result.X);
        Assert.Equal(Has(mask, EntityCodec.FY) ? current.Y : previous.Y, result.Y);
        Assert.Equal(Has(mask, EntityCodec.FVX) ? current.VX : previous.VX, result.VX);
        Assert.Equal(Has(mask, EntityCodec.FVY) ? current.VY : previous.VY, result.VY);
        Assert.Equal(Has(mask, EntityCodec.FRot) ? current.Rot : previous.Rot, result.Rot);
        Assert.Equal(Has(mask, EntityCodec.FHealth) ? current.Health : previous.Health, result.Health);
        Assert.Equal(Has(mask, EntityCodec.FTeam) ? current.Team : previous.Team, result.Team);
        Assert.Equal(Has(mask, EntityCodec.FFlags) ? current.Flags : previous.Flags, result.Flags);
    }

    public static IEnumerable<object[]> RandomStates()
    {
        var random = new Random(20261023);
        for (var i = 0; i < 40; i++)
        {
            yield return new object[]
            {
                EntityState.Create(
                    random.Next(1, 1_000_000),
                    (EntityKind)random.Next(0, 4),
                    (float)(random.NextDouble() * 1000 - 500),
                    (float)(random.NextDouble() * 1000 - 500),
                    (float)(random.NextDouble() * 10 - 5),
                    (float)(random.NextDouble() * 10 - 5),
                    (float)(random.NextDouble() * 6.28),
                    (short)random.Next(0, 200),
                    (byte)random.Next(0, 4),
                    (byte)random.Next(0, 8)),
            };
        }
    }

    [Theory]
    [MemberData(nameof(RandomStates))]
    public void Entity_FullRoundTrip(EntityState state)
    {
        var writer = new ByteWriter();
        writer.WriteVarInt(state.Id);
        EntityCodec.WriteFull(ref writer, state);

        var reader = new ByteReader(writer.Written);
        Assert.Equal(state.Id, reader.ReadVarInt());
        var decoded = EntityCodec.ReadFull(ref reader);

        Assert.Equal(state.Kind, decoded.Kind);
        Assert.Equal(state.X, decoded.X);
        Assert.Equal(state.Y, decoded.Y);
        Assert.Equal(state.VX, decoded.VX);
        Assert.Equal(state.VY, decoded.VY);
        Assert.Equal(state.Rot, decoded.Rot);
        Assert.Equal(state.Health, decoded.Health);
        Assert.Equal(state.Team, decoded.Team);
        Assert.Equal(state.Flags, decoded.Flags);
        Assert.True(reader.End);
    }

    public static IEnumerable<object[]> SnapshotSizes()
    {
        foreach (var count in new[] { 1, 10, 100, 250, 500, 1000, 2000 }) yield return new object[] { count };
    }

    [Theory]
    [MemberData(nameof(SnapshotSizes))]
    public void Snapshot_RoundTripsThroughChunks(int count)
    {
        var deltas = new List<EntityDelta>(count);
        for (var i = 0; i < count; i++)
            deltas.Add(new EntityDelta
            {
                Id = i,
                Op = DeltaOp.Add,
                State = EntityState.Create(i, EntityKind.Prop, i, i * 2),
            });

        var chunks = SnapshotCodec.Build(1, deltas, 1234);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= SnapshotCodec.MaxChunkPayload));

        var world = new ClientWorld();
        foreach (var payload in chunks)
        {
            Assert.True(SnapshotCodec.TryRead(payload, out var chunk));
            world.OnChunk(chunk);
        }

        Assert.Equal(count, world.EntityCount);
        Assert.Equal(chunks.Count, world.ChunksApplied);
    }

    public static IEnumerable<object[]> LossRates()
    {
        foreach (var loss in new[] { 0.0, 0.02, 0.05, 0.10 }) yield return new object[] { loss };
    }

    [Theory]
    [MemberData(nameof(LossRates))]
    public void ReliableChannel_DeliversEverything_AtLossRate(double loss)
    {
        var link = new LossyLink(seed: 42, lossRate: loss);
        var received = new List<byte[]>();
        ReliableChannel? a = null;
        ReliableChannel? b = null;
        a = new ReliableChannel(0, (_, packet) => link.Send(true, packet), _ => { }, () => link.Now);
        b = new ReliableChannel(0, (_, packet) => link.Send(false, packet),
            message => received.Add(message.ToArray()), () => link.Now);

        var sent = new List<byte[]>();
        var start = Environment.TickCount64;
        link.Now = start;
        for (var i = 0; i < 20; i++)
        {
            var payload = new byte[64 + i * 16];
            Random.Shared.NextBytes(payload);
            sent.Add(payload);
            a.Send(payload);
        }

        for (var elapsed = 0L; elapsed < 10_000 && received.Count < sent.Count; elapsed += 5)
        {
            link.Now = start + elapsed;
            a.Tick(link.Now);
            b.Tick(link.Now);
            link.Pump(p => b.OnPacket(p), p => a.OnPacket(p));
        }

        Assert.Equal(sent.Count, received.Count);
        for (var i = 0; i < sent.Count; i++) Assert.Equal(sent[i], received[i]);
    }

    private static bool Has(ulong mask, ulong bit) => (mask & bit) != 0;

    private static int VarIntSize(ulong value)
    {
        var size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    private static int MaskedPayloadSize(ulong mask)
    {
        var size = 0;
        if (Has(mask, EntityCodec.FKind)) size += 1;
        if (Has(mask, EntityCodec.FX)) size += 4;
        if (Has(mask, EntityCodec.FY)) size += 4;
        if (Has(mask, EntityCodec.FVX)) size += 4;
        if (Has(mask, EntityCodec.FVY)) size += 4;
        if (Has(mask, EntityCodec.FRot)) size += 4;
        if (Has(mask, EntityCodec.FHealth)) size += 2;
        if (Has(mask, EntityCodec.FTeam)) size += 1;
        if (Has(mask, EntityCodec.FFlags)) size += 1;
        return size;
    }
}
