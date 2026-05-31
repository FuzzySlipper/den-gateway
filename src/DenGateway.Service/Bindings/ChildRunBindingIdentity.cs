namespace DenGateway.Service.Bindings;

public static class ChildRunBindingIdentity
{
    public static bool IsChildRunBinding(string? adapterInstanceId)
    {
        if (string.IsNullOrWhiteSpace(adapterInstanceId))
            return false;

        // Child-run pattern: hermes:{host}:{profile}:{run_id}. Profile-level
        // live bindings such as hermes:den-k8:spawned-coder:pool-coder-01:live
        // intentionally do not match.
        var parts = adapterInstanceId.Split(':');
        return parts.Length == 4
            && parts[0].Equals("hermes", StringComparison.OrdinalIgnoreCase)
            && parts[3].StartsWith("piw_", StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryParseProfileIdentity(string? adapterInstanceId)
    {
        if (!IsChildRunBinding(adapterInstanceId))
            return null;

        return adapterInstanceId!.Split(':')[2];
    }

    public static string? TryParseRunId(string? adapterInstanceId)
    {
        if (!IsChildRunBinding(adapterInstanceId))
            return null;

        return adapterInstanceId!.Split(':')[3];
    }
}
