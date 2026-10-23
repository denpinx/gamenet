namespace GameNet.Core;

/// <summary>
/// Marks an RPC-group constructor parameter as a registered collection, resolved by name.
/// Example: <c>public ChatApi([Collection("chat")] EntityCollection chat)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromCollectionAttribute : Attribute
{
    public FromCollectionAttribute(string name) => Name = name;
    public string Name { get; }
}
