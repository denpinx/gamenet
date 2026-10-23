using System.IO.Compression;
using GameNet.Persistence;
using Xunit;

namespace GameNet.Tests;

public sealed class HeroStats
{
    [Key] public Guid Id { get; set; }
    public int Hp { get; set; }
    public int Mp { get; set; }
}

public sealed class HeroItem
{
    [Key] public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class HeroRow
{
    [Key] public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TeamId { get; set; }
    public HeroStats? Stats { get; set; }
    public List<HeroItem> Items { get; set; } = new();
}

public sealed class FlatRow
{
    [Key] public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
}

public class RelationalPersistenceTests : IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;

    public RelationalPersistenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"gamenet-rel-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_path}";
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void NestedSingleAndList_RoundTrip()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        store.EnsureSchema();

        var hero = new HeroRow
        {
            Id = Guid.NewGuid(),
            Name = "亚瑟",
            TeamId = 1,
            Stats = new HeroStats { Hp = 100, Mp = 50 },
            Items = new List<HeroItem>
            {
                new() { Name = "剑", Count = 1 },
                new() { Name = "药水", Count = 3 },
            },
        };
        store.Save(hero);

        var loaded = store.Load(hero.Id);
        Assert.NotNull(loaded);
        Assert.Equal("亚瑟", loaded!.Name);
        Assert.NotNull(loaded.Stats);
        Assert.Equal(100, loaded.Stats!.Hp);
        Assert.Equal(50, loaded.Stats.Mp);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("剑", loaded.Items[0].Name);
        Assert.Equal("药水", loaded.Items[1].Name);
        Assert.Equal(3, loaded.Items[1].Count);
    }

    [Fact]
    public void NestedTables_AreCreatedWithFkColumns()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        var schema = store.SchemaSql(SqlDialect.Sqlite);

        Assert.Contains(schema, s => s.Contains("\"HeroRow\"") && s.Contains("stats_id"));
        Assert.Contains(schema, s => s.Contains("\"HeroItem\"") && s.Contains("hero_row_id"));
        Assert.Contains(schema, s => s.Contains("\"HeroItem\"") && s.Contains("_index"));
    }

    [Fact]
    public void ListOrder_IsPreserved()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        store.EnsureSchema();

        var hero = new HeroRow { Id = Guid.NewGuid(), Name = "order" };
        for (var i = 0; i < 5; i++) hero.Items.Add(new HeroItem { Name = "item" + i, Count = i });
        store.Save(hero);

        var loaded = store.Load(hero.Id)!;
        Assert.Equal(new[] { "item0", "item1", "item2", "item3", "item4" }, loaded.Items.Select(i => i.Name));
    }

    [Fact]
    public void Delete_CascadesToNestedRows()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        store.EnsureSchema();
        var hero = new HeroRow
        {
            Id = Guid.NewGuid(),
            Stats = new HeroStats { Hp = 1 },
            Items = new List<HeroItem> { new() { Name = "x" } },
        };
        store.Save(hero);

        Assert.True(store.Delete(hero.Id));
        Assert.Null(store.Load(hero.Id));
        Assert.Equal(0, new SqliteStore<HeroStats>(_connectionString).Count());
        Assert.Equal(0, new SqliteStore<HeroItem>(_connectionString).Count());
    }

    [Fact]
    public void MySqlDialect_GeneratesExpectedDdl()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        var schema = store.SchemaSql(SqlDialect.MySql);

        Assert.Contains(schema, s => s.Contains("CHAR(36)"));
        Assert.Contains(schema, s => s.Contains("ENGINE=InnoDB"));
        Assert.Contains(schema, s => s.Contains("`") == false); // we quote with " consistently
        Assert.Contains(schema, s => s.Contains("stats_id"));
    }

    [Fact]
    public void BlobMode_WithCompression_RoundTrips()
    {
        var options = new PersistenceOptions
        {
            Compress = data => Compress(data),
            Decompress = data => Decompress(data),
            CompressionThreshold = 0,
        };
        var store = new SqliteStore<FlatRow>(_connectionString, options);
        Assert.True(store.IsBlobMode);
        store.EnsureSchema();

        var row = new FlatRow { Id = Guid.NewGuid(), Name = "compressed", Level = 9 };
        store.Save(row);

        var loaded = store.Load(row.Id);
        Assert.NotNull(loaded);
        Assert.Equal("compressed", loaded!.Name);
        Assert.Equal(9, loaded.Level);

        var schema = store.SchemaSql(SqlDialect.Sqlite);
        Assert.Contains(schema, s => s.Contains("\"data\""));
        Assert.DoesNotContain(schema, s => s.Contains("\"level\""));
    }

    [Fact]
    public void PersistableSet_RunsTickLambdaAndAutoSaves()
    {
        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();

        var set = new PersistableSet<FlatRow>(store) { AutoSaveInterval = TimeSpan.Zero };
        var ticks = 0;
        set.OnTick = (_, _) => ticks++;

        var row = new FlatRow { Id = Guid.NewGuid(), Name = "auto", Level = 1 };
        set.Add(row);
        Assert.Equal(1, set.DirtyCount);

        set.Tick(1 / 30.0);

        Assert.Equal(1, ticks);
        Assert.Equal(0, set.DirtyCount);
        Assert.Equal("auto", store.Load(row.Id)!.Name);
    }

    [Fact]
    public void PersistableSet_ManualFlushWritesOnlyDirty()
    {
        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();
        var set = new PersistableSet<FlatRow>(store) { AutoSave = false };

        var a = new FlatRow { Name = "a", Level = 1 };
        var b = new FlatRow { Name = "b", Level = 2 };
        set.Add(a);
        set.Add(b);
        set.Flush();
        Assert.Equal(0, set.DirtyCount);

        a.Level = 10;
        set.MarkDirty(a);
        Assert.Equal(1, set.DirtyCount);
        Assert.Equal(1, set.Flush());
        Assert.Equal(10, store.Load(a.Id)!.Level);
    }

    [Fact]
    public void ForeignKeys_AreCreatedAndEnforced()
    {
        var store = new SqliteStore<HeroRow>(_connectionString);
        store.EnsureSchema();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA foreign_key_list(\"HeroItem\")";
            using var reader = pragma.ExecuteReader();
            Assert.True(reader.Read(), "HeroItem should declare a foreign key");
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO \"HeroItem\" (\"id\", \"hero_row_id\", \"_index\", \"name\", \"count\") VALUES ('x', 'missing-owner', 0, 'n', 1)";
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public void SavePartial_UpdatesOnlyListedColumns()
    {
        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();

        var row = new FlatRow { Name = "old", Level = 1 };
        store.Save(row);

        row.Name = "new";
        row.Level = 99;
        store.SavePartial(row, new[] { nameof(FlatRow.Level) });

        var loaded = store.Load(row.Id)!;
        Assert.Equal("old", loaded.Name);
        Assert.Equal(99, loaded.Level);
    }

    [Fact]
    public void LoadAll_ReturnsEveryRow()
    {
        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();
        for (var i = 0; i < 3; i++) store.Save(new FlatRow { Name = "p" + i, Level = i });

        var all = store.LoadAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { 0, 1, 2 }, all.Select(x => x.Level).OrderBy(x => x));
    }

    [Fact]
    public void BlobMode_SupportsNestedObjectsAndLists()
    {
        var options = new PersistenceOptions
        {
            Compress = Compress,
            Decompress = Decompress,
            CompressionThreshold = 0,
        };
        var store = new SqliteStore<HeroRow>(_connectionString, options);
        store.EnsureSchema();

        var hero = new HeroRow
        {
            Id = Guid.NewGuid(),
            Name = "blob",
            Stats = new HeroStats { Hp = 7, Mp = 3 },
            Items = new List<HeroItem> { new() { Name = "a" }, new() { Name = "b" } },
        };
        store.Save(hero);

        var loaded = store.Load(hero.Id)!;
        Assert.Equal("blob", loaded.Name);
        Assert.Equal(7, loaded.Stats!.Hp);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("b", loaded.Items[1].Name);
    }

    [Fact]
    public void PersistableSet_LoadAll_RestoresFromDisk()
    {
        var store = new SqliteStore<FlatRow>(_connectionString);
        store.EnsureSchema();
        store.Save(new FlatRow { Name = "a", Level = 1 });
        store.Save(new FlatRow { Name = "b", Level = 2 });

        var set = new PersistableSet<FlatRow>(store);
        var loaded = set.LoadAll();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, set.Count);
    }
    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
