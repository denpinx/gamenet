using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GameNet.Persistence;

public sealed class PersistenceOptions
{
    public SqlDialect Dialect { get; set; } = SqlDialect.Sqlite;

    /// <summary>Optional compression applied to the blob payload. Setting it switches to blob mode.</summary>
    public Func<byte[], byte[]>? Compress { get; set; }

    public Func<byte[], byte[]>? Decompress { get; set; }

    public int CompressionThreshold { get; set; } = 256;

    public bool BlobMode => Compress is not null && Decompress is not null;
}

/// <summary>
/// Code-first relational store. Builds a table per type, decomposes nested types into their own
/// tables and single/collection references, and exposes single-record Load/Save/Delete.
/// Blob mode (when compression is configured) stores one opaque `data` column per row instead.
/// </summary>
public sealed class SqliteStore<T> where T : class, new()
{
    private readonly string _connectionString;
    private readonly SchemaGraph _graph;
    private readonly TableDef _root;
    private readonly PersistenceOptions _options;

    public SqliteStore(string connectionString, PersistenceOptions? options = null, string? tableName = null)
    {
        _connectionString = connectionString;
        _options = options ?? new PersistenceOptions();
        _graph = new SchemaGraph();
        _root = _graph.For(typeof(T), tableName);
    }

    /// <summary>Convenience overload: use a table name different from the CLR type name.</summary>
    public SqliteStore(string connectionString, string tableName) : this(connectionString, null, tableName)
    {
    }

    public string TableName => _root.Name;
    public bool IsBlobMode => _options.BlobMode;

    public IReadOnlyList<string> SchemaSql(SqlDialect dialect)
    {
        if (_options.BlobMode)
        {
            var idType = dialect == SqlDialect.MySql ? "CHAR(36)" : "TEXT";
            var suffix = dialect == SqlDialect.MySql ? " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4" : string.Empty;
            return new[]
            {
                $"CREATE TABLE IF NOT EXISTS {TableDef.Quote(_root.Name)} (\"id\" {idType} NOT NULL PRIMARY KEY, \"data\" BLOB NOT NULL){suffix}",
            };
        }

        var ordered = _graph.OrderedTables();
        var position = ordered.Select((table, index) => (table.Name, index)).ToDictionary(x => x.Name, x => x.index);
        var statements = new List<string>();
        foreach (var table in ordered)
        {
            var allowed = table.ForeignKeys
                .Where(fk => position.TryGetValue(fk.RefTable, out var refIndex) && refIndex < position[table.Name])
                .Select(fk => fk.Column)
                .ToHashSet();
            statements.Add(table.CreateTableSql(dialect, allowed));
        }
        foreach (var table in ordered) statements.AddRange(table.CreateIndexSql(dialect));
        return statements;
    }

    public string CreateTableSql(SqlDialect dialect = SqlDialect.Sqlite) => SchemaSql(dialect)[0];

    /// <summary>
    /// Creates missing tables, then migrates existing ones by adding columns that the CLR type has
    /// gained. Type changes are rejected (manual migration), so a rename is "old column kept + new
    /// column added" unless the property maps to the old column via [Column].
    /// </summary>
    public void EnsureSchema()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (_options.BlobMode)
        {
            Execute(connection, transaction, SchemaSql(SqlDialect.Sqlite)[0]);
            transaction.Commit();
            return;
        }

        var ordered = _graph.OrderedTables();
        var position = ordered.Select((table, index) => (table.Name, index)).ToDictionary(x => x.Name, x => x.index);

        foreach (var table in ordered)
        {
            var allowed = table.ForeignKeys
                .Where(fk => position.TryGetValue(fk.RefTable, out var refIndex) && refIndex < position[table.Name])
                .Select(fk => fk.Column)
                .ToHashSet();
            Execute(connection, transaction, table.CreateTableSql(SqlDialect.Sqlite, allowed));
            MigrateColumns(connection, transaction, table);
        }

        foreach (var table in ordered)
            foreach (var index in table.CreateIndexSql(SqlDialect.Sqlite))
                Execute(connection, transaction, index);

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void MigrateColumns(SqliteConnection connection, SqliteTransaction? transaction, TableDef table)
    {
        var existing = ReadColumns(connection, transaction, table.Name);
        if (!existing.ContainsKey(table.KeyColumn))
            throw new InvalidOperationException($"table {table.Name} exists but has no key column '{table.KeyColumn}'; manual migration required");

        foreach (var (column, type) in ExpectedColumns(table, SqlDialect.Sqlite))
        {
            if (!existing.TryGetValue(column, out var existingType))
            {
                Execute(connection, transaction, $"ALTER TABLE {TableDef.Quote(table.Name)} ADD COLUMN {TableDef.Quote(column)} {type}");
                continue;
            }

            if (TypeClass(existingType) != TypeClass(type))
                throw new InvalidOperationException(
                    $"column {table.Name}.{column} is {existingType} in the database but {type} in the model; manual migration required");
        }
    }

    /// <summary>ALTER statements needed to bring an existing MySQL table up to the model.</summary>
    public IReadOnlyList<string> MigrationSql(SqlDialect dialect, IReadOnlyCollection<string> existingColumns)
    {
        if (_options.BlobMode || dialect != SqlDialect.MySql) return Array.Empty<string>();
        var statements = new List<string>();
        foreach (var (column, type) in ExpectedColumns(_root, dialect))
        {
            if (!existingColumns.Contains(column))
                statements.Add($"ALTER TABLE {TableDef.Quote(_root.Name)} ADD COLUMN {TableDef.Quote(column)} {type}");
        }
        return statements;
    }

    private static Dictionary<string, string> ReadColumns(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({TableDef.Quote(table)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            columns[name] = type;
        }
        return columns;
    }

    private static List<(string Column, string Type)> ExpectedColumns(TableDef table, SqlDialect dialect)
    {
        var result = new List<(string, string)>
        {
            (table.KeyColumn, TableDef.MapType(table.Key.PropertyType, dialect)),
        };
        foreach (var column in table.Columns)
            result.Add((column.Column, TableDef.MapType(column.Property.PropertyType, dialect)));
        foreach (var relation in table.Relations.Where(r => !r.IsList))
            result.Add((relation.FkColumn, TableDef.MapType(relation.Child.Key.PropertyType, dialect)));
        foreach (var owner in table.OwnerColumns)
            result.Add((owner.Column, TableDef.MapType(typeof(Guid), dialect)));
        if (table.OwnerColumns.Count > 0) result.Add(("_index", "INTEGER"));
        return result;
    }

    private static string TypeClass(string sqlType)
    {
        var type = sqlType.ToUpperInvariant();
        if (type.Contains("INT")) return "INTEGER";
        if (type.Contains("CHAR") || type.Contains("TEXT") || type.Contains("CLOB") || type.Contains("DATE")) return "TEXT";
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") || type.Contains("DEC") || type.Contains("NUM")) return "REAL";
        if (type.Contains("BLOB")) return "BLOB";
        return type;
    }

    public void Save(T item)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SaveItem(connection, transaction, _root, item, null);
        transaction.Commit();
    }

    /// <summary>Updates only the listed scalar columns; falls back to a full save when a relation changed.</summary>
    public void SavePartial(T item, IReadOnlyList<string> changedProperties)
    {
        if (_options.BlobMode || changedProperties.Count == 0)
        {
            Save(item);
            return;
        }

        var changed = new List<ColumnDef>();
        foreach (var name in changedProperties)
        {
            var property = typeof(T).GetProperty(name);
            if (property is null) continue;
            if (_root.Relations.Any(r => r.Property == property))
            {
                Save(item);
                return;
            }
            var column = _root.Columns.FirstOrDefault(c => c.Property == property);
            if (column is not null && !changed.Contains(column)) changed.Add(column);
        }

        if (changed.Count == 0) return;

        var key = EnsureKey(_root, item);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var sets = string.Join(", ", changed.Select(c => $"{TableDef.Quote(c.Column)} = @{c.Column}"));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {TableDef.Quote(_root.Name)} SET {sets} WHERE {TableDef.Quote(_root.KeyColumn)} = @__key";
        command.Parameters.AddWithValue("@__key", ToDb(key));
        foreach (var column in changed)
            command.Parameters.AddWithValue("@" + column.Column, ToDb(column.Property.GetValue(item)));
        var rows = command.ExecuteNonQuery();
        transaction.Commit();

        if (rows == 0) Save(item); // row vanished underneath us — self-heal with an insert
    }

    public T? Load(object key)
    {
        using var connection = Open();
        return (T?)LoadItem(connection, null, _root, key);
    }

    public bool Delete(object key)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var deleted = DeleteItem(connection, transaction, _root, key);
        transaction.Commit();
        return deleted;
    }

    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {TableDef.Quote(_root.Name)}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<T> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TableDef.Quote(_root.KeyColumn)} FROM {TableDef.Quote(_root.Name)}";
        var keys = new List<object>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) keys.Add(FromDb(reader.GetValue(0), _root.Key.PropertyType)!);
        }

        var result = new List<T>(keys.Count);
        foreach (var key in keys)
        {
            var item = LoadItem(connection, null, _root, key);
            if (item is not null) result.Add((T)item);
        }
        return result;
    }

    // ---------------------------------------------------------------- save

    private object SaveItem(SqliteConnection connection, SqliteTransaction? transaction, TableDef table, object item, IReadOnlyDictionary<string, object?>? ownerValues)
    {
        if (_options.BlobMode && table == _root) return SaveBlob(connection, transaction, item);

        var key = EnsureKey(table, item);

        var columns = new List<(string Column, object? Value)>();
        foreach (var column in table.Columns)
            columns.Add((column.Column, ToDb(column.Property.GetValue(item))));

        foreach (var relation in table.Relations)
        {
            if (relation.IsList) continue;
            var child = relation.Property.GetValue(item);
            object? fk = null;
            if (child is not null)
            {
                var childGraphTable = relation.Child;
                var childId = SaveItem(connection, transaction, childGraphTable, child, null);
                fk = ToDb(childId);
            }
            columns.Add((relation.FkColumn, fk));
        }

        if (ownerValues is not null)
            foreach (var pair in ownerValues) columns.Add((pair.Key, pair.Value));

        columns.Add((table.KeyColumn, ToDb(key)));

        var sql = BuildUpsert(table, columns);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (column, value) in columns)
                command.Parameters.AddWithValue("@" + column, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach (var relation in table.Relations)
        {
            if (!relation.IsList) continue;
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = $"DELETE FROM {TableDef.Quote(relation.Child.Name)} WHERE {TableDef.Quote(relation.OwnerColumn)} = @owner";
                clear.Parameters.AddWithValue("@owner", ToDb(key));
                clear.ExecuteNonQuery();
            }

            var list = relation.Property.GetValue(item) as IEnumerable;
            if (list is null) continue;
            var index = 0;
            foreach (var child in list)
            {
                if (child is null) continue;
                var extras = new Dictionary<string, object?>
                {
                    [relation.OwnerColumn] = ToDb(key),
                    ["_index"] = index,
                };
                SaveItem(connection, transaction, relation.Child, child, extras);
                index++;
            }
        }

        return key;
    }

    private string BuildUpsert(TableDef table, List<(string Column, object? Value)> columns)
    {
        var names = string.Join(", ", columns.Select(c => TableDef.Quote(c.Column)));
        var values = string.Join(", ", columns.Select(c => "@" + c.Column));
        var updates = string.Join(", ", columns
            .Where(c => c.Column != table.KeyColumn)
            .Select(c => $"{TableDef.Quote(c.Column)} = excluded.{TableDef.Quote(c.Column)}"));
        return $"INSERT INTO {TableDef.Quote(table.Name)} ({names}) VALUES ({values}) " +
               $"ON CONFLICT({TableDef.Quote(table.KeyColumn)}) DO UPDATE SET {updates}";
    }

    private object EnsureKey(TableDef table, object item)
    {
        var value = table.Key.GetValue(item);
        if (value is Guid guid)
        {
            if (guid == Guid.Empty)
            {
                guid = Guid.NewGuid();
                if (table.Key.CanWrite) table.Key.SetValue(item, guid);
                else throw new InvalidOperationException($"{table.Type.Name}.{table.Key.Name} is Guid.Empty and read-only");
            }
            return guid;
        }

        if (value is null) throw new InvalidOperationException($"{table.Type.Name}.{table.Key.Name} is null");
        if (value is string text && text.Length == 0) throw new InvalidOperationException($"{table.Type.Name}.{table.Key.Name} is empty");
        return value;
    }

    // ---------------------------------------------------------------- load

    private object? LoadItem(SqliteConnection connection, SqliteTransaction? transaction, TableDef table, object key)
    {
        if (_options.BlobMode && table == _root) return LoadBlob(connection, transaction, key);

        var scalarColumns = table.Columns.Select(c => c.Column).ToList();
        var select = new List<string> { TableDef.Quote(table.KeyColumn) };
        select.AddRange(scalarColumns.Select(TableDef.Quote));
        foreach (var relation in table.Relations.Where(r => !r.IsList))
            select.Add(TableDef.Quote(relation.FkColumn));

        object? instance;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {string.Join(", ", select)} FROM {TableDef.Quote(table.Name)} WHERE {TableDef.Quote(table.KeyColumn)} = @key";
            command.Parameters.AddWithValue("@key", ToDb(key));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            instance = Activator.CreateInstance(table.Type)
                       ?? throw new InvalidOperationException($"cannot construct {table.Type.Name}");

            table.Key.SetValue(instance, FromDb(reader.GetValue(0), table.Key.PropertyType));
            for (var i = 0; i < table.Columns.Count; i++)
                table.Columns[i].Property.SetValue(instance, FromDb(reader.GetValue(i + 1), table.Columns[i].Property.PropertyType));

            var fkIndex = table.Columns.Count + 1;
            foreach (var relation in table.Relations.Where(r => !r.IsList))
            {
                var raw = reader.GetValue(fkIndex++);
                if (raw is DBNull) continue;
                var fk = FromDb(raw, relation.Child.Key.PropertyType);
                var child = LoadItem(connection, transaction, relation.Child, fk!);
                relation.Property.SetValue(instance, child);
            }
        }

        foreach (var relation in table.Relations.Where(r => r.IsList))
        {
            var listType = typeof(List<>).MakeGenericType(relation.Child.Type);
            var list = (IList)Activator.CreateInstance(listType)!;

            var keys = new List<object>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT {TableDef.Quote(relation.Child.KeyColumn)} FROM {TableDef.Quote(relation.Child.Name)} " +
                                      $"WHERE {TableDef.Quote(relation.OwnerColumn)} = @owner ORDER BY \"_index\"";
                command.Parameters.AddWithValue("@owner", ToDb(key));
                using var reader = command.ExecuteReader();
                while (reader.Read()) keys.Add(FromDb(reader.GetValue(0), relation.Child.Key.PropertyType)!);
            }

            foreach (var childKey in keys)
            {
                var child = LoadItem(connection, transaction, relation.Child, childKey);
                if (child is not null) list.Add(child);
            }

            relation.Property.SetValue(instance, list);
        }

        return instance;
    }

    // ---------------------------------------------------------------- delete

    private bool DeleteItem(SqliteConnection connection, SqliteTransaction? transaction, TableDef table, object key)
    {
        if (_options.BlobMode && table == _root) return DeleteRow(connection, transaction, table, key);

        // Read single-child keys before the parent row disappears (the FK lives on the parent).
        var singleChildren = new List<(RelationDef Relation, object Key)>();
        foreach (var relation in table.Relations.Where(r => !r.IsList))
        {
            using var fkCommand = connection.CreateCommand();
            fkCommand.Transaction = transaction;
            fkCommand.CommandText = $"SELECT {TableDef.Quote(relation.FkColumn)} FROM {TableDef.Quote(table.Name)} WHERE {TableDef.Quote(table.KeyColumn)} = @key";
            fkCommand.Parameters.AddWithValue("@key", ToDb(key));
            var fkValue = fkCommand.ExecuteScalar();
            if (fkValue is not null and not DBNull)
                singleChildren.Add((relation, FromDb(fkValue, relation.Child.Key.PropertyType)!));
        }

        // Collection children reference the parent, so they must go first.
        foreach (var relation in table.Relations.Where(r => r.IsList))
        {
            var keys = new List<object>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT {TableDef.Quote(relation.Child.KeyColumn)} FROM {TableDef.Quote(relation.Child.Name)} WHERE {TableDef.Quote(relation.OwnerColumn)} = @owner";
                command.Parameters.AddWithValue("@owner", ToDb(key));
                using var reader = command.ExecuteReader();
                while (reader.Read()) keys.Add(FromDb(reader.GetValue(0), relation.Child.Key.PropertyType)!);
            }
            foreach (var childKey in keys) DeleteItem(connection, transaction, relation.Child, childKey);
        }

        var deleted = DeleteRow(connection, transaction, table, key);

        // Single children are referenced by the parent, so they can only go after it.
        foreach (var (relation, childKey) in singleChildren)
            DeleteItem(connection, transaction, relation.Child, childKey);

        return deleted;
    }

    private static bool DeleteRow(SqliteConnection connection, SqliteTransaction? transaction, TableDef table, object key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {TableDef.Quote(table.Name)} WHERE {TableDef.Quote(table.KeyColumn)} = @key";
        command.Parameters.AddWithValue("@key", ToDb(key));
        return command.ExecuteNonQuery() > 0;
    }

    // ---------------------------------------------------------------- blob mode

    private object SaveBlob(SqliteConnection connection, SqliteTransaction? transaction, object item)
    {
        var key = EnsureKey(_root, item);
        var payload = BinaryObjectCodec.Write(item);
        var codec = (byte)0;
        var stored = payload;
        if (payload.Length >= _options.CompressionThreshold)
        {
            var compressed = _options.Compress!(payload);
            if (compressed.Length < payload.Length)
            {
                stored = compressed;
                codec = 1;
            }
        }

        var data = new byte[stored.Length + 1];
        data[0] = codec;
        stored.CopyTo(data, 1);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {TableDef.Quote(_root.Name)} (\"id\", \"data\") VALUES (@id, @data) " +
                              $"ON CONFLICT(\"id\") DO UPDATE SET \"data\" = excluded.\"data\"";
        command.Parameters.AddWithValue("@id", ToDb(key));
        command.Parameters.AddWithValue("@data", data);
        command.ExecuteNonQuery();
        return key;
    }

    private object? LoadBlob(SqliteConnection connection, SqliteTransaction? transaction, object key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT \"data\" FROM {TableDef.Quote(_root.Name)} WHERE \"id\" = @id";
        command.Parameters.AddWithValue("@id", ToDb(key));
        var raw = command.ExecuteScalar();
        if (raw is not byte[] data) return null;

        var codec = data[0];
        var payload = codec == 1 ? _options.Decompress!(data[1..]) : data[1..];
        return BinaryObjectCodec.Read(payload, _root.Type);
    }

    // ---------------------------------------------------------------- helpers

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();
        return connection;
    }

    internal static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        Guid guid => guid.ToString("D"),
        bool b => b ? 1 : 0,
        DateTime dt => dt.ToUniversalTime().ToString("O"),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O"),
        Enum e => Convert.ToInt64(e),
        _ => value,
    };

    internal static object? FromDb(object value, Type targetType)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is DBNull)
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        if (type == typeof(Guid)) return Guid.Parse(Convert.ToString(value)!);
        if (type == typeof(bool)) return Convert.ToInt64(value) != 0;
        if (type == typeof(DateTime)) return DateTime.Parse(Convert.ToString(value)!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(Convert.ToString(value)!);
        if (type.IsEnum) return Enum.ToObject(type, Convert.ToInt64(value));
        if (type == typeof(string)) return Convert.ToString(value);
        if (type == typeof(byte[])) return (byte[])value;
        if (type == typeof(float)) return Convert.ToSingle(value);
        if (type == typeof(double)) return Convert.ToDouble(value);
        if (type == typeof(int)) return Convert.ToInt32(value);
        if (type == typeof(short)) return Convert.ToInt16(value);
        if (type == typeof(byte)) return Convert.ToByte(value);
        if (type == typeof(long)) return Convert.ToInt64(value);
        return Convert.ChangeType(value, type);
    }
}
