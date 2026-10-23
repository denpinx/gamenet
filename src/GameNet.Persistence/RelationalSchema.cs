using System.Reflection;
using System.Text;

namespace GameNet.Persistence;

public enum SqlDialect
{
    Sqlite,
    MySql,
}

internal sealed class ColumnDef
{
    public required PropertyInfo Property { get; init; }
    public required string Column { get; init; }
}

internal sealed class RelationDef
{
    public required PropertyInfo Property { get; init; }
    public required TableDef Child { get; init; }
    public required bool IsList { get; init; }
    public required string FkColumn { get; init; }
    public required string OwnerColumn { get; init; }
}

internal sealed class ForeignKeyDef
{
    public required string Column { get; init; }
    public required string RefTable { get; init; }
    public required string RefColumn { get; init; }
    public required bool Cascade { get; init; }
}

internal sealed class TableDef
{
    public required Type Type { get; init; }
    public required string Name { get; init; }
    public required PropertyInfo Key { get; init; }
    public required string KeyColumn { get; init; }
    public List<ColumnDef> Columns { get; } = new();
    public List<RelationDef> Relations { get; } = new();
    public List<(string Column, Type Parent)> OwnerColumns { get; } = new();
    public List<ForeignKeyDef> ForeignKeys { get; } = new();

    public IEnumerable<Type> Dependencies()
    {
        foreach (var relation in Relations)
        {
            if (relation.IsList) continue;
            yield return relation.Child.Type;
        }
        foreach (var owner in OwnerColumns) yield return owner.Parent;
    }

    public string CreateTableSql(SqlDialect dialect, ISet<string>? allowedForeignKeys = null)
    {
        var builder = new StringBuilder();
        builder.Append("CREATE TABLE IF NOT EXISTS ").Append(Quote(Name)).Append(" (");
        var first = true;

        void Add(string column, string type, string suffix = "")
        {
            if (!first) builder.Append(", ");
            first = false;
            builder.Append(Quote(column)).Append(' ').Append(type).Append(suffix);
        }

        Add(KeyColumn, MapType(Key.PropertyType, dialect), " NOT NULL PRIMARY KEY");
        foreach (var column in Columns) Add(column.Column, MapType(column.Property.PropertyType, dialect));
        foreach (var relation in Relations)
        {
            if (relation.IsList) continue;
            Add(relation.FkColumn, MapType(relation.Child.Key.PropertyType, dialect));
        }
        foreach (var owner in OwnerColumns) Add(owner.Column, MapType(typeof(Guid), dialect));
        if (OwnerColumns.Count > 0) Add("_index", "INTEGER");

        if (allowedForeignKeys is not null)
        {
            foreach (var fk in ForeignKeys)
            {
                if (!allowedForeignKeys.Contains(fk.Column)) continue;
                builder.Append(", FOREIGN KEY (").Append(Quote(fk.Column)).Append(") REFERENCES ");
                builder.Append(Quote(fk.RefTable)).Append('(').Append(Quote(fk.RefColumn)).Append(')');
                if (fk.Cascade) builder.Append(" ON DELETE CASCADE");
            }
        }

        builder.Append(')');
        if (dialect == SqlDialect.MySql) builder.Append(" ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        return builder.ToString();
    }

    public IEnumerable<string> CreateIndexSql(SqlDialect dialect)
    {
        foreach (var relation in Relations)
        {
            var column = relation.IsList ? relation.OwnerColumn : relation.FkColumn;
            yield return IndexSql(column, dialect);
        }
        foreach (var owner in OwnerColumns) yield return IndexSql(owner.Column, dialect);
    }

    private string IndexSql(string column, SqlDialect dialect)
    {
        var name = "ix_" + Name + "_" + column;
        var body = $"ON {Quote(Name)}({Quote(column)})";
        if (dialect == SqlDialect.Sqlite)
            return $"CREATE INDEX IF NOT EXISTS {Quote(name)} {body}";
        // MySQL has no CREATE INDEX IF NOT EXISTS; callers check information_schema first.
        return $"CREATE INDEX {Quote(name)} {body}";
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string MapType(Type type, SqlDialect dialect)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum) return "INTEGER";
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte) || t == typeof(bool)) return "INTEGER";
        if (t == typeof(long)) return "BIGINT";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "REAL";
        if (t == typeof(byte[])) return "BLOB";
        if (t == typeof(Guid)) return dialect == SqlDialect.MySql ? "CHAR(36)" : "TEXT";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return dialect == SqlDialect.MySql ? "DATETIME(3)" : "TEXT";
        if (t == typeof(string)) return "TEXT";
        throw new NotSupportedException($"type {t.Name} is not a supported column type");
    }
}

internal sealed class SchemaGraph
{
    private readonly Dictionary<Type, TableDef> _tables = new();
    private readonly HashSet<Type> _building = new();
    private readonly List<TableDef> _order = new();

    public IReadOnlyCollection<TableDef> Tables => _order;

    /// <summary>Creates tables in dependency order (children before parents / parents before list children).</summary>
    public IReadOnlyList<TableDef> OrderedTables()
    {
        var ordered = new List<TableDef>();
        var state = new Dictionary<Type, int>(); // 0 unvisited, 1 visiting, 2 done

        void Visit(TableDef table)
        {
            if (state.TryGetValue(table.Type, out var s))
            {
                if (s == 2) return;
                if (s == 1) return; // cycle: fall back to declaration order
            }
            state[table.Type] = 1;
            foreach (var dependency in table.Dependencies())
            {
                if (_tables.TryGetValue(dependency, out var child)) Visit(child);
            }
            state[table.Type] = 2;
            if (!ordered.Contains(table)) ordered.Add(table);
        }

        foreach (var table in _order) Visit(table);
        return ordered;
    }

    public TableDef For(Type type, string? tableName = null)
    {
        if (_tables.TryGetValue(type, out var existing)) return existing;
        if (!_building.Add(type))
            throw new InvalidOperationException($"cyclic type graph detected at {type.Name}; nested types must not form cycles");

        var key = FindKey(type);
        var definition = new TableDef
        {
            Type = type,
            Name = tableName ?? type.GetCustomAttribute<TableAttribute>()?.Name ?? type.Name,
            Key = key,
            KeyColumn = Snake(key.Name),
        };
        _tables[type] = definition;
        _order.Add(definition);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetCustomAttribute<IgnoreAttribute>() is not null) continue;
            if (property == definition.Key) continue;

            var propertyType = property.PropertyType;
            if (IsScalar(propertyType))
            {
                definition.Columns.Add(new ColumnDef { Property = property, Column = ColumnName(property) });
                continue;
            }

            if (TryGetListElement(propertyType, out var elementType))
            {
                if (IsScalar(elementType))
                    throw new NotSupportedException($"{type.Name}.{property.Name}: lists of scalars are not supported; use a nested type with a key");
                var child = For(elementType);
                var ownerColumn = Snake(type.Name) + "_id";
                if (!child.OwnerColumns.Any(o => o.Column == ownerColumn))
                {
                    child.OwnerColumns.Add((ownerColumn, type));
                    child.ForeignKeys.Add(new ForeignKeyDef
                    {
                        Column = ownerColumn,
                        RefTable = definition.Name,
                        RefColumn = definition.KeyColumn,
                        Cascade = true,
                    });
                }
                definition.Relations.Add(new RelationDef
                {
                    Property = property,
                    Child = child,
                    IsList = true,
                    FkColumn = string.Empty,
                    OwnerColumn = ownerColumn,
                });
                continue;
            }

            if (propertyType.IsClass || (propertyType.IsValueType && !propertyType.IsPrimitive))
            {
                var child = For(propertyType);
                var fkColumn = Snake(property.Name) + "_id";
                definition.ForeignKeys.Add(new ForeignKeyDef
                {
                    Column = fkColumn,
                    RefTable = child.Name,
                    RefColumn = child.KeyColumn,
                    Cascade = false,
                });
                definition.Relations.Add(new RelationDef
                {
                    Property = property,
                    Child = child,
                    IsList = false,
                    FkColumn = fkColumn,
                    OwnerColumn = string.Empty,
                });
                continue;
            }

            throw new NotSupportedException($"{type.Name}.{property.Name}: unsupported nested type {propertyType.Name}");
        }

        _building.Remove(type);
        return definition;
    }

    private static PropertyInfo FindKey(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToList();

        var key = properties.FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null)
                  ?? properties.FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase));

        return key ?? throw new InvalidOperationException($"{type.Name} needs a [Key] property or a property named Id");
    }

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum || t.IsPrimitive || t == typeof(string) || t == typeof(Guid) ||
               t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(decimal) || t == typeof(byte[]);
    }

    private static bool TryGetListElement(Type type, out Type element)
    {
        element = typeof(object);
        if (!type.IsGenericType) return false;
        if (type.GetGenericTypeDefinition() != typeof(List<>)) return false;
        element = type.GetGenericArguments()[0];
        return true;
    }

    private static string ColumnName(PropertyInfo property) =>
        property.GetCustomAttribute<ColumnAttribute>()?.Name ?? Snake(property.Name);

    internal static string Snake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
