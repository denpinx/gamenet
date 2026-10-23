using GameNet.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameNet.Tests;

public sealed class RenamedRow
{
    [Key] public Guid Id { get; set; }
    [Column("title")] public string Title { get; set; } = string.Empty;
}

/// <summary>Schema evolution: old tables gain new columns; type changes are rejected.</summary>
public class MigrationTests : IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;

    public MigrationTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"gamenet-mig-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_path}";
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureSchema_AddsColumnsMissingFromAnOlderTable()
    {
        var id = Guid.NewGuid();
        Execute("CREATE TABLE \"flat_mig\" (\"id\" TEXT NOT NULL PRIMARY KEY, \"name\" TEXT)");
        Execute($"INSERT INTO \"flat_mig\" (\"id\", \"name\") VALUES ('{id}', 'legacy')");

        var store = new SqliteStore<FlatRow>(_connectionString, "flat_mig");
        store.EnsureSchema();

        var loaded = store.Load(id);
        Assert.NotNull(loaded);
        Assert.Equal("legacy", loaded!.Name);
        Assert.Equal(0, loaded.Level); // new column defaults for old rows

        loaded.Level = 7;
        store.Save(loaded);
        Assert.Equal(7, store.Load(id)!.Level);
    }

    [Fact]
    public void EnsureSchema_IsIdempotent()
    {
        var store = new SqliteStore<FlatRow>(_connectionString, "flat_idem");
        store.EnsureSchema();
        store.EnsureSchema();
        store.Save(new FlatRow { Name = "x", Level = 1 });
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void RenamedProperty_KeepsOldColumnAndAddsNewOne()
    {
        var id = Guid.NewGuid();
        Execute("CREATE TABLE \"renamed\" (\"id\" TEXT NOT NULL PRIMARY KEY, \"name\" TEXT)");
        Execute($"INSERT INTO \"renamed\" (\"id\", \"name\") VALUES ('{id}', 'old-name')");

        var store = new SqliteStore<RenamedRow>(_connectionString, "renamed");
        store.EnsureSchema();

        // Old data stays in the legacy column; the new property reads its own column (empty).
        var loaded = store.Load(id);
        Assert.NotNull(loaded);
        Assert.True(string.IsNullOrEmpty(loaded!.Title));

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"name\" FROM \"renamed\" WHERE \"id\" = @id";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        Assert.Equal("old-name", command.ExecuteScalar());
    }

    [Fact]
    public void ChangedColumnType_IsRejected()
    {
        Execute("CREATE TABLE \"flat_bad\" (\"id\" TEXT NOT NULL PRIMARY KEY, \"name\" TEXT, \"level\" TEXT)");
        var store = new SqliteStore<FlatRow>(_connectionString, "flat_bad");

        var error = Assert.Throws<InvalidOperationException>(() => store.EnsureSchema());
        Assert.Contains("manual migration", error.Message);
        Assert.Contains("level", error.Message);
    }

    [Fact]
    public void MySqlMigration_ProducesAlterStatements()
    {
        var store = new SqliteStore<FlatRow>(_connectionString, "flat_mysql");
        var sql = store.MigrationSql(SqlDialect.MySql, new[] { "id", "name" });

        Assert.Single(sql);
        Assert.Contains("ALTER TABLE", sql[0]);
        Assert.Contains("ADD COLUMN", sql[0]);
        Assert.Contains("level", sql[0]);
    }
}
