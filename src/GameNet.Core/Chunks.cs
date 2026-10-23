namespace GameNet.Core;

/// <summary>Chunk coordinate (Minecraft-style columns / sections).</summary>
public readonly record struct ChunkKey(int X, int Z);

public sealed class ChunkOptions
{
    /// <summary>Square view radius in chunks (8 => 17x17 = 289 chunks).</summary>
    public int ViewRadius { get; set; } = 8;

    public int ChunkPayloadBytes { get; set; } = 1024;
    public int EditsPerSecond { get; set; } = 200;

    /// <summary>How fast a simulated player crosses chunk borders.</summary>
    public float ChunksPerSecond { get; set; } = 2f;
    public int WorldChunks { get; set; } = 256;
}

public enum ChunkMessageType : byte
{
    Full = 20,
    Delta = 21,
    Unload = 22,
}

public readonly record struct BlockEdit(ushort Index, ushort Block)
{
}

/// <summary>Client-side chunk cache: full chunks + block-edit deltas + unloads.</summary>
public sealed class ChunkWorld
{
    private readonly Dictionary<ChunkKey, ChunkView> _chunks = new();

    public int LoadedChunks => _chunks.Count;
    public long FullChunks { get; private set; }
    public long DeltaChunks { get; private set; }
    public long Unloads { get; private set; }
    public long AppliedEdits { get; private set; }

    public bool TryGet(ChunkKey key, out byte[] data, out uint version)
    {
        if (_chunks.TryGetValue(key, out var view))
        {
            data = view.Data;
            version = view.Version;
            return true;
        }
        data = Array.Empty<byte>();
        version = 0;
        return false;
    }

    public int Apply(ReadOnlySpan<byte> payload)
    {
        var reader = new ByteReader(payload);
        var type = (ChunkMessageType)reader.ReadByte();
        var key = new ChunkKey(reader.ReadVarInt(), reader.ReadVarInt());
        switch (type)
        {
            case ChunkMessageType.Full:
            {
                var version = reader.ReadVarUInt();
                var length = (int)reader.ReadVarUInt();
                var data = reader.ReadBytes(length).ToArray();
                _chunks[key] = new ChunkView(version, data);
                FullChunks++;
                return 1;
            }
            case ChunkMessageType.Delta:
            {
                var version = reader.ReadVarUInt();
                var count = (int)reader.ReadVarUInt();
                if (!_chunks.TryGetValue(key, out var view)) return 0;
                for (var i = 0; i < count; i++)
                {
                    var index = (int)reader.ReadVarUInt();
                    var block = (ushort)reader.ReadVarUInt();
                    if (index >= 0 && index < view.Data.Length) view.Data[index] = (byte)block;
                }
                view.Version = version;
                DeltaChunks++;
                AppliedEdits += count;
                return count;
            }
            case ChunkMessageType.Unload:
                _chunks.Remove(key);
                Unloads++;
                return 1;
            default:
                return 0;
        }
    }

    private sealed class ChunkView
    {
        public ChunkView(uint version, byte[] data)
        {
            Version = version;
            Data = data;
        }

        public uint Version;
        public byte[] Data;
    }
}

public sealed class ChunkMetrics
{
    private readonly List<double> _ticks = new();
    public long PacketsSent { get; private set; }
    public long BytesSent { get; private set; }
    public long FullChunksSent { get; private set; }
    public long DeltasSent { get; private set; }
    public long Edits { get; private set; }
    public int LoadedChunks { get; private set; }

    public void RecordTick(double ms, long bytes, long packets, long full, long deltas, int loaded)
    {
        _ticks.Add(ms);
        BytesSent += bytes;
        PacketsSent += packets;
        FullChunksSent += full;
        DeltasSent += deltas;
        LoadedChunks = loaded;
    }

    public void AddEdits(long count) => Edits += count;

    public void Reset()
    {
        _ticks.Clear();
        PacketsSent = 0;
        BytesSent = 0;
        FullChunksSent = 0;
        DeltasSent = 0;
        Edits = 0;
    }

    public (double Avg, double P50, double P95, double P99, double Max) Summarize()
    {
        if (_ticks.Count == 0) return (0, 0, 0, 0, 0);
        var sorted = _ticks.OrderBy(x => x).ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        return (sorted.Average(), At(0.50), At(0.95), At(0.99), sorted[^1]);
    }
}

/// <summary>
/// Chunk replication: sends full chunks when they enter view, block-edit deltas while they
/// stay visible, and unload notices when they leave. Interest = square radius in chunks.
/// </summary>
public sealed class ChunkServer
{
    private readonly IServerTransport _transport;
    private readonly Dictionary<int, ChunkSession> _sessions = new();
    private readonly Dictionary<ChunkKey, ChunkEntry> _chunks = new();
    private readonly Random _random = new(7);
    private double _editAccumulator;

    public ChunkServer(IServerTransport transport, ChunkOptions? options = null)
    {
        _transport = transport;
        Options = options ?? new ChunkOptions();
    }

    public ChunkOptions Options { get; }
    public ChunkMetrics Metrics { get; } = new();
    public IReadOnlyDictionary<int, ChunkSession> Sessions => _sessions;

    public void Start()
    {
        _transport.PeerConnected += OnConnected;
        _transport.PeerDisconnected += OnDisconnected;
        _transport.Start();
    }

    private void OnConnected(IPeer peer) => _sessions[peer.Id] = new ChunkSession(peer);

    private void OnDisconnected(IPeer peer) => _sessions.Remove(peer.Id);

    public void Tick(float deltaSeconds)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _transport.Poll();

        var sessions = _sessions.Values.ToList();
        foreach (var session in sessions)
        {
            // Drain control packets (subscribing is implicit here).
            while (session.Peer.TryDequeue(out _)) { }
            AdvanceAvatar(session, deltaSeconds);
        }

        SimulateEdits(deltaSeconds, sessions);

        long bytes = 0, packets = 0, full = 0, deltas = 0;
        var loaded = 0;
        foreach (var session in sessions)
        {
            Replicate(session, ref bytes, ref packets, ref full, ref deltas);
            loaded += session.Known.Count;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Metrics.RecordTick(elapsed, bytes, packets, full, deltas, sessions.Count == 0 ? 0 : loaded / sessions.Count);
    }

    private void AdvanceAvatar(ChunkSession session, float dt)
    {
        var radius = Options.WorldChunks * 0.25f;
        session.Angle += dt * Options.ChunksPerSecond / MathF.Max(1f, radius);
        session.ChunkX = (int)(Options.WorldChunks * 0.5f + MathF.Cos(session.Angle) * radius);
        session.ChunkZ = (int)(Options.WorldChunks * 0.5f + MathF.Sin(session.Angle) * radius);
    }

    private void SimulateEdits(float dt, List<ChunkSession> sessions)
    {
        if (sessions.Count == 0) return;
        _editAccumulator += Options.EditsPerSecond * dt;
        var edits = (int)_editAccumulator;
        _editAccumulator -= edits;

        for (var i = 0; i < edits; i++)
        {
            var session = sessions[_random.Next(sessions.Count)];
            var key = new ChunkKey(
                session.ChunkX + _random.Next(-2, 3),
                session.ChunkZ + _random.Next(-2, 3));
            var entry = GetOrCreate(key);
            entry.Version++;
            entry.History.Add((entry.Version, (ushort)_random.Next(4096), (ushort)_random.Next(256)));
            if (entry.History.Count > 256) entry.History.RemoveAt(0);
            Metrics.AddEdits(1);
        }
    }

    private void Replicate(ChunkSession session, ref long bytes, ref long packets, ref long full, ref long deltas)
    {
        var radius = Options.ViewRadius;
        var visible = new HashSet<ChunkKey>();
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                visible.Add(new ChunkKey(session.ChunkX + dx, session.ChunkZ + dz));
            }
        }

        foreach (var key in visible)
        {
            var entry = GetOrCreate(key);
            if (!session.Known.TryGetValue(key, out var knownVersion))
            {
                var message = BuildFull(key, entry);
                if (session.Peer.Send(0, message))
                {
                    session.Known[key] = entry.Version;
                    bytes += message.Length;
                    packets++;
                    full++;
                }
                continue;
            }

            if (entry.Version == knownVersion) continue;

            var pending = entry.History.Where(h => h.Version > knownVersion).ToList();
            if (pending.Count == 0 || entry.History.Count > 0 && entry.History[0].Version > knownVersion + 1)
            {
                var message = BuildFull(key, entry);
                if (session.Peer.Send(0, message))
                {
                    session.Known[key] = entry.Version;
                    bytes += message.Length;
                    packets++;
                    full++;
                }
                continue;
            }

            var delta = BuildDelta(key, entry.Version, pending);
            if (session.Peer.Send(0, delta))
            {
                session.Known[key] = entry.Version;
                bytes += delta.Length;
                packets++;
                deltas++;
            }
        }

        foreach (var key in session.Known.Keys.ToList())
        {
            if (visible.Contains(key)) continue;
            var unload = BuildUnload(key);
            if (session.Peer.Send(0, unload))
            {
                session.Known.Remove(key);
                bytes += unload.Length;
                packets++;
            }
        }
    }

    private ChunkEntry GetOrCreate(ChunkKey key)
    {
        if (_chunks.TryGetValue(key, out var entry)) return entry;
        entry = new ChunkEntry { Version = 1, Payload = GeneratePayload(key) };
        _chunks[key] = entry;
        return entry;
    }

    private byte[] GeneratePayload(ChunkKey key)
    {
        var size = Options.ChunkPayloadBytes;
        var payload = new byte[size];
        var state = (uint)(key.X * 73856093 ^ key.Z * 19349663) | 1;
        for (var i = 0; i < size; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[i] = (byte)state;
        }
        return payload;
    }

    private static byte[] BuildFull(ChunkKey key, ChunkEntry entry)
    {
        var writer = new ByteWriter(entry.Payload.Length + 32);
        writer.WriteByte((byte)ChunkMessageType.Full);
        writer.WriteVarInt(key.X);
        writer.WriteVarInt(key.Z);
        writer.WriteVarUInt(entry.Version);
        writer.WriteVarUInt((uint)entry.Payload.Length);
        writer.WriteBytes(entry.Payload);
        return writer.ToArray();
    }

    private static byte[] BuildDelta(ChunkKey key, uint version, List<(uint Version, ushort Index, ushort Block)> edits)
    {
        var writer = new ByteWriter(32 + edits.Count * 8);
        writer.WriteByte((byte)ChunkMessageType.Delta);
        writer.WriteVarInt(key.X);
        writer.WriteVarInt(key.Z);
        writer.WriteVarUInt(version);
        writer.WriteVarUInt((uint)edits.Count);
        foreach (var (_, index, block) in edits)
        {
            writer.WriteVarUInt(index);
            writer.WriteVarUInt(block);
        }
        return writer.ToArray();
    }

    private static byte[] BuildUnload(ChunkKey key)
    {
        var writer = new ByteWriter(8);
        writer.WriteByte((byte)ChunkMessageType.Unload);
        writer.WriteVarInt(key.X);
        writer.WriteVarInt(key.Z);
        return writer.ToArray();
    }

    private sealed class ChunkEntry
    {
        public uint Version;
        public byte[] Payload = Array.Empty<byte>();
        public List<(uint Version, ushort Index, ushort Block)> History = new();
    }

    public sealed class ChunkSession
    {
        public ChunkSession(IPeer peer) => Peer = peer;
        public IPeer Peer { get; }
        public int ChunkX;
        public int ChunkZ;
        public float Angle;
        public Dictionary<ChunkKey, uint> Known { get; } = new();
    }
}
