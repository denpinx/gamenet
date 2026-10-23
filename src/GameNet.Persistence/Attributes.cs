namespace GameNet.Persistence;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name) => Name = name;
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute
{
}

/// <summary>Optional: limits string columns so they can be indexed (SQLite ignores it, we validate).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MaxLengthAttribute : Attribute
{
    public MaxLengthAttribute(int length) => Length = length;
    public int Length { get; }
}
