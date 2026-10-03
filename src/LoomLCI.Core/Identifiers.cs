using System.Security.Cryptography;

namespace LoomLCI.Core;

internal static class IdentifierFactory
{
    public static string Create(string prefix)
        => $"{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
}

public readonly record struct WorkId(string Value)
{
    public static WorkId Create() => new(IdentifierFactory.Create("wrk"));
    public override string ToString() => Value;
}

public readonly record struct InvocationId(string Value)
{
    public static InvocationId Create() => new(IdentifierFactory.Create("inv"));
    public override string ToString() => Value;
}

public readonly record struct ResourceHandle(string Value)
{
    public static ResourceHandle Create(string prefix) => new(IdentifierFactory.Create(prefix));
    public override string ToString() => Value;
}

public readonly record struct ProcessHandle(string Value)
{
    public ResourceHandle AsResourceHandle() => new(Value);
    public override string ToString() => Value;
}
