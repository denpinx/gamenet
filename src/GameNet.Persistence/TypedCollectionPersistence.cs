using GameNet.Core;
using Microsoft.Data.Sqlite;

namespace GameNet.Persistence;

/// <summary>
/// SQLite store for typed collections: key is encoded by the collection's key codec into TEXT,
/// entity payload is the generated codec BLOB. Works for int/string/Guid and custom struct keys.
/// </summary>
public sealed class SqliteTypedCollectionStore<TKey, T> : ITypedCollectionStore<TKey, T>
    where TKey : notnull
    where T : class, IEntityCodec<T>
{
    private readonly string _connectionString;
    private readonly string _table;
    private readonly ITypedKeyCodec<TKey> _keyCodec;

    public SqliteTypedCollectionStore(string connectionString, string tableName, ITypedKeyCodec<TKey> keyCodec)
    {
        _connectionString = connectionString;
        _table = tableName;
        _keyCodec = keyCodec;
    }

    public void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE IF NOT EXISTS {Quote(_table)} (\"key\" TEXT NOT NULL PRIMARY KEY, \"data\" BLOB NOT NULL)";
        command.ExecuteNonQuery();
    }

    public void Save(TKey id, T value)
    {
        var keyBytes = EncodeKey(id);
        var payload = Encode(value);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {Quote(_table)} (\"key\", \"data\") VALUES (@key, @data) " +
            "ON CONFLICT(\"key\") DO UPDATE SET \"data\" = excluded.\"data\"";
        command.Parameters.AddWithValue("@key", Convert.ToBase64String(keyBytes));
        command.Parameters.AddWithValue("@data", payload);
        command.ExecuteNonQuery();
    }

    public T? Load(TKey id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"data\" FROM {Quote(_table)} WHERE \"key\" = @key";
        command.Parameters.AddWithValue("@key", Convert.ToBase64String(EncodeKey(id)));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return Decode((byte[])reader.GetValue(0));
    }

    public bool Delete(TKey id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {Quote(_table)} WHERE \"key\" = @key";
        command.Parameters.AddWithValue("@key", Convert.ToBase64String(EncodeKey(id)));
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<(TKey Id, T Value)> LoadAll()
    {
        var result = new List<(TKey, T)>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"key\", \"data\" FROM {Quote(_table)}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = DecodeKey(Convert.FromBase64String(reader.GetString(0)));
            result.Add((key, Decode((byte[])reader.GetValue(1))));
        }
        return result;
    }

    public IReadOnlyList<(TKey Id, T Value)> Query(Func<T, bool> predicate) =>
        LoadAll().Where(row => predicate(row.Value)).ToList();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private byte[] EncodeKey(TKey key)
    {
        var writer = new ByteWriter(32);
        _keyCodec.Write(writer, key);
        return writer.ToArray();
    }

    private TKey DecodeKey(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        return _keyCodec.Read(ref reader);
    }

    private static byte[] Encode(T value)
    {
        var writer = new ByteWriter(64);
        T.Write(writer, value);
        return writer.ToArray();
    }

    private static T Decode(byte[] bytes)
    {
        var reader = new ByteReader(bytes);
        return T.Read(ref reader);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

public static class TypedCollectionPersistenceExtensions
{
    /// <summary>Enables SQLite persistence for a typed collection; every Spawn/Despawn is saved/deleted.</summary>
    public static SqliteTypedCollectionStore<TKey, T> UseSqlite<TKey, T>(
        this TypedCollection<TKey, T> collection,
        string connectionString,
        bool autoPersist = true)
        where TKey : notnull
        where T : class, IEntityCodec<T>
    {
        var store = new SqliteTypedCollectionStore<TKey, T>(
            connectionString, collection.Name, TypedKeyCodecs.Get<TKey>());
        collection.UseStore(store, autoPersist);
        return store;
    }
}
