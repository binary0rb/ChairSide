namespace ChairSide.LoadLab;

// Prevents the load lab from accidentally hitting production.
// Only localhost/127.0.0.1 targets are accepted unless the caller
// explicitly opts in with allowProductionTarget = true.
//
// Validate returns null if the target is acceptable, or a human-readable
// refusal message if the run should be blocked.
public static class TargetValidator
{
    private static readonly HashSet<string> SafeHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1" };

    private static readonly HashSet<string> BlockedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "chairside" };

    public static string? Validate(string rawUrl, bool allowProductionTarget)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return $"Invalid URL: '{rawUrl}'. Provide a well-formed absolute URL such as http://localhost:5000.";
        }

        if (allowProductionTarget)
        {
            return null;  // caller accepted full responsibility
        }

        var host = uri.Host;

        if (BlockedHosts.Contains(host))
        {
            return $"Refused: target host '{host}' matches a known production hostname. "
                 + "Pass --allow-production-target to override (strongly discouraged for live clinic use).";
        }

        if (!SafeHosts.Contains(host))
        {
            return $"Refused: target host '{host}' is not localhost or 127.0.0.1. "
                 + "The load lab is designed for local/dev use only. "
                 + "Pass --allow-production-target to override.";
        }

        return null;
    }
}
