using System.Text;
using GameNet.Core;
using GameNet.Generated.GameNet.Tests.Protocol;
using GameNet.Tests.Protocol;
using Xunit;

namespace GameNet.Tests.Protocol
{
    // Same field ids across versions; NewMsg appends id 3, RenamedMsg renames id 2.
    [Packet]
    public readonly partial record struct OldMsg(
        [property: Field(1)] int Id,
        [property: Field(2)] string Name);

    [Packet]
    public readonly partial record struct NewMsg(
        [property: Field(1)] int Id,
        [property: Field(2)] string Name,
        [property: Field(3)] int Level);

    [Packet]
    public readonly partial record struct RenamedMsg(
        [property: Field(1)] int Id,
        [property: Field(2)] string DisplayName);
}

namespace GameNet.Tests
{
    public class ProtocolCompatTests
    {
        private static byte[] Encode<T>(Action<ByteWriter> write)
        {
            var writer = new ByteWriter(64);
            write(writer);
            return writer.ToArray();
        }

        [Fact]
        public void NewReader_ReadsOldPayload_WithDefaultsForNewFields()
        {
            var writer = new ByteWriter(64);
            OldMsgPacketCodec.Write(ref writer, new OldMsg(7, "alice"));

            var reader = new ByteReader(writer.Written);
            var decoded = NewMsgPacketCodec.Read(ref reader);

            Assert.Equal(7, decoded.Id);
            Assert.Equal("alice", decoded.Name);
            Assert.Equal(0, decoded.Level); // field 3 missing -> default
        }

        [Fact]
        public void OldReader_SkipsUnknownNewField()
        {
            var writer = new ByteWriter(64);
            NewMsgPacketCodec.Write(ref writer, new NewMsg(9, "bob", 42));

            var reader = new ByteReader(writer.Written);
            var decoded = OldMsgPacketCodec.Read(ref reader);

            Assert.Equal(9, decoded.Id);
            Assert.Equal("bob", decoded.Name); // unknown id 3 skipped
        }

        [Fact]
        public void RenamedMember_IsWireCompatible()
        {
            var writer = new ByteWriter(64);
            OldMsgPacketCodec.Write(ref writer, new OldMsg(3, "carol"));

            var reader = new ByteReader(writer.Written);
            var decoded = RenamedMsgPacketCodec.Read(ref reader);

            Assert.Equal(3, decoded.Id);
            Assert.Equal("carol", decoded.DisplayName); // same id 2, different name
        }

        [Fact]
        public void TypeTagMismatch_IsRejected()
        {
            // Field 1 is int (tag 4), but the payload claims a string (tag 8).
            var writer = new ByteWriter(32);
            writer.WriteVarUInt(1);
            writer.WriteByte(8);
            var text = Encoding.UTF8.GetBytes("boom");
            writer.WriteVarUInt((uint)text.Length);
            writer.WriteBytes(text);

            var reader = new ByteReader(writer.Written);
            var threw = false;
            try { OldMsgPacketCodec.Read(ref reader); }
            catch (InvalidOperationException ex) { threw = true; Assert.Contains("expects tag", ex.Message); }
            Assert.True(threw);
        }

        [Fact]
        public void SchemaHash_ChangesOnNewField_ButNotOnRename()
        {
            Assert.NotEqual(OldMsgPacketCodec.SchemaHash, NewMsgPacketCodec.SchemaHash);
            Assert.Equal(OldMsgPacketCodec.SchemaHash, RenamedMsgPacketCodec.SchemaHash);
        }
    }
}
