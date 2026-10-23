namespace GameNet.Core;

/// <summary>Marks a class as an RPC group; the name is what clients pass to <c>Rpc.Group(name)</c>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RpcGroupAttribute : Attribute
{
    public RpcGroupAttribute(string name) => Name = name;
    public string Name { get; }
}

/// <summary>Marks a public method as remotely callable inside its group.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcAttribute : Attribute
{
    public RpcAttribute(string? name = null) => Name = name;
    public string? Name { get; }
}
