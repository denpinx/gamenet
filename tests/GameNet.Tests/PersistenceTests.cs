using GameNet.Persistence;
using Xunit;

namespace GameNet.Tests;

public sealed class PlayerRow
{
    [Key] public Guid Id { get; set; }
    [Column("name")] public string Name { get; set; } = string.Empty;
    public int TeamId { get; set; }
    public float X { get; set; }
    public bool IsBot { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class NestedRow
{
    [Key] public Guid Id { get; set; }
    public PlayerRow Nested { get; set; } = new();
}

public sealed class CompositeKeyRow
{
    [Key] public int A { get; set; }
    [Key] public int B { get; set; }
}

public class PersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public PersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gamenet-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void MultipleKeyAttributes_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var store = new SqliteStore<CompositeKeyRow>(_connectionString);
            store.EnsureSchema();
        });
    }
    [Fact]
    public void EnsureSchema_CreatesTable_FromType()
    {
        var store = new SqliteStore<PlayerRow>(_connectionString);
        store.EnsureSchema();

        var sql = store.CreateTableSql();
        Assert.Contains("CREATE TABLE IF NOT EXISTS", sql);
        Assert.Contains("\"id\"", sql);
        Assert.Contains("\"team_id\" INTEGER", sql);
        Assert.Contains("PRIMARY KEY", sql);
        Assert.Equal("PlayerRow", store.TableName);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValues()
    {
        var store = new SqliteStore<PlayerRow>(_connectionString);
        store.EnsureSchema();

        var row = new PlayerRow
        {
            Id = Guid.NewGuid(),
            Name = "张三",
            TeamId = 2,
            X = 12.5f,
            IsBot = true,
            CreatedAt = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc),
        };
        store.Save(row);

        var loaded = store.Load(row.Id);
        Assert.NotNull(loaded);
        Assert.Equal(row.Name, loaded!.Name);
        Assert.Equal(2, loaded.TeamId);
        Assert.Equal(12.5f, loaded.X);
        Assert.True(loaded.IsBot);
        Assert.Equal(row.CreatedAt, loaded.CreatedAt);
    }

    [Fact]
    public void SaveTwice_UpdatesInsteadOfDuplicating()
    {
        var store = new SqliteStore<PlayerRow>(_connectionString);
        store.EnsureSchema();

        var row = new PlayerRow { Id = Guid.NewGuid(), Name = "a", TeamId = 1 };
        store.Save(row);
        row.Name = "b";
        row.TeamId = 9;
        store.Save(row);

        Assert.Equal(1, store.Count());
        Assert.Equal("b", store.Load(row.Id)!.Name);
    }

    [Fact]
    public void Load_MissingKey_ReturnsNull()
    {
        var store = new SqliteStore<PlayerRow>(_connectionString);
        store.EnsureSchema();
        Assert.Null(store.Load(Guid.NewGuid()));
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var store = new SqliteStore<PlayerRow>(_connectionString);
        store.EnsureSchema();
        var row = new PlayerRow { Id = Guid.NewGuid(), Name = "x" };
        store.Save(row);

        Assert.True(store.Delete(row.Id));
        Assert.Equal(0, store.Count());
        Assert.False(store.Delete(row.Id));
    }

    [Fact]
    public void NestedType_GetsItsOwnTableAndReferenceColumn()
    {
        var store = new SqliteStore<NestedRow>(_connectionString);
        var schema = store.SchemaSql(SqlDialect.Sqlite);

        Assert.Contains(schema, s => s.Contains("CREATE TABLE") && s.Contains("\"NestedRow\"") && s.Contains("nested_id"));
        Assert.Contains(schema, s => s.Contains("CREATE TABLE") && s.Contains("\"PlayerRow\""));
    }

    [Fact]
    public void NestedType_RoundTrips()
    {
        var store = new SqliteStore<NestedRow>(_connectionString);
        store.EnsureSchema();

        var row = new NestedRow { Id = Guid.NewGuid(), Nested = new PlayerRow { Id = Guid.NewGuid(), Name = "inner", TeamId = 5 } };
        store.Save(row);

        var loaded = store.Load(row.Id);
        Assert.NotNull(loaded);
        Assert.Equal("inner", loaded!.Nested.Name);
        Assert.Equal(5, loaded.Nested.TeamId);
    }
}
