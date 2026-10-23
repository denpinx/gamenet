using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameNet.Core;
using GameNet.Transport;

var options = ArgMap.Parse(args);

if (options.Has("serve"))
{
    await RunServerAsync(options);
    return;
}

if (options.Has("client"))
{
    await RunClientAsync(options);
    return;
}

if (options.Has("chunks"))
{
    RunChunkBenchmarkAsync(options).GetAwaiter().GetResult();
    return;
}

await RunOrchestratorAsync(options);

// ------------------------------------------------------------------ orchestrator

static async Task RunOrchestratorAsync(ArgMap options)
{
    var quick = options.Has("quick");
    var measureSeconds = quick ? 2 : 8;
    var warmupSeconds = quick ? 1 : 2;
    var clientCount = options.Int("clients", 10);
    var impairment = ImpairmentOf(options);
    var orderedState = options.Get("ordered-state", "0") == "1";

    var scenarios = new List<Scenario>
    {
        new(50, true, "AOI on"),
        new(200, true, "AOI on"),
        new(1000, true, "AOI on"),
        new(5000, true, "AOI on"),
        new(20000, true, "AOI on"),
        new(200, false, "AOI off"),
        new(1000, false, "AOI off"),
    };
    if (options.Has("aoi-only")) scenarios.RemoveAll(s => !s.Aoi);
    if (options.Has("objects"))
    {
        var count = options.Int("objects", 1000);
        scenarios = new List<Scenario> { new(count, options.Get("aoi", "1") == "1", options.Get("aoi", "1") == "1" ? "AOI on" : "AOI off") };
    }

    var results = new List<BenchResult>();
    foreach (var scenario in scenarios)
    {
        if (results.Count > 0) await Task.Delay(500); // let sockets/ports settle between scenarios
        Console.WriteLine($"== objects={scenario.Objects} {scenario.Label} (2 processes) ==");
        var result = await RunScenarioAsync(scenario, measureSeconds, warmupSeconds, clientCount, impairment, orderedState);
        if (result.DownstreamKBps <= 0)
        {
            Console.WriteLine("   no data received — retrying once");
            await Task.Delay(1000);
            result = await RunScenarioAsync(scenario, measureSeconds, warmupSeconds, clientCount, impairment, orderedState);
        }
        results.Add(result);
        Console.WriteLine(
            $"   tick avg={result.TickAvg:F2}ms p95={result.TickP95:F2}ms max={result.TickMax:F2}ms | " +
            $"visible={result.VisibleAvg:F0} | down={result.DownstreamKBps:F1}KB/s/client | " +
            $"total={result.TotalMbit:F2}Mbit/s ({result.WireMbit:F2}Mbit/s wire) blocked={result.WindowBlocked} retx={result.Retransmits} fast={result.FastRetransmits} lag50={result.LagP50:F0}ms lag99={result.LagP99:F0}ms");
    }

    WriteReports(results);
}

static async Task<BenchResult> RunScenarioAsync(Scenario scenario, int measureSeconds, int warmupSeconds, int clientCount, ImpairmentOptions impairment, bool orderedState)
{
    var serverOut = Path.Combine(Path.GetTempPath(), $"gn-server-{Guid.NewGuid():N}.json");
    var clientOut = Path.Combine(Path.GetTempPath(), $"gn-client-{Guid.NewGuid():N}.json");

    using var server = StartChild(new[]
    {
        "--serve", "--objects", scenario.Objects.ToString(),
        "--aoi", scenario.Aoi ? "1" : "0",
        "--warmup", warmupSeconds.ToString(),
        "--measure", measureSeconds.ToString(),
        "--clients", clientCount.ToString(),
        "--loss", impairment.Loss.ToString(CultureInfo.InvariantCulture),
        "--latency", impairment.LatencyMs.ToString(),
        "--jitter", impairment.JitterMs.ToString(),
        "--dup", impairment.DuplicateRate.ToString(CultureInfo.InvariantCulture),
        "--redundancy", impairment.StateRedundancy.ToString(),
        "--ordered", orderedState ? "1" : "0",
        "--out", serverOut,
    });

    int port;
    try
    {
        port = await WaitForReadyAsync(server, TimeSpan.FromSeconds(20));
    }
    catch
    {
        try { server.Kill(true); } catch { }
        throw;
    }

    using var client = StartChild(new[]
    {
        "--client", "--port", port.ToString(),
        "--warmup", warmupSeconds.ToString(),
        "--measure", measureSeconds.ToString(),
        "--clients", clientCount.ToString(),
        "--loss", impairment.Loss.ToString(CultureInfo.InvariantCulture),
        "--latency", impairment.LatencyMs.ToString(),
        "--jitter", impairment.JitterMs.ToString(),
        "--dup", impairment.DuplicateRate.ToString(CultureInfo.InvariantCulture),
        "--redundancy", impairment.StateRedundancy.ToString(),
        "--ordered", orderedState ? "1" : "0",
        "--out", clientOut,
    });

    await WaitExitAsync(client, TimeSpan.FromSeconds(warmupSeconds + measureSeconds + 40));
    await WaitExitAsync(server, TimeSpan.FromSeconds(15));

    var serverReport = JsonSerializer.Deserialize<ServerReport>(File.ReadAllText(serverOut))
                       ?? throw new InvalidOperationException("missing server report");
    var clientReport = JsonSerializer.Deserialize<ClientReport>(File.ReadAllText(clientOut))
                       ?? throw new InvalidOperationException("missing client report");

    TryDelete(serverOut);
    TryDelete(clientOut);

    return new BenchResult
    {
        Objects = scenario.Objects,
        Mode = scenario.Label,
        Clients = clientReport.Clients,
        TickAvg = serverReport.TickAvg,
        TickP50 = serverReport.TickP50,
        TickP95 = serverReport.TickP95,
        TickP99 = serverReport.TickP99,
        TickMax = serverReport.TickMax,
        VisibleAvg = serverReport.VisibleAvg,
        DownstreamKBps = clientReport.BytesReceived / (double)measureSeconds / 1024.0 / clientReport.Clients,
        TotalMbit = serverReport.PayloadBytes * 8.0 / measureSeconds / 1_000_000.0,
        WireMbit = (serverReport.PayloadBytes + serverReport.Datagrams * 40) * 8.0 / measureSeconds / 1_000_000.0,
        UpstreamKBps = clientReport.BytesSent / (double)measureSeconds / 1024.0,
        ClientApplyAvg = clientReport.ApplyMicrosAvg,
        ClientEntitiesAvg = clientReport.EntitiesAvg,
        LagP50 = clientReport.LagP50,
        LagP99 = clientReport.LagP99,
        LagAvg = clientReport.LagAvg,
        Datagrams = serverReport.Datagrams,
        WindowBlocked = serverReport.WindowBlocked,
        Retransmits = serverReport.Retransmits,
        FastRetransmits = serverReport.FastRetransmits,
    };
}

static Process StartChild(string[] arguments)
{
    var executable = Environment.ProcessPath ?? "dotnet";
    var startInfo = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        startInfo.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);

    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

    var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start child process");
    process.ErrorDataReceived += (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("[child] " + e.Data);
    };
    process.BeginErrorReadLine();
    return process;
}

static async Task<int> WaitForReadyAsync(Process process, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var lineTask = process.StandardOutput.ReadLineAsync();
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) break;
        if (await Task.WhenAny(lineTask, Task.Delay(remaining)) != lineTask) break;

        var line = await lineTask;
        if (line is null) break;
        if (line.StartsWith("READY ", StringComparison.Ordinal))
            return int.Parse(line[6..], CultureInfo.InvariantCulture);
    }
    throw new TimeoutException("server child did not report READY");
}

static async Task WaitExitAsync(Process process, TimeSpan timeout)
{
    var exitTask = process.WaitForExitAsync();
    if (await Task.WhenAny(exitTask, Task.Delay(timeout)) != exitTask)
    {
        try { process.Kill(true); } catch { }
        throw new TimeoutException($"child process {process.Id} did not exit in {timeout}");
    }
}

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { }
}

// ------------------------------------------------------------------ server child

static async Task RunServerAsync(ArgMap options)
{
    const int tickRate = 30;
    var objects = options.Int("objects", 200);
    var clientCount = options.Int("clients", 10);
    var aoi = options.Get("aoi", "1") == "1";
    var warmupSeconds = options.Int("warmup", 2);
    var measureSeconds = options.Int("measure", 8);
    var outPath = options.Get("out", Path.Combine(Path.GetTempPath(), "gn-server.json"));

    var ordered = options.Get("ordered", "0") == "1";
    using var transport = new UdpServerTransport(0, ImpairmentOf(options), unorderedState: !ordered);
    var server = new GameServer(transport, new ServerOptions
    {
        AoiRadius = aoi ? 60f : 0f,
        AutoSubscribe = true,
    });
    server.Start();

    var random = new Random(1234 + objects);
    var side = MathF.Max(200f, MathF.Sqrt(objects * 100f));
    server.Options.ArenaSize = side;

    for (var i = 0; i < clientCount; i++)
    {
        var x = (float)random.NextDouble() * side;
        var y = (float)random.NextDouble() * side;
        server.Entities.Spawn(EntityState.Create(i + 1, EntityKind.Player, x, y, 15f, 5f, 0, 100, (byte)(i % 2)));
    }

    for (var i = 0; i < objects; i++)
    {
        var x = (float)random.NextDouble() * side;
        var y = (float)random.NextDouble() * side;
        var moving = i % 3 == 0;
        var vx = moving ? (float)(random.NextDouble() * 4 - 2) : 0f;
        var vy = moving ? (float)(random.NextDouble() * 4 - 2) : 0f;
        server.Entities.Spawn(EntityState.Create(1000 + i, EntityKind.Prop, x, y, vx, vy, (float)random.NextDouble() * 6.28f));
    }

    Console.WriteLine($"READY {transport.Port}");
    Console.Out.Flush();

    var totalTicks = (warmupSeconds + measureSeconds) * tickRate + tickRate; // +1s grace for clients
    var interval = 1000.0 / tickRate;
    var pacing = Stopwatch.StartNew();
    var nextAt = 0.0;
    long datagramBaseline = 0;

    for (var tick = 0; tick < totalTicks; tick++)
    {
        server.Tick(1f / tickRate);
        if (tick == warmupSeconds * tickRate)
        {
            server.Metrics.Reset();
            datagramBaseline = Datagrams(server);
        }
        nextAt += interval;
        var wait = nextAt - pacing.Elapsed.TotalMilliseconds;
        if (wait > 0) Thread.Sleep((int)wait);
    }

    await Task.Delay(500); // let clients drain the last snapshots

    var (avg, p50, p95, p99, max) = server.Metrics.Summarize();
    var report = new ServerReport
    {
        TickAvg = avg,
        TickP50 = p50,
        TickP95 = p95,
        TickP99 = p99,
        TickMax = max,
        PayloadBytes = server.Metrics.BytesSent,
        Datagrams = Math.Max(0, Datagrams(server) - datagramBaseline),
        VisibleAvg = server.Sessions.Values.Select(s => s.VisibleCount).DefaultIfEmpty(0).Average(),
        Sessions = server.Sessions.Count,
        WindowBlocked = server.Sessions.Values.Sum(s => s.Peer is UdpPeer peer ? peer.WindowBlocked : 0),
        Retransmits = server.Sessions.Values.Sum(s => s.Peer is UdpPeer peer ? peer.Retransmits : 0),
        FastRetransmits = server.Sessions.Values.Sum(s => s.Peer is UdpPeer peer ? peer.FastRetransmits : 0),
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(report));
}

static long Datagrams(GameServer server) =>
    server.Sessions.Values.Sum(s => s.Peer is UdpPeer peer ? peer.DatagramsSent : 0);

// ------------------------------------------------------------------ client child

static async Task RunClientAsync(ArgMap options)
{
    var clientCount = options.Int("clients", 10);
    var ordered = options.Get("ordered", "0") == "1";
    var port = options.Int("port", 0);
    var warmupSeconds = options.Int("warmup", 2);
    var measureSeconds = options.Int("measure", 8);
    var outPath = options.Get("out", Path.Combine(Path.GetTempPath(), "gn-client.json"));

    var clients = new List<SimClient>();
    try
    {
        for (var i = 0; i < clientCount; i++)
        {
            var client = new SimClient(i, ImpairmentOf(options), !ordered);
            if (!client.Connect("127.0.0.1", port))
                throw new InvalidOperationException($"client {i} failed to connect to {port}");
            clients.Add(client);
        }

        foreach (var client in clients) client.Bind(client.Index + 1);
        foreach (var client in clients) client.Start();

        var warmupUntil = DateTime.UtcNow.AddSeconds(warmupSeconds);
        while (DateTime.UtcNow < warmupUntil)
        {
            foreach (var client in clients) client.Drain();
            await Task.Delay(10);
        }

        foreach (var client in clients) client.ResetStats();
        var measureUntil = DateTime.UtcNow.AddSeconds(measureSeconds);
        while (DateTime.UtcNow < measureUntil)
        {
            foreach (var client in clients) client.Drain();
            await Task.Delay(10);
        }

        var samples = clients.SelectMany(c => c.ApplySamples).ToList();
        var lags = clients.SelectMany(c => c.LagSamples).OrderBy(x => x).ToArray();
        double At(double q) => lags.Length == 0 ? 0 : lags[Math.Min(lags.Length - 1, (int)(q * lags.Length))];
        var report = new ClientReport
        {
            Clients = clients.Count,
            BytesReceived = clients.Sum(c => c.BytesReceived),
            BytesSent = clients.Sum(c => c.BytesSent),
            EntitiesAvg = clients.Average(c => c.EntityCount),
            ApplyMicrosAvg = samples.Count > 0 ? samples.Average() : 0,
            LagP50 = At(0.50),
            LagP99 = At(0.99),
            LagAvg = lags.Length > 0 ? lags.Average() : 0,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(report));
    }
    finally
    {
        foreach (var client in clients) client.Dispose();
    }
}

// ------------------------------------------------------------------ chunk benchmark

static Task RunChunkBenchmarkAsync(ArgMap options)
{
    var clients = options.Int("clients", 10);
    var world = options.Int("world", 256);
    var view = options.Int("view", 8);
    var chunkBytes = options.Int("chunk-bytes", 1024);
    var edits = options.Int("edits", 200);
    var warmupSeconds = options.Int("warmup", 2);
    var measureSeconds = options.Int("measure", 8);
    const int tickRate = 30;

    var ordered = options.Get("ordered", "0") == "1";
    using var transport = new UdpServerTransport(0, ImpairmentOf(options), unorderedState: !ordered);
    var server = new ChunkServer(transport, new ChunkOptions
    {
        WorldChunks = world,
        ViewRadius = view,
        ChunkPayloadBytes = chunkBytes,
        EditsPerSecond = edits,
    });
    server.Start();

    var chunkClients = new List<ChunkClient>();
    try
    {
        for (var i = 0; i < clients; i++)
        {
            var client = new ChunkClient();
            if (!client.Connect("127.0.0.1", transport.Port))
                throw new InvalidOperationException($"chunk client {i} failed to connect");
            client.SendSubscribe();
            chunkClients.Add(client);
        }

        var interval = 1000.0 / tickRate;
        var pacing = Stopwatch.StartNew();
        var nextAt = 0.0;

        void RunTicks(int count)
        {
            for (var i = 0; i < count; i++)
            {
                server.Tick(1f / tickRate);
                foreach (var client in chunkClients) client.Pump();
                nextAt += interval;
                var wait = nextAt - pacing.Elapsed.TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }
        }

        RunTicks(warmupSeconds * tickRate);
        var joinBytes = chunkClients.Sum(c => c.BytesReceived);
        server.Metrics.Reset();
        var baseline = chunkClients.Select(c => c.BytesReceived).ToArray();

        RunTicks(measureSeconds * tickRate);

        var steadyBytes = 0L;
        for (var i = 0; i < chunkClients.Count; i++)
            steadyBytes += chunkClients[i].BytesReceived - baseline[i];

        var (avg, p50, p95, p99, max) = server.Metrics.Summarize();
        var loaded = chunkClients.Average(c => c.World.LoadedChunks);
        var fullPerSec = server.Metrics.FullChunksSent / (double)measureSeconds;
        var deltaPerSec = server.Metrics.DeltasSent / (double)measureSeconds;
        var editsPerSec = server.Metrics.Edits / (double)measureSeconds;
        var downKBs = steadyBytes / (double)measureSeconds / 1024.0 / clients;
        var totalMbit = server.Metrics.BytesSent * 8.0 / measureSeconds / 1_000_000.0;
        var joinMB = joinBytes / (double)clients / 1024 / 1024;

        Console.WriteLine();
        Console.WriteLine($"chunk world {world}x{world}, view {view} ({2 * view + 1}^2 = {(2 * view + 1) * (2 * view + 1)} chunks), payload {chunkBytes} B, edits {edits}/s, {clients} clients");
        Console.WriteLine($"  loaded/client={loaded:F0}  join={joinMB:F2} MB/client  steady={downKBs:F1} KB/s/client  total={totalMbit:F2} Mbit/s");
        Console.WriteLine($"  tick avg={avg:F2} ms p95={p95:F2} p99={p99:F2} max={max:F2} | full/s={fullPerSec:F0} delta/s={deltaPerSec:F0} edits/s={editsPerSec:F0}");

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results"));
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(directory, $"chunk-{stamp}.md");
        File.WriteAllText(path,
            $"# Chunk replication benchmark\n\n" +
            $"- world: {world}x{world} chunks\n- view radius: {view} chunks ({(2 * view + 1) * (2 * view + 1)} loaded per player)\n" +
            $"- payload: {chunkBytes} B/chunk\n- edits: {edits}/s\n- clients: {clients}\n\n" +
            $"| metric | value |\n|---|---:|\n" +
            $"| loaded chunks / client | {loaded:F0} |\n" +
            $"| join burst | {joinMB:F2} MB / client |\n" +
            $"| steady downstream | {downKBs:F1} KB/s / client |\n" +
            $"| steady total | {totalMbit:F2} Mbit/s |\n" +
            $"| server tick avg / p99 | {avg:F2} / {p99:F2} ms |\n" +
            $"| full chunks sent | {fullPerSec:F0} /s |\n" +
            $"| deltas sent | {deltaPerSec:F0} /s |\n" +
            $"| edits applied | {editsPerSec:F0} /s |\n");
        Console.WriteLine($"  report: {path}");
    }
    finally
    {
        foreach (var client in chunkClients) client.Dispose();
        transport.Stop();
    }
    return Task.CompletedTask;
}
// ------------------------------------------------------------------ reporting

static void WriteReports(List<BenchResult> results)
{
    var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results"));
    Directory.CreateDirectory(directory);

    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var csvPath = Path.Combine(directory, $"bench-{stamp}.csv");
    var mdPath = Path.Combine(directory, $"bench-{stamp}.md");

    var csv = new StringBuilder();
    csv.AppendLine("objects,mode,clients,tick_avg_ms,tick_p50_ms,tick_p95_ms,tick_p99_ms,tick_max_ms,visible_avg,down_kBps_client,total_mbit,wire_mbit,up_kBps,client_apply_us,client_entities,datagrams,window_blocked,retransmits,fast_retransmits,lag_p50_ms,lag_p99_ms,lag_avg_ms");
    foreach (var r in results)
    {
        csv.AppendLine(string.Join(',',
            r.Objects, r.Mode, r.Clients,
            r.TickAvg.ToString("F3", CultureInfo.InvariantCulture),
            r.TickP50.ToString("F3", CultureInfo.InvariantCulture),
            r.TickP95.ToString("F3", CultureInfo.InvariantCulture),
            r.TickP99.ToString("F3", CultureInfo.InvariantCulture),
            r.TickMax.ToString("F3", CultureInfo.InvariantCulture),
            r.VisibleAvg.ToString("F1", CultureInfo.InvariantCulture),
            r.DownstreamKBps.ToString("F1", CultureInfo.InvariantCulture),
            r.TotalMbit.ToString("F3", CultureInfo.InvariantCulture),
            r.WireMbit.ToString("F3", CultureInfo.InvariantCulture),
            r.UpstreamKBps.ToString("F1", CultureInfo.InvariantCulture),
            r.ClientApplyAvg.ToString("F1", CultureInfo.InvariantCulture),
            r.ClientEntitiesAvg.ToString("F1", CultureInfo.InvariantCulture),
            r.Datagrams,
            r.WindowBlocked,
            r.Retransmits,
            r.FastRetransmits,
            r.LagP50.ToString("F1", CultureInfo.InvariantCulture),
            r.LagP99.ToString("F1", CultureInfo.InvariantCulture),
            r.LagAvg.ToString("F1", CultureInfo.InvariantCulture)));
    }
    File.WriteAllText(csvPath, csv.ToString());

    var md = new StringBuilder();
    md.AppendLine("# 10-player FPS replication benchmark (server and clients in separate processes)");
    md.AppendLine();
    md.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    md.AppendLine("Server tick 30 Hz, 10 client processes over UDP loopback, constant object density (100 u² per object), AOI radius 60 when enabled.");
    md.AppendLine();
    md.AppendLine("| objects | mode | tick avg (ms) | p95 (ms) | p99 (ms) | max (ms) | visible/client | entities/client | down KB/s per client | total Mbit/s | wire Mbit/s | client apply (µs) | lag p50 (ms) | lag p99 (ms) |");
    md.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var r in results)
    {
        md.AppendLine($"| {r.Objects} | {r.Mode} | {r.TickAvg:F2} | {r.TickP95:F2} | {r.TickP99:F2} | {r.TickMax:F2} | {r.VisibleAvg:F0} | {r.ClientEntitiesAvg:F0} | {r.DownstreamKBps:F1} | {r.TotalMbit:F2} | {r.WireMbit:F2} | {r.ClientApplyAvg:F0} | {r.LagP50:F0} | {r.LagP99:F0} |");
    }
    md.AppendLine();
    md.AppendLine("Notes:");
    md.AppendLine("- `total_mbit` counts protocol payload only; `wire_mbit` adds ~40 B per datagram (IP 20 + UDP 8 + frame 12).");
    md.AppendLine("- Server and clients are separate OS processes on the same machine; throughput shares one CPU.");
    md.AppendLine("- `AOI off` rows show the cost of unfiltered replication and are not run at 20k objects.");
    File.WriteAllText(mdPath, md.ToString());

    Console.WriteLine();
    Console.WriteLine(md.ToString());
    Console.WriteLine($"CSV: {csvPath}");
    Console.WriteLine($"MD : {mdPath}");
}

static ImpairmentOptions ImpairmentOf(ArgMap options) => new()
{
    Loss = options.Double("loss", 0),
    LatencyMs = options.Int("latency", 0),
    JitterMs = options.Int("jitter", 0),
    DuplicateRate = options.Double("dup", 0),
    StateRedundancy = options.Int("redundancy", 1),
};

// ------------------------------------------------------------------ types

internal sealed class ArgMap
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ArgMap Parse(string[] args)
    {
        var map = new ArgMap();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map._values[key] = args[++i];
            }
            else
            {
                map._values[key] = null;
            }
        }
        return map;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string Get(string key, string fallback) =>
        _values.TryGetValue(key, out var value) && value is not null ? value : fallback;

    public int Int(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public double Double(string key, double fallback) =>
        _values.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}

internal sealed record Scenario(int Objects, bool Aoi, string Label);

internal sealed class ServerReport
{
    public double TickAvg { get; set; }
    public double TickP50 { get; set; }
    public double TickP95 { get; set; }
    public double TickP99 { get; set; }
    public double TickMax { get; set; }
    public long PayloadBytes { get; set; }
    public long Datagrams { get; set; }
    public double VisibleAvg { get; set; }
    public int Sessions { get; set; }
    public long WindowBlocked { get; set; }
    public long Retransmits { get; set; }
    public long FastRetransmits { get; set; }
}

internal sealed class ClientReport
{
    public int Clients { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public double EntitiesAvg { get; set; }
    public double ApplyMicrosAvg { get; set; }
    public double LagP50 { get; set; }
    public double LagP99 { get; set; }
    public double LagAvg { get; set; }
}

internal sealed class BenchResult
{
    public int Objects { get; init; }
    public string Mode { get; init; } = "";
    public int Clients { get; init; }
    public double TickAvg { get; init; }
    public double TickP50 { get; init; }
    public double TickP95 { get; init; }
    public double TickP99 { get; init; }
    public double TickMax { get; init; }
    public double VisibleAvg { get; init; }
    public double DownstreamKBps { get; init; }
    public double TotalMbit { get; init; }
    public double WireMbit { get; init; }
    public double UpstreamKBps { get; init; }
    public double ClientApplyAvg { get; init; }
    public double ClientEntitiesAvg { get; init; }
    public long Datagrams { get; init; }
    public long WindowBlocked { get; init; }
    public long Retransmits { get; init; }
    public long FastRetransmits { get; init; }
    public double LagP50 { get; init; }
    public double LagP99 { get; init; }
    public double LagAvg { get; init; }
}

internal sealed class SimClient : IDisposable
{
    private readonly UdpClientTransport _transport;
    private readonly ClientWorld _world = new();
    private readonly List<double> _applySamples = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _bytesBaseline;

    public SimClient(int index, ImpairmentOptions? impairment = null, bool unorderedState = true)
    {
        Index = index;
        _transport = new UdpClientTransport(impairment, unorderedState);
    }

    public int Index { get; }
    public int Id => _transport.Id;
    public long BytesReceived => _transport.BytesReceived - _bytesBaseline;
    public long BytesSent => _transport.BytesSent;
    public IReadOnlyList<double> ApplySamples => _applySamples;
    public int EntityCount => _world.EntityCount;
    public IReadOnlyList<int> LagSamples => _world.LagSamples;

    public bool Connect(string host, int port) => _transport.Connect(host, port, 5000);

    public void Bind(int entityId)
    {
        var bind = new ByteWriter(8);
        bind.WriteByte((byte)MessageType.BindRequest);
        bind.WriteVarInt(entityId);
        _transport.Send(1, bind.Written);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var phase = Index * 0.6;
            while (!token.IsCancellationRequested)
            {
                var angle = phase + Environment.TickCount64 / 1000.0;
                var input = new ByteWriter(16);
                input.WriteByte((byte)MessageType.Input);
                input.WriteFloat((float)Math.Cos(angle) * 15f);
                input.WriteFloat((float)Math.Sin(angle) * 15f);
                _transport.Send(2, input.Written);

                try
                {
                    await Task.Delay(33, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void Drain()
    {
        while (_transport.TryDequeue(out var packet))
        {
            if (packet.Channel != 0) continue;
            var started = Stopwatch.GetTimestamp();
            if (SnapshotCodec.TryRead(packet.Payload, out var chunk)) _world.OnChunk(chunk);
            _applySamples.Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        }

        if (_world.NeedsResync)
        {
            // Drop the stale world so the full baseline cannot resurrect invisible entities.
            _world.Clear();
            var writer = new ByteWriter(2);
            writer.WriteByte((byte)MessageType.Resync);
            _transport.Send(1, writer.Written);
            _world.AcknowledgeResync();
        }
    }

    public void ResetStats()
    {
        _bytesBaseline = _transport.BytesReceived;
        lock (_applySamples) _applySamples.Clear();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(500); } catch { }
        _transport.Dispose();
        _cts?.Dispose();
    }
}
