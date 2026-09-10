namespace ChairSide.LoadLab;

// Configuration for a single load lab run.
// All options have safe, dev-friendly defaults.
public sealed class LoadLabOptions
{
    public string BaseUrl           { get; init; } = "http://localhost:5000";
    public int    DurationSeconds   { get; init; } = 60;
    public int    Rooms             { get; init; } = 12;
    public int    RoomPollers       { get; init; } = 12;
    public int    MasterPollers     { get; init; } = 2;
    public int    ReportsPollers    { get; init; } = 1;
    public int    MutationWorkers   { get; init; } = 1;
    public bool   AllowProductionTarget { get; init; } = false;
    public bool   Verbose           { get; init; } = false;
    public string? AdminPassword    { get; init; } = null;

    // Parses --key value pairs from command-line args.
    // Boolean flags may appear without a value: --verbose is equivalent to --verbose true.
    // Returns (options, null) on success or (null!, errorMessage) on bad input.
    public static (LoadLabOptions options, string? error) Parse(string[] args)
    {
        // Build a key -> value map. Pass by value for boolean flags.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
            {
                continue;
            }

            var key = args[i][2..];

            // If next token exists and is not itself a flag, treat it as the value.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                map[key] = args[i + 1];
                i++;  // consumed: outer loop will advance past the value
            }
            else
            {
                map[key] = "true";  // bare flag
            }
        }

        // Helper: parse an int option with a default.
        static bool TryGetInt(
            Dictionary<string, string> m, string key, int defaultVal,
            out int value, out string? err)
        {
            err = null;
            if (!m.TryGetValue(key, out var raw))
            {
                value = defaultVal;
                return true;
            }

            if (!int.TryParse(raw, out value))
            {
                err = $"Invalid value for --{key}: '{raw}' is not an integer.";
                return false;
            }

            return true;
        }

        if (!TryGetInt(map, "duration-seconds",  60, out var duration,        out var err)) return (null!, err);
        if (!TryGetInt(map, "rooms",             12, out var rooms,            out err))     return (null!, err);
        if (!TryGetInt(map, "room-pollers",      12, out var roomPollers,      out err))     return (null!, err);
        if (!TryGetInt(map, "master-pollers",     2, out var masterPollers,    out err))     return (null!, err);
        if (!TryGetInt(map, "reports-pollers",    1, out var reportsPollers,   out err))     return (null!, err);
        if (!TryGetInt(map, "mutation-workers",   1, out var mutationWorkers,  out err))     return (null!, err);

        var baseUrl = map.TryGetValue("base-url", out var u) ? u : "http://localhost:5000";

        // A flag is true if present and not explicitly "false".
        static bool Flag(Dictionary<string, string> m, string key) =>
            m.TryGetValue(key, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        return (new LoadLabOptions
        {
            BaseUrl                = baseUrl,
            DurationSeconds        = duration,
            Rooms                  = rooms,
            RoomPollers            = roomPollers,
            MasterPollers          = masterPollers,
            ReportsPollers         = reportsPollers,
            MutationWorkers        = mutationWorkers,
            AllowProductionTarget  = Flag(map, "allow-production-target"),
            Verbose                = Flag(map, "verbose"),
            AdminPassword          = map.TryGetValue("admin-password", out var pw) ? pw : null,
        }, null);
    }
}
