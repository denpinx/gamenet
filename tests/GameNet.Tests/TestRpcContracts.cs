using GameNet.Core;

namespace GameNet.Tests;

/// <summary>Wire contracts used by the end-to-end RPC test (moved out of the framework).</summary>
public static class RpcMethods
{
    public const ushort CreateHero = 1;
    public const ushort Ping = 2;
}

public sealed record CreateHeroRequest(string Name, int TeamId)
{
    public void Write(ByteWriter writer)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Name);
        writer.WriteVarUInt((uint)bytes.Length);
        writer.WriteBytes(bytes);
        writer.WriteVarInt(TeamId);
    }

    public static CreateHeroRequest Read(ByteReader reader)
    {
        var length = (int)reader.ReadVarUInt();
        var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));
        var team = reader.ReadVarInt();
        return new CreateHeroRequest(name, team);
    }
}

public sealed record CreateHeroResponse(int EntityId, float X, float Y)
{
    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(EntityId);
        writer.WriteFloat(X);
        writer.WriteFloat(Y);
    }

    public static CreateHeroResponse Read(ByteReader reader) =>
        new(reader.ReadVarInt(), reader.ReadFloat(), reader.ReadFloat());
}
