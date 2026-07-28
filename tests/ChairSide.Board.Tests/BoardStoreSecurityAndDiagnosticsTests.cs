using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed partial class BoardStoreTests
{
    [Fact]
    public void Room_device_binding_disabled_allows_existing_mutation_behavior()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        Assert.Equal(RoomDeviceTokenValidationResult.Disabled, validator.Validate(1, token: null));
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
    }

    [Fact]
    public void Room_device_binding_enabled_rejects_missing_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: null));
        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: ""));
    }

    [Fact]
    public void Room_device_binding_enabled_rejects_wrong_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "wrong-token"));
    }

    [Fact]
    public void Room_device_binding_enabled_accepts_correct_room_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Valid, validator.Validate(1, "room-1-token"));
    }

    [Fact]
    public void Room_device_binding_room_one_token_does_not_work_for_room_two()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(2, "room-1-token"));
    }

    [Fact]
    public void Room_device_binding_enabled_fails_closed_when_room_has_no_configured_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(3, "room-3-token"));
    }

    [Fact]
    public void Read_only_board_state_still_works_without_room_token()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: null));

        var snapshot = context.Store.GetSnapshot();
        var reports = context.Store.GetReports();

        Assert.Equal(3, snapshot.RoomCount);
        Assert.Equal(3, snapshot.Rooms.Count);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public async Task Room_device_binding_guard_returns_expected_mutation_statuses()
    {
        var enabledValidator = CreateBindingValidator(enabled: true);
        var disabledValidator = CreateBindingValidator(enabled: false);

        Assert.Null(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader(token: null), disabledValidator));
        Assert.Equal(401, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader(token: null), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader("wrong-token"), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(2, RequestWithHeader("room-1-token"), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(3, RequestWithHeader("room-3-token"), enabledValidator)));
        Assert.Null(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader("room-1-token"), enabledValidator));
        Assert.Equal(401, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithQueryToken("room-1-token"), enabledValidator)));
    }

    [Fact]
    public void Room_device_binding_options_allow_disabled_config_without_room_tokens()
    {
        var result = ValidateBindingOptions(
            roomCount: 3,
            new RoomDeviceBindingOptions
            {
                Enabled = false,
                RoomTokens = []
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Room_device_binding_options_require_all_configured_rooms_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 3,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = "room-2-token"
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("RoomDeviceBindingOptions:RoomTokens:3 is required", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_reject_blank_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = " "
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("RoomDeviceBindingOptions:RoomTokens:2 must not be blank", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_reject_duplicate_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "same-token",
                    ["2"] = "same-token"
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("duplicate token values", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_accept_complete_unique_room_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = "room-2-token"
                }
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Admin_access_disabled_allows_reports_behavior()
    {
        var validator = CreateAdminValidator(enabled: false);

        Assert.Equal(AdminAccessTokenValidationResult.Disabled, validator.Validate(token: null));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader(token: null), validator));
    }

    [Fact]
    public async Task Admin_access_enabled_rejects_missing_and_wrong_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Missing, validator.Validate(token: null));
        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("wrong-token"));
        Assert.Equal(401, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader(token: null), validator)));
        Assert.Equal(403, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("wrong-token"), validator)));
    }

    [Fact]
    public void Admin_access_enabled_accepts_correct_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Valid, validator.Validate("admin-token"));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("admin-token"), validator));
    }

    [Fact]
    public async Task Admin_access_rejects_query_string_token_and_accepts_header_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(401, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminQueryToken("admin-token"), validator)));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("admin-token"), validator));
    }

    [Fact]
    public void Admin_access_token_comparison_rejects_prefix_and_extended_tokens()
    {
        // Comparison hashes both sides with SHA-256 before calling FixedTimeEquals,
        // so tokens that are a prefix of, or an extension of, the configured value
        // are rejected without leaking the expected token's byte length via timing.
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("admin-toke"));
        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("admin-token-extra"));
    }

    [Fact]
    public void Room_device_token_comparison_rejects_prefix_and_extended_tokens()
    {
        // Same SHA-256 hash normalisation as admin tokens - length of the submitted
        // value never gates acceptance.
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "room-1-toke"));
        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "room-1-token-extra"));
    }

    [Fact]
    public void Admin_access_protects_reports_and_keeps_board_room_surfaces_open()
    {
        Assert.False(AdminAccessGuard.IsProtectedPath("/reports.html"));
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports"));
        // Admin mutation endpoints nested under /api/reports are also protected.
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports/cycles/mark-exception"));

        Assert.False(AdminAccessGuard.IsProtectedPath("/"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/master.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/doctor.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room-1.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/board"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/rooms/1"));
        // Client error reporting must be unprotected so normal clients can post.
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/client-errors"));
    }

    [Fact]
    public async Task Diagnostic_logger_appends_client_error_entry_to_log_file()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogClientErrorAsync(new ClientErrorEntry
        {
            ServerTimestamp = "2026-06-04T10:00:00Z",
            Message = "TypeError: Cannot read properties of null",
            View = "room",
            RoomId = "1",
            ConnectionStatus = "live"
        });

        var logPath = Path.Combine(logDir, "client-errors.log");
        Assert.True(File.Exists(logPath));
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("TypeError: Cannot read properties of null", content);
        Assert.Contains("room", content);
        Assert.Contains("serverTimestamp", content);
        Assert.Contains("2026-06-04", content);
    }

    [Fact]
    public async Task Diagnostic_logger_appends_room_audit_entry_to_log_file()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = "2026-06-04T10:05:00Z",
            Action = "seat",
            RoomNumber = 2,
            PreviousState = "available",
            NewState = "seated",
            DoctorId = "otte",
            ProcedureCode = "CON",
            Success = true
        });

        var logPath = Path.Combine(logDir, "room-audit.log");
        Assert.True(File.Exists(logPath));
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("seat", content);
        Assert.Contains("available", content);
        Assert.Contains("seated", content);
        Assert.Contains("action", content);
        Assert.Contains("roomNumber", content);
    }

    [Fact]
    public async Task Diagnostic_logger_creates_log_directory_if_missing()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "deep", "nested", "logs");
        // Directory does not exist yet - logger must create it.
        Assert.False(Directory.Exists(logDir));

        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Action = "seat",
            RoomNumber = 1,
            Success = true
        });

        Assert.True(File.Exists(Path.Combine(logDir, "room-audit.log")));
    }

    [Fact]
    public async Task Diagnostic_logger_multiple_entries_are_each_on_their_own_line()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "doctor-arrived", RoomNumber = 1, Success = true });

        var lines = (await File.ReadAllLinesAsync(Path.Combine(logDir, "room-audit.log")))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        Assert.Equal(3, lines.Count);
        Assert.Contains("seat", lines[0]);
        Assert.Contains("ready-for-doctor", lines[1]);
        Assert.Contains("doctor-arrived", lines[2]);
    }

    [Fact]
    public void Client_error_rate_limiter_allows_up_to_limit_then_rejects()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var limiter = new ClientErrorRateLimiter(
            new TestOptionsMonitor<DiagnosticOptions>(new DiagnosticOptions { ClientErrorRateLimitPerMinute = 3 }),
            clock);

        // First 3 requests from the same IP are allowed.
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));

        // 4th request in the same window is blocked.
        Assert.False(limiter.IsAllowed("10.0.0.1"));
        Assert.False(limiter.IsAllowed("10.0.0.1"));

        // A different IP is not affected by the first IP's counter.
        Assert.True(limiter.IsAllowed("10.0.0.2"));

        // After the one-minute window expires, the first IP is allowed again.
        clock.SetUtcNow(now.AddMinutes(1).AddSeconds(1));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.False(limiter.IsAllowed("10.0.0.1")); // new window, same limit
    }

    [Fact]
    public void Client_error_rate_limiter_null_or_empty_ip_is_always_allowed()
    {
        // Unknown/proxied source IPs must never be blocked - they cannot be rate-limited
        // by address and the limiter must not throw on null input.
        var limiter = new ClientErrorRateLimiter(
            new TestOptionsMonitor<DiagnosticOptions>(new DiagnosticOptions { ClientErrorRateLimitPerMinute = 1 }));

        Assert.True(limiter.IsAllowed(null));
        Assert.True(limiter.IsAllowed(null));
        Assert.True(limiter.IsAllowed(""));
        Assert.True(limiter.IsAllowed(""));
    }

    [Fact]
    public async Task Diagnostic_logger_rotates_when_max_file_size_exceeded()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");

        // Set a tiny cap (10 bytes) so the very first write already exceeds the limit
        // and rotation triggers on the second write.
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot, maxFileSizeBytes: 10);

        var logPath = Path.Combine(logDir, "room-audit.log");
        var rotatedPath = logPath + ".1";

        // First write creates the file; no rotation yet (file didn't exist before).
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });
        Assert.True(File.Exists(logPath));
        Assert.False(File.Exists(rotatedPath));

        // Second write: file already exceeds 10 bytes, so rotation fires first.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(rotatedPath));

        // The rotated file holds the first entry; the new file holds the second.
        var rotatedContent = await File.ReadAllTextAsync(rotatedPath);
        var currentContent = await File.ReadAllTextAsync(logPath);
        Assert.Contains("seat", rotatedContent);
        Assert.Contains("ready-for-doctor", currentContent);
    }

    [Fact]
    public async Task Diagnostic_logger_rotation_failure_does_not_block_logging()
    {
        // If the .1 path is occupied by a directory, File.Move will fail.
        // The logger must catch that, write a message to stderr, and still append
        // to the original file so room workflow is never disrupted.
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot, maxFileSizeBytes: 10);

        var logPath = Path.Combine(logDir, "room-audit.log");
        var rotatedPath = logPath + ".1";

        // First write creates the file.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });

        // Block the rotation target so File.Move cannot succeed.
        Directory.CreateDirectory(rotatedPath);

        // Second write: rotation fails silently, but the entry must still be written.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });

        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("ready-for-doctor", content);
    }

    [Fact]
    public void Admin_access_options_allow_disabled_config_without_token()
    {
        var result = ValidateAdminAccessOptions(new AdminAccessOptions { Enabled = false, SharedToken = "" });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Admin_access_options_require_token_when_enabled()
    {
        var result = ValidateAdminAccessOptions(new AdminAccessOptions { Enabled = true, SharedToken = " " });

        Assert.True(result.Failed);
        Assert.Contains("AdminAccessOptions:SharedToken is required", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Admin_access_options_reject_sample_token_in_production()
    {
        var result = ValidateAdminAccessOptions(
            new AdminAccessOptions { Enabled = true, SharedToken = "dev-admin-token" },
            Environments.Production);

        Assert.True(result.Failed);
        Assert.Contains("must not use the dev-admin-token sample value in Production", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Backup_restore_scripts_and_documentation_are_present()
    {
        var root = FindRepositoryRoot();
        var backupScript = Path.Combine(root, "scripts", "Backup-ChairSideSqlite.ps1");
        var restoreScript = Path.Combine(root, "scripts", "Restore-ChairSideSqlite.ps1");
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.True(File.Exists(backupScript));
        Assert.True(File.Exists(restoreScript));
        Assert.Contains(@"C:\ChairSide\Data\chairside.db", readme);
        Assert.Contains(@"C:\ChairSide\Backups", readme);
        Assert.Contains("chairside.db-wal", readme);
        Assert.Contains("Backup-ChairSideSqlite.ps1", readme);
        Assert.Contains("Restore-ChairSideSqlite.ps1", readme);
    }

}
