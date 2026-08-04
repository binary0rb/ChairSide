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
    private static void InsertLegacyCompletedCycleRow(SqliteConnection connection, int roomId, string seatedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO completed_room_cycles (
                room_id, assigned_doctor_id, assigned_doctor_display_name,
                procedure_code, procedure_category, seated_at,
                seated_to_doctor_seconds, final_wait_state, created_at, updated_at
            ) VALUES (
                $roomId, 'otte', 'Dr. Otte', 'CON', 'Consult', $seatedAt,
                300, 'ReadyForDoctor', $now, $now
            );
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$seatedAt", seatedAt);
        cmd.Parameters.AddWithValue("$now", "2026-06-01T12:00:00.0000000+00:00");
        cmd.ExecuteNonQuery();
    }

    private static readonly HashSet<string> AllowedActiveRoomColumns =
    [
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "state",
        "seated_at",
        "aging_started_at",
        "stale_started_at",
        "ready_for_doctor_at",
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "prestage_started_at",
        "episode_id",
        "sedation_state",
        "expected_allocation_state",
        "expected_allocation_suggested_units",
        "expected_allocation_confirmed_units",
        "is_add_on",
        "active_ready_handoff_id",
        "accepted_ready_handoff_id",
        "updated_at"
    ];

    private static readonly HashSet<string> AllowedCompletedCycleColumns =
    [
        "id",
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "seated_at",
        "ready_for_doctor_at",
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "seated_to_doctor_seconds",
        "prep_seconds",
        "ready_to_doctor_seconds",
        "doctor_in_room_seconds",
        "turnover_seconds",
        "total_room_cycle_seconds",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "final_wait_state",
        "aging_threshold_reached",
        "stale_threshold_reached",
        "is_exception",
        "requires_review",
        "exception_reason",
        "review_status",
        "suggested_action",
        "reviewed_at",
        "reviewed_by",
        "prestage_started_at",
        "episode_id",
        "accepted_ready_handoff_id",
        "is_add_on",
        "created_at",
        "updated_at"
    ];

    private static readonly HashSet<string> AllowedAbortedAssignmentColumns =
    [
        "id",
        "episode_id",
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "sedation_state",
        "expected_allocation_state",
        "expected_allocation_suggested_units",
        "expected_allocation_confirmed_units",
        "terminal_ready_handoff_id",
        "is_add_on",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "prestage_started_at",
        "seated_at",
        "ready_for_doctor_at",
        "terminated_at",
        "terminated_from_state",
        "termination_kind",
        "cancellation_reason",
        "is_exception",
        "requires_review",
        "exception_reason",
        "review_status",
        "suggested_action",
        "reviewed_at",
        "reviewed_by",
        "created_at",
        "updated_at"
    ];

    private static bool ContainsBannedPhiTerm(string columnName)
    {
        string[] bannedTerms =
        [
            "patient",
            "dob",
            "date_of_birth",
            "chart",
            "diagnosis",
            "insurance",
            "billing",
            "medical",
            "note"
        ];

        return bannedTerms.Any(term => columnName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void ExecuteSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Minimal valid aborted-assignment record for repository tests that do not care about the full
    // snapshot (idempotency, distinct-episode, and combined-write coverage). Phase timestamps beyond
    // the prestage start are left null (a prestage-phase cancel).
    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

}
