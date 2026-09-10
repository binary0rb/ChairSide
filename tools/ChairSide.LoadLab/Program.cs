// ChairSide Load Lab
// ==================
// Developer tool for simulating ChairSide board traffic against a local instance.
// Measures endpoint latency and verifies no obvious failures under normal load.
//
// WARNING: Do NOT point this at a live clinic server. The safety check blocks
// known production hostnames by default. Use --allow-production-target only if
// you understand the consequences and have verified the target is safe.
//
// Usage:
//   dotnet run --project tools/ChairSide.LoadLab -- [options]
//
// Options:
//   --base-url <url>             Target base URL (default: http://localhost:5000)
//   --duration-seconds <n>       Run duration in seconds (default: 60)
//   --rooms <n>                  Number of rooms configured on target (default: 12)
//   --room-pollers <n>           Concurrent GET /api/board pollers (default: 12)
//   --master-pollers <n>         Additional GET /api/board pollers (default: 2)
//   --reports-pollers <n>        Concurrent GET /api/reports pollers (default: 1)
//   --mutation-workers <n>       Concurrent lifecycle mutation workers (default: 1)
//   --admin-password <pw>        Admin password for /api/reports (optional)
//   --allow-production-target    Override the production-target safety block
//   --verbose                    Print each request as it completes
//
// Example:
//   dotnet run --project tools/ChairSide.LoadLab -- \
//       --base-url http://localhost:5000 --duration-seconds 60
//
// Note: room lifecycle mutations require that device binding is disabled on the
// target (the default for a dev configuration). If binding is enabled, seat/
// lifecycle endpoints will return 403 and mutations will be counted as failures.
//
// Acceptance targets (printed at the end):
//   - 0 failed requests
//   - /api/board p95 < 250ms
//   - /api/board max < 1000ms
//   - /api/reports p95 < 1000ms

using System.Diagnostics;
using System.Net.Http.Json;
using ChairSide.LoadLab;

// -- parse options --
var (options, parseError) = LoadLabOptions.Parse(args);
if (parseError is not null)
{
    Console.Error.WriteLine($"Error: {parseError}");
    return 1;
}

// -- safety check before doing anything else --
var refusal = TargetValidator.Validate(options.BaseUrl, options.AllowProductionTarget);
if (refusal is not null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("LOAD LAB REFUSED TO START");
    Console.Error.WriteLine("-------------------------");
    Console.Error.WriteLine(refusal);
    Console.Error.WriteLine();
    return 2;
}

// -- print banner so the operator can confirm the target before traffic flows --
Console.WriteLine();
Console.WriteLine("ChairSide Load Lab");
Console.WriteLine("==================");
Console.WriteLine($"Target URL:         {options.BaseUrl}");
Console.WriteLine($"Duration:           {options.DurationSeconds}s");
Console.WriteLine($"Room pollers:       {options.RoomPollers}");
Console.WriteLine($"Master pollers:     {options.MasterPollers}");
Console.WriteLine($"Reports pollers:    {options.ReportsPollers}");
Console.WriteLine($"Mutation workers:   {options.MutationWorkers}");
Console.WriteLine($"Rooms:              {options.Rooms}");
Console.WriteLine($"Verbose:            {options.Verbose}");
if (options.AdminPassword is not null)
    Console.WriteLine("Admin password:     (provided)");
Console.WriteLine();
Console.WriteLine("Starting... Press Ctrl+C to stop early.");
Console.WriteLine();

// -- set up shared state --
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var metrics = new MetricsCollector();
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var rng = new Random();

// Valid values from the default ChairSide roster. No PHI - room numbers only.
string[] DoctorIds = ["otte", "pledger", "gibson", "schroeder"];
string[] ProcedureCodes = ["CON", "EXT", "SED", "POST", "IMP", "BX", "MISC", "POE"];

// -- launch all workers --
var sw = Stopwatch.StartNew();
var tasks = new List<Task>();

// Board pollers (room clients + master clients both hit /api/board)
var totalBoardPollers = options.RoomPollers + options.MasterPollers;
for (var i = 0; i < totalBoardPollers; i++)
{
    var startDelayMs = rng.Next(0, 500);  // stagger so they don't all fire at T=0
    tasks.Add(RunPoller($"{options.BaseUrl}/api/board", startDelayMs, intervalMs: 1000, cts.Token));
}

// Reports pollers
for (var i = 0; i < options.ReportsPollers; i++)
{
    var startDelayMs = rng.Next(0, 1000);
    tasks.Add(RunPoller($"{options.BaseUrl}/api/reports", startDelayMs, intervalMs: 2000, cts.Token));
}

// Mutation workers - each owns one room number exclusively
for (var i = 0; i < options.MutationWorkers; i++)
{
    var roomNumber = (i % Math.Max(1, options.Rooms)) + 1;
    tasks.Add(RunMutationWorker(roomNumber, cts.Token));
}

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    // Normal end-of-duration shutdown.
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
}

sw.Stop();

// -- print results --
PrintResults(metrics, sw.Elapsed);
return 0;

// ---------------------------------------------------------------------------
// Poller worker: polls one URL on a fixed interval
// ---------------------------------------------------------------------------

async Task RunPoller(string url, int startDelayMs, int intervalMs, CancellationToken ct)
{
    try
    {
        await Task.Delay(startDelayMs, ct);
    }
    catch (OperationCanceledException)
    {
        return;
    }

    while (!ct.IsCancellationRequested)
    {
        var statusCode = 0;
        var isException = false;
        var latencyMs = 0L;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);

            // Include admin password for reports endpoint when provided.
            if (url.Contains("/api/reports", StringComparison.OrdinalIgnoreCase)
                && options.AdminPassword is not null)
            {
                req.Headers.Add("X-Admin-Password", options.AdminPassword);
            }

            var pollSw = Stopwatch.StartNew();
            using var resp = await httpClient.SendAsync(req, ct);
            pollSw.Stop();

            statusCode  = (int)resp.StatusCode;
            latencyMs   = pollSw.ElapsedMilliseconds;

            if (options.Verbose)
                Console.WriteLine($"  [poll] GET {url} -> {statusCode} ({latencyMs}ms)");
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            isException = true;
            if (options.Verbose)
                Console.WriteLine($"  [poll] GET {url} -> EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        metrics.RecordPoll(url, statusCode, latencyMs, isException);

        try
        {
            await Task.Delay(intervalMs, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

// ---------------------------------------------------------------------------
// Mutation worker: drives one room through repeated lifecycle cycles
// ---------------------------------------------------------------------------

async Task RunMutationWorker(int roomNumber, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await DriveFullCycle(roomNumber, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            if (options.Verbose)
                Console.WriteLine($"  [mutation] room {roomNumber}: error: {ex.GetType().Name}: {ex.Message}");

            // Brief backoff before retrying to avoid hammering a broken endpoint.
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}

// Drives one complete lifecycle: Seat -> Ready -> Arrived -> Complete -> Available.
// Occasionally cancels after seating (10%) to add variance.
// If any step is rejected (400/non-2xx), the cycle is abandoned and retried next iteration.
async Task DriveFullCycle(int roomNumber, CancellationToken ct)
{
    var doctor    = DoctorIds[rng.Next(DoctorIds.Length)];
    var procedure = ProcedureCodes[rng.Next(ProcedureCodes.Length)];

    // Step 1: Seat
    var ok = await PostMutation(
        $"/api/rooms/{roomNumber}/seat",
        new { DoctorId = doctor, ProcedureCode = procedure },
        ct);

    if (!ok)
    {
        // Room may already be occupied from a previous incomplete cycle.
        // Wait and let the background expiration or a prior cycle finish.
        await Jitter(1000, 3000, ct);
        return;
    }

    await Jitter(200, 800, ct);

    // 10% chance: cancel seating instead of advancing (simulates workflow correction).
    if (rng.Next(10) == 0)
    {
        await PostMutation($"/api/rooms/{roomNumber}/cancel-seating", null, ct);
        await Jitter(500, 1000, ct);
        return;
    }

    // Step 2: Ready for Doctor
    ok = await PostMutation($"/api/rooms/{roomNumber}/ready-for-doctor", null, ct);
    if (!ok) return;
    await Jitter(500, 2000, ct);

    // Step 3: Doctor Arrived
    ok = await PostMutation($"/api/rooms/{roomNumber}/doctor-arrived", null, ct);
    if (!ok) return;
    await Jitter(500, 1500, ct);

    // Step 4: Doctor Complete
    ok = await PostMutation($"/api/rooms/{roomNumber}/doctor-complete", null, ct);
    if (!ok) return;
    await Jitter(200, 500, ct);

    // Step 5: Room Available (turnover complete)
    await PostMutation($"/api/rooms/{roomNumber}/available", null, ct);
    await Jitter(500, 1000, ct);
}

// Posts a mutation and records latency + success in the metrics collector.
// Returns true if the response was 2xx.
async Task<bool> PostMutation(string path, object? body, CancellationToken ct)
{
    var url     = $"{options.BaseUrl}{path}";
    var success = false;
    var mutSw   = Stopwatch.StartNew();

    try
    {
        HttpResponseMessage resp;

        if (body is null)
        {
            resp = await httpClient.PostAsync(url, content: null, ct);
        }
        else
        {
            resp = await httpClient.PostAsJsonAsync(url, body, ct);
        }

        mutSw.Stop();
        success = resp.IsSuccessStatusCode;

        if (options.Verbose)
            Console.WriteLine($"  [mutation] POST {path} -> {(int)resp.StatusCode} ({mutSw.ElapsedMilliseconds}ms)");
    }
    catch (OperationCanceledException)
    {
        mutSw.Stop();
        metrics.RecordMutation(mutSw.ElapsedMilliseconds, success: false);
        throw;
    }
    catch (Exception ex)
    {
        mutSw.Stop();
        if (options.Verbose)
            Console.WriteLine($"  [mutation] POST {path} -> EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        metrics.RecordMutation(mutSw.ElapsedMilliseconds, success: false);
        return false;
    }

    metrics.RecordMutation(mutSw.ElapsedMilliseconds, success);
    return success;
}

// Random delay between minMs and maxMs, respecting cancellation.
async Task Jitter(int minMs, int maxMs, CancellationToken ct) =>
    await Task.Delay(rng.Next(minMs, maxMs), ct);

// ---------------------------------------------------------------------------
// Results output
// ---------------------------------------------------------------------------

void PrintResults(MetricsCollector m, TimeSpan elapsed)
{
    var s          = m.GetSummary();
    var board      = s.BoardLatency;
    var reports    = s.ReportsLatency;
    var mutations  = s.MutationsLatency;

    Console.WriteLine();
    Console.WriteLine("=== Load Lab Results ===");
    Console.WriteLine($"Run duration:         {elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Total requests:       {s.TotalRequests}");
    Console.WriteLine($"Failed requests:      {s.FailedRequests}");
    Console.WriteLine($"Exception count:      {s.ExceptionCount}");
    Console.WriteLine($"Mutations attempted:  {s.MutationsAttempted}");
    Console.WriteLine($"Mutations failed:     {s.MutationsFailed}");
    Console.WriteLine();

    const string hdr = "  {0,-22} {1,6} {2,7} {3,8} {4,7} {5,7}";
    const string row = "  {0,-22} {1,6} {2,7} {3,8:F1} {4,7} {5,7}";
    Console.WriteLine("--- Latency (ms) ---");
    Console.WriteLine(string.Format(hdr, "Endpoint", "Count", "Min", "Avg", "P95", "Max"));
    Console.WriteLine(string.Format(hdr, new string('-', 22), "-----", "------", "-------", "------", "------"));
    PrintLatencyRow(row, "/api/board",   board);
    PrintLatencyRow(row, "/api/reports", reports);
    PrintLatencyRow(row, "mutations",    mutations);
    Console.WriteLine();

    Console.WriteLine("--- HTTP Status Codes ---");
    foreach (var kv in s.StatusCodes.OrderBy(x => x.Key))
    {
        var label = kv.Key == 0 ? "(exception)" : kv.Key.ToString();
        Console.WriteLine($"  {label,12}: {kv.Value}");
    }
    Console.WriteLine();

    Console.WriteLine("--- Acceptance Targets ---");
    Target("0 failed requests",        s.FailedRequests == 0,
        $"{s.FailedRequests} failed");
    Target("/api/board   p95 < 250ms", board.Count   == 0 || board.P95   < 250,
        $"{board.P95}ms");
    Target("/api/board   max < 1000ms",board.Count   == 0 || board.Max   < 1000,
        $"{board.Max}ms");
    Target("/api/reports p95 < 1000ms",reports.Count == 0 || reports.P95 < 1000,
        $"{reports.P95}ms");
    Console.WriteLine();
}

static void PrintLatencyRow(string fmt, string label, LatencyStats s)
{
    if (s.Count == 0)
    {
        Console.WriteLine($"  {label,-22} {"(no data)",-42}");
        return;
    }

    Console.WriteLine(string.Format(fmt, label, s.Count, s.Min, s.Avg, s.P95, s.Max));
}

static void Target(string label, bool pass, string actual)
{
    var mark = pass ? "PASS" : "FAIL";
    var suffix = pass ? "" : $" (actual: {actual})";
    Console.WriteLine($"  [{mark}] {label}{suffix}");
}
