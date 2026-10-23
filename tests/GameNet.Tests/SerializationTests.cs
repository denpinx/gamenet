using GameNet.Core;
using Xunit;

namespace GameNet.Tests;

public class SerializationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(-300)]
    [InlineData(int.MinValue)]
    public void VarInt_RoundTrips(int value)
    {
        var w = new ByteWriter();
        w.WriteVarInt(value);
        var reader = new ByteReader(w.Written);
        Assert.Equal(value, reader.ReadVarInt());
        Assert.True(reader.End);
    }

    [Fact]
    public void VarUInt_UsesFewerBytesForSmallValues()
    {
        var w = new ByteWriter();
        w.WriteVarUInt(1);
        w.WriteVarUInt(300);
        Assert.Equal(3, w.Length); // 1 byte + 2 bytes
    }

    [Fact]
    public void Reader_ThrowsOnUnderflow()
    {
        var reader = new ByteReader(new byte[] { 1 });
        reader.ReadByte();
        var threw = false;
        try { reader.ReadByte(); } catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Entity_FullRoundTrip()
    {
        var state = EntityState.Create(42, EntityKind.Player, 1.5f, -2.25f, 3f, 4f, 5f, 90, 2, 7);
        var w = new ByteWriter();
        w.WriteVarInt(state.Id);
        EntityCodec.WriteFull(ref w, state);

        var r = new ByteReader(w.Written);
        Assert.Equal(42, r.ReadVarInt());
        var decoded = EntityCodec.ReadFull(ref r);

        Assert.Equal(state.X, decoded.X);
        Assert.Equal(state.Health, decoded.Health);
        Assert.Equal(state.Team, decoded.Team);
        Assert.Equal(state.Flags, decoded.Flags);
    }

    [Fact]
    public void Entity_DeltaMask_OnlyChangedFields()
    {
        var a = EntityState.Create(1, EntityKind.Player, 10, 20, 0, 0, 0, 100, 1, 0);
        var b = a;
        b.X = 11;
        b.Health = 80;

        var mask = EntityCodec.DiffMask(a, b);
        Assert.Equal(EntityCodec.FX | EntityCodec.FHealth, mask);
        Assert.Equal(0UL, mask & EntityCodec.FY);
    }

    [Fact]
    public void Entity_DeltaRoundTrip_AppliesOnlyMaskedFields()
    {
        var a = EntityState.Create(1, EntityKind.Player, 10, 20, 0, 0, 0, 100, 1, 0);
        var b = a;
        b.X = 11;
        b.Health = 80;

        var mask = EntityCodec.DiffMask(a, b);
        var w = new ByteWriter();
        w.WriteVarInt(b.Id);
        w.WriteVarUInt((uint)mask);
        EntityCodec.WriteDelta(ref w, b, mask);

        var r = new ByteReader(w.Written);
        var id = r.ReadVarInt();
        var readMask = r.ReadVarUInt();
        EntityCodec.ReadDelta(ref r, a, readMask, out var patch);
        var applied = a;
        EntityCodec.ApplyFields(ref applied, patch, readMask);

        Assert.Equal(1, id);
        Assert.Equal(11, applied.X);
        Assert.Equal(80, applied.Health);
        Assert.Equal(20, applied.Y); // untouched
    }
}
