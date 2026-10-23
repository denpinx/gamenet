using GameNet.Core;
using GameNet.Generated.GameNet.Tests.Packets;
using GameNet.Tests.Packets;
using Xunit;

namespace GameNet.Tests.Packets
{
    [Packet]
    public sealed class MoveCommand
    {
        [Field(1)] public int PlayerId { get; set; }
        [Field(2)] public float X { get; set; }
        [Field(3)] public float Y { get; set; }
        [Field(4)] public string Note { get; set; } = string.Empty;
        [Field(5)] public bool Sprint { get; set; }
    }

    [Packet]
    public struct DamageEvent
    {
        [Field(1)] public int TargetId { get; set; }
        [Field(2)] public short Amount { get; set; }
    }
}

namespace GameNet.Tests
{
    public class GeneratedCodecTests
    {
        [Fact]
        public void Generated_WriteRead_RoundTrips()
        {
            var value = new MoveCommand { PlayerId = 7, X = 1.5f, Y = -2.25f, Note = "冲刺", Sprint = true };
            var writer = new ByteWriter();
            MoveCommandPacketCodec.Write(ref writer, value);

            var reader = new ByteReader(writer.Written);
            var decoded = MoveCommandPacketCodec.Read(ref reader);

            Assert.Equal(value.PlayerId, decoded.PlayerId);
            Assert.Equal(value.X, decoded.X);
            Assert.Equal(value.Y, decoded.Y);
            Assert.Equal(value.Note, decoded.Note);
            Assert.Equal(value.Sprint, decoded.Sprint);
            Assert.True(reader.End);
        }

        [Fact]
        public void Generated_DiffMask_OnlyChangedFields()
        {
            var a = new MoveCommand { PlayerId = 1, X = 1, Y = 2 };
            var b = new MoveCommand { PlayerId = 1, X = 3, Y = 2 };

            Assert.Equal(1UL << 1, MoveCommandPacketCodec.DiffMask(a, b));
        }

        [Fact]
        public void Generated_Delta_RoundTrips()
        {
            var a = new MoveCommand { PlayerId = 1, X = 1, Y = 2, Note = "a" };
            var b = new MoveCommand { PlayerId = 1, X = 3, Y = 2, Note = "b" };
            var mask = MoveCommandPacketCodec.DiffMask(a, b);

            var writer = new ByteWriter();
            writer.WriteVarUInt((uint)mask);
            MoveCommandPacketCodec.WriteDelta(ref writer, b, mask);

            var reader = new ByteReader(writer.Written);
            var readMask = (ulong)reader.ReadVarUInt();
            var applied = MoveCommandPacketCodec.ApplyDelta(ref reader, a, readMask);

            Assert.Equal(3, applied.X);
            Assert.Equal("b", applied.Note);
            Assert.Equal(2, applied.Y);
        }

        [Fact]
        public void Generated_StructCodec_Works()
        {
            var value = new DamageEvent { TargetId = 42, Amount = 7 };
            var writer = new ByteWriter();
            DamageEventPacketCodec.Write(ref writer, value);

            var reader = new ByteReader(writer.Written);
            var decoded = DamageEventPacketCodec.Read(ref reader);
            Assert.Equal(42, decoded.TargetId);
            Assert.Equal(7, decoded.Amount);
        }

        [Fact]
        public void Generated_SchemaHash_IsStable()
        {
            Assert.False(string.IsNullOrEmpty(MoveCommandPacketCodec.SchemaHash));
            Assert.Equal(MoveCommandPacketCodec.AllFields, 0b11111UL);
        }
    }
}
