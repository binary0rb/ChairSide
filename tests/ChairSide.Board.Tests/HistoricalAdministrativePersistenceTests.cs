using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalAdministrativePersistenceTests
{
    [Fact]
    public void Fresh_database_creates_bounded_administrative_schema_and_indexes()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);

        using var connection = OpenConnection(context.DatabasePath);
        var tables = ReadNames(connection, "table");
        var indexes = ReadNames(connection, "index");

        Assert.Contains("historical_encounter_admin_state", tables);
        Assert.Contains("historical_encounter_ledger", tables);
        Assert.DoesNotContain("historical_encounters", tables);
        Assert.Contains("ix_historical_admin_state_disposition", indexes);
        Assert.Contains("ix_historical_encounter_ledger_chronology", indexes);
        Assert.Contains("ux_historical_encounter_ledger_legacy_import", indexes);

        var stateColumns = ReadColumns(connection, "historical_encounter_admin_state");
        Assert.Contains("administrative_revision", stateColumns);
        Assert.Contains("override_doctor_id", stateColumns);
        Assert.Contains("override_procedure_code", stateColumns);
        Assert.Contains("override_sedation_state", stateColumns);
        Assert.Contains("override_is_add_on", stateColumns);
        Assert.Contains("override_expected_allocation_confirmed_units", stateColumns);
        Assert.DoesNotContain("flagged_at", stateColumns);
        Assert.DoesNotContain("detected_at", stateColumns);
    }

    [Fact]
    public void Typed_sources_with_the_same_numeric_id_remain_distinct_and_overlays_round_trip()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        long completedId;
        long abortedId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            completedId = InsertCompletedSource(connection, 1, DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
            abortedId = InsertAbortedSource(connection, "episode-same-id", DateTimeOffset.Parse("2026-08-01T12:30:00Z"));
        }
        Assert.Equal(completedId, abortedId);

        var completedKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, completedId);
        var abortedKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, abortedId);
        var occurredAt = DateTimeOffset.Parse("2026-08-02T10:00:00Z");
        var completedState = CreateState(
            completedKey,
            HistoricalAdministrativeDispositions.NoAnomaly,
            revision: 0,
            overrideDoctorId: "gibson-inactive",
            overrideProcedureCode: "IMP",
            overrideSedationState: SedationState.EligibleYes,
            overrideIsAddOn: true,
            overrideAllocationState: ExpectedAllocationState.ConfirmedAdjustedValue,
            overrideSuggestedUnits: 2,
            overrideConfirmedUnits: 3);
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            completedState,
            CreateEvent(
                completedKey,
                HistoricalAdministrativeLedgerEventTypes.MetadataCorrected,
                occurredAt,
                revision: 0));
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(abortedKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(
                abortedKey,
                HistoricalAdministrativeLedgerEventTypes.SystemFinding,
                occurredAt,
                revision: 0));

        var loadedCompleted = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(completedKey));
        var loadedAborted = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(abortedKey));
        Assert.Equal(HistoricalAdministrativeDispositions.NoAnomaly, loadedCompleted.Disposition);
        Assert.Equal("gibson-inactive", loadedCompleted.OverrideDoctorId);
        Assert.Equal("IMP", loadedCompleted.OverrideProcedureCode);
        Assert.Equal(SedationState.EligibleYes, loadedCompleted.OverrideSedationState);
        Assert.True(loadedCompleted.OverrideIsAddOn);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, loadedCompleted.OverrideExpectedAllocationState);
        Assert.Equal(2, loadedCompleted.OverrideExpectedAllocationSuggestedUnits);
        Assert.Equal(3, loadedCompleted.OverrideExpectedAllocationConfirmedUnits);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, loadedAborted.Disposition);

        using var verifyConnection = OpenConnection(context.DatabasePath);
        using var sourceCommand = verifyConnection.CreateCommand();
        sourceCommand.CommandText = "SELECT assigned_doctor_id, procedure_code FROM completed_room_cycles WHERE id = $id;";
        sourceCommand.Parameters.AddWithValue("$id", completedId);
        using var reader = sourceCommand.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("otte", reader.GetString(0));
        Assert.Equal("CON", reader.GetString(1));
    }

    [Fact]
    public void Ledger_reads_are_per_encounter_bounded_and_stable_for_equal_timestamps()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        long completedId;
        long abortedId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            completedId = InsertCompletedSource(connection, 1, DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
            abortedId = InsertAbortedSource(connection, "episode-ledger-boundary", DateTimeOffset.Parse("2026-08-03T12:30:00Z"));
        }
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, completedId);
        var otherKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, abortedId);
        var occurredAt = DateTimeOffset.Parse("2026-08-04T08:00:00Z");
        var note500 = new string('x', 500);

        var first = context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(key, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(key, HistoricalAdministrativeLedgerEventTypes.ManualFlag, occurredAt, revision: 0));
        var second = context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(key, HistoricalAdministrativeDispositions.NeedsReview, revision: 1),
            CreateEvent(
                key,
                HistoricalAdministrativeLedgerEventTypes.NoteAdded,
                occurredAt,
                revision: 1,
                adminNote: note500));
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(otherKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(otherKey, HistoricalAdministrativeLedgerEventTypes.SystemFinding, occurredAt, revision: 0));

        var page = context.Repository.LoadHistoricalAdministrativeLedger(key, offset: 0, limit: 1);
        Assert.Single(page.Rows);
        Assert.Equal(2, page.TotalMatchingCount);
        Assert.True(page.HasMore);
        Assert.Equal(first.LedgerEvent.LedgerId, page.Rows[0].LedgerId);

        var all = context.Repository.LoadHistoricalAdministrativeLedger(key, offset: 0, limit: 10);
        Assert.Equal([first.LedgerEvent.LedgerId, second.LedgerEvent.LedgerId], all.Rows.Select(row => row.LedgerId));
        Assert.Equal(note500, all.Rows[1].AdminNote);

        var tooLong = CreateEvent(
            key,
            HistoricalAdministrativeLedgerEventTypes.NoteAdded,
            occurredAt,
            revision: 2,
            adminNote: new string('x', 501));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Repository.PersistHistoricalAdministrativeStateAndLedger(
                CreateState(key, HistoricalAdministrativeDispositions.NeedsReview, revision: 2),
                tooLong));
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).TotalMatchingCount);
        Assert.Equal(1, context.Repository.LoadHistoricalAdministrativeLedger(otherKey, 0, 10).TotalMatchingCount);
    }

    [Fact]
    public void Administrative_attachment_rejects_invalid_and_nonexistent_sources()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var invalidKey = new HistoricalEncounterKey("Completed", 1);
        var missingKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, 999);
        var occurredAt = DateTimeOffset.Parse("2026-08-05T08:00:00Z");

        Assert.Throws<ArgumentException>(() =>
            context.Repository.PersistHistoricalAdministrativeStateAndLedger(
                CreateState(invalidKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
                CreateEvent(invalidKey, HistoricalAdministrativeLedgerEventTypes.ManualFlag, occurredAt, revision: 0)));
        Assert.Throws<InvalidOperationException>(() =>
            context.Repository.PersistHistoricalAdministrativeStateAndLedger(
                CreateState(missingKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
                CreateEvent(missingKey, HistoricalAdministrativeLedgerEventTypes.ManualFlag, occurredAt, revision: 0)));
        Assert.Throws<ArgumentException>(() =>
            context.Repository.LoadHistoricalAdministrativeLedger(new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, 0), 0, 10));
    }

    [Fact]
    public void Legacy_migration_maps_both_sources_conservatively_and_preserves_only_known_provenance()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var reviewedAt = DateTimeOffset.Parse("2026-07-31T20:15:00Z");
        var importedAt = DateTimeOffset.Parse("2026-08-06T09:00:00Z");
        long ordinaryId;
        long pendingId;
        long confirmedId;
        long contradictoryId;
        long malformedId;
        long abortedConfirmedId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            ordinaryId = InsertCompletedSource(
                connection,
                1,
                DateTimeOffset.Parse("2026-07-30T10:00:00Z"),
                isException: false,
                requiresReview: false,
                reviewStatus: ReviewStatuses.PendingReview);
            pendingId = InsertCompletedSource(
                connection,
                2,
                DateTimeOffset.Parse("2026-07-30T11:00:00Z"),
                isException: true,
                requiresReview: true,
                exceptionReason: ExceptionReasons.AfterHoursSweep,
                reviewStatus: ReviewStatuses.PendingReview);
            confirmedId = InsertCompletedSource(
                connection,
                3,
                DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
                isException: true,
                requiresReview: false,
                exceptionReason: ExceptionReasons.ManualReview,
                reviewStatus: ReviewStatuses.Reviewed,
                reviewedAt: reviewedAt.ToString("O"),
                reviewedBy: ExceptionReviewers.LocalAdmin);
            contradictoryId = InsertCompletedSource(
                connection,
                4,
                DateTimeOffset.Parse("2026-07-30T13:00:00Z"),
                isException: true,
                requiresReview: false,
                exceptionReason: "UnclassifiedLegacyReason",
                reviewStatus: ReviewStatuses.PendingReview,
                reviewedAt: reviewedAt.ToString("O"));
            malformedId = InsertCompletedSource(
                connection,
                5,
                DateTimeOffset.Parse("2026-07-30T14:00:00Z"),
                isException: true,
                requiresReview: false,
                exceptionReason: ExceptionReasons.ManualReview,
                reviewStatus: ReviewStatuses.Reviewed,
                reviewedAt: "not-a-timestamp",
                reviewedBy: "arbitrary-account-name");
            abortedConfirmedId = InsertAbortedSource(
                connection,
                "episode-legacy-confirmed",
                DateTimeOffset.Parse("2026-07-30T15:00:00Z"),
                isException: true,
                requiresReview: false,
                exceptionReason: ExceptionReasons.AfterHoursSweep,
                reviewStatus: ReviewStatuses.Reviewed,
                reviewedAt: reviewedAt.ToString("O"),
                reviewedBy: ExceptionReviewers.LocalAdmin);

            SqliteBoardSchema.MigrateLegacyAdministrativeState(connection, importedAt);

            var completedEvidence = ReadLegacyReviewEvidence(connection, "completed_room_cycles", confirmedId);
            Assert.True(completedEvidence.IsException);
            Assert.False(completedEvidence.RequiresReview);
            Assert.Equal(ExceptionReasons.ManualReview, completedEvidence.ExceptionReason);
            Assert.Equal(ReviewStatuses.Reviewed, completedEvidence.ReviewStatus);
            Assert.Equal(reviewedAt.ToString("O"), completedEvidence.ReviewedAt);
            Assert.Equal(ExceptionReviewers.LocalAdmin, completedEvidence.ReviewedBy);

            var abortedEvidence = ReadLegacyReviewEvidence(connection, "aborted_room_assignments", abortedConfirmedId);
            Assert.True(abortedEvidence.IsException);
            Assert.False(abortedEvidence.RequiresReview);
            Assert.Equal(ExceptionReasons.AfterHoursSweep, abortedEvidence.ExceptionReason);
            Assert.Equal(ReviewStatuses.Reviewed, abortedEvidence.ReviewStatus);
            Assert.Equal(reviewedAt.ToString("O"), abortedEvidence.ReviewedAt);
            Assert.Equal(ExceptionReviewers.LocalAdmin, abortedEvidence.ReviewedBy);
        }

        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, ordinaryId)));

        var pending = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(
                new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, pendingId)));
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, pending.Disposition);
        Assert.Equal(HistoricalAdministrativeReasonSources.System, pending.ReasonSource);
        Assert.Equal(0, pending.AdministrativeRevision);

        var confirmedKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, confirmedId);
        var confirmed = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(confirmedKey));
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, confirmed.Disposition);
        Assert.Equal(reviewedAt, confirmed.KnownReviewedAt);
        Assert.Equal(HistoricalAdministrativeActorClasses.LocalAdmin, confirmed.KnownReviewedActorClass);
        Assert.Equal(HistoricalAdministrativeReasonSources.LocalAdmin, confirmed.ReasonSource);

        var contradictory = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(
                new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, contradictoryId)));
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, contradictory.Disposition);
        Assert.Equal(reviewedAt, contradictory.KnownReviewedAt);
        Assert.Null(contradictory.KnownReviewedActorClass);
        Assert.Equal(HistoricalAdministrativeReasonSources.Legacy, contradictory.ReasonSource);

        var malformed = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(
                new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, malformedId)));
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, malformed.Disposition);
        Assert.Null(malformed.KnownReviewedAt);
        Assert.Null(malformed.KnownReviewedActorClass);

        var aborted = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(
                new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, abortedConfirmedId)));
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, aborted.Disposition);

        var importEvent = Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(confirmedKey, 0, 10).Rows);
        Assert.Equal(HistoricalAdministrativeLedgerEventTypes.LegacyStateImported, importEvent.EventType);
        Assert.Equal(importedAt, importEvent.OccurredAt);
        Assert.Equal(HistoricalAdministrativeActorClasses.System, importEvent.ActorClass);
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, importEvent.NewValue);
        Assert.Null(importEvent.PreviousValue);
        Assert.Null(importEvent.AdminNote);
    }

    [Fact]
    public void Repeated_migration_preserves_newer_projection_revision_overlays_and_original_import_event()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var firstImport = DateTimeOffset.Parse("2026-08-07T08:00:00Z");
        var administrativeChange = DateTimeOffset.Parse("2026-08-07T09:00:00Z");
        var repeatedImport = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        long sourceId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            sourceId = InsertCompletedSource(
                connection,
                1,
                DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
                isException: true,
                requiresReview: true,
                exceptionReason: ExceptionReasons.ManualReview);
            SqliteBoardSchema.MigrateLegacyAdministrativeState(connection, firstImport);
        }
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, sourceId);
        var newerState = CreateState(
            key,
            HistoricalAdministrativeDispositions.NoAnomaly,
            revision: 7,
            overrideDoctorId: "historical-doctor");
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            newerState,
            CreateEvent(
                key,
                HistoricalAdministrativeLedgerEventTypes.MetadataCorrected,
                administrativeChange,
                revision: 7));

        using (var connection = OpenConnection(context.DatabasePath))
        {
            SqliteBoardSchema.MigrateLegacyAdministrativeState(connection, repeatedImport);
        }

        var loaded = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(HistoricalAdministrativeDispositions.NoAnomaly, loaded.Disposition);
        Assert.Equal(7, loaded.AdministrativeRevision);
        Assert.Equal("historical-doctor", loaded.OverrideDoctorId);
        var ledger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;
        Assert.Equal(2, ledger.Count);
        var import = Assert.Single(ledger.Where(row =>
            row.EventType == HistoricalAdministrativeLedgerEventTypes.LegacyStateImported));
        Assert.Equal(firstImport, import.OccurredAt);
    }

    [Fact]
    public void Administrative_state_and_ledger_rollback_together_on_injected_failure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        long sourceId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            sourceId = InsertCompletedSource(connection, 1, DateTimeOffset.Parse("2026-08-08T08:00:00Z"));
        }
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, sourceId);
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(key, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(
                key,
                HistoricalAdministrativeLedgerEventTypes.ManualFlag,
                DateTimeOffset.Parse("2026-08-08T09:00:00Z"),
                revision: 0));

        Assert.Throws<InvalidOperationException>(() =>
            context.Repository.PersistHistoricalAdministrativeStateAndLedger(
                CreateState(key, HistoricalAdministrativeDispositions.ClearedForReporting, revision: 1),
                CreateEvent(
                    key,
                    HistoricalAdministrativeLedgerEventTypes.ClearedForReporting,
                    DateTimeOffset.Parse("2026-08-08T10:00:00Z"),
                    revision: 1),
                afterStatePersisted: () => throw new InvalidOperationException("injected ledger failure")));

        var loaded = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, loaded.Disposition);
        Assert.Equal(0, loaded.AdministrativeRevision);
        Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Legacy_migration_rolls_back_projection_and_ledger_on_injected_failure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        long sourceId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            sourceId = InsertCompletedSource(
                connection,
                1,
                DateTimeOffset.Parse("2026-08-09T08:00:00Z"),
                isException: true,
                requiresReview: true);

            Assert.Throws<InvalidOperationException>(() =>
                SqliteBoardSchema.MigrateLegacyAdministrativeState(
                    connection,
                    DateTimeOffset.Parse("2026-08-09T09:00:00Z"),
                    afterProjectionRowsImported: () => throw new InvalidOperationException("injected migration failure")));
        }

        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, sourceId);
        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Empty(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Training")]
    public void Reset_cleanup_removes_completed_administration_without_orphaning_preserved_aborted_history(
        string environmentName)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName);
        long completedId;
        long abortedId;
        using (var connection = OpenConnection(context.DatabasePath))
        {
            completedId = InsertCompletedSource(connection, 90, DateTimeOffset.Parse("2026-08-10T08:00:00Z"));
            abortedId = InsertAbortedSource(connection, $"episode-reset-{environmentName}", DateTimeOffset.Parse("2026-08-10T09:00:00Z"));
        }
        var completedKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, completedId);
        var abortedKey = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, abortedId);
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(completedKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(completedKey, HistoricalAdministrativeLedgerEventTypes.ManualFlag, DateTimeOffset.UtcNow, revision: 0));
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            CreateState(abortedKey, HistoricalAdministrativeDispositions.NeedsReview, revision: 0),
            CreateEvent(abortedKey, HistoricalAdministrativeLedgerEventTypes.SystemFinding, DateTimeOffset.UtcNow, revision: 0));

        context.Repository.ResetMaintenanceState(roomCount: 3);

        Assert.Null(context.Repository.LoadHistoricalEncounter(completedKey));
        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(completedKey));
        Assert.Empty(context.Repository.LoadHistoricalAdministrativeLedger(completedKey, 0, 10).Rows);
        Assert.NotNull(context.Repository.LoadHistoricalEncounter(abortedKey));
        Assert.NotNull(context.Repository.LoadHistoricalAdministrativeState(abortedKey));
        Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(abortedKey, 0, 10).Rows);
    }

    [Fact]
    public void Production_repeated_initialization_imports_once_and_retains_administrative_history_unchanged()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        _ = StoreContext.Create(workspace, Environments.Production, databasePath: databasePath);
        long sourceId;
        using (var connection = OpenConnection(databasePath))
        {
            sourceId = InsertCompletedSource(
                connection,
                1,
                DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
                isException: true,
                requiresReview: true,
                exceptionReason: ExceptionReasons.ManualReview);
        }
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, sourceId);
        var second = StoreContext.Create(workspace, Environments.Production, databasePath: databasePath);
        var importedState = Assert.IsType<HistoricalEncounterAdministrativeState>(
            second.Repository.LoadHistoricalAdministrativeState(key));
        var importedEvent = Assert.Single(second.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);

        var third = StoreContext.Create(workspace, Environments.Production, databasePath: databasePath);
        Assert.Equal(importedState, third.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(importedEvent, Assert.Single(third.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows));
    }

    private static HistoricalEncounterAdministrativeState CreateState(
        HistoricalEncounterKey key,
        string disposition,
        int revision,
        string? overrideDoctorId = null,
        string? overrideProcedureCode = null,
        SedationState? overrideSedationState = null,
        bool? overrideIsAddOn = null,
        ExpectedAllocationState? overrideAllocationState = null,
        int? overrideSuggestedUnits = null,
        int? overrideConfirmedUnits = null) =>
        new(
            key,
            disposition,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            OverrideDoctorId: overrideDoctorId,
            OverrideProcedureCode: overrideProcedureCode,
            OverrideSedationState: overrideSedationState,
            OverrideIsAddOn: overrideIsAddOn,
            OverrideExpectedAllocationState: overrideAllocationState,
            OverrideExpectedAllocationSuggestedUnits: overrideSuggestedUnits,
            OverrideExpectedAllocationConfirmedUnits: overrideConfirmedUnits,
            AdministrativeRevision: revision);

    private static HistoricalEncounterAdministrativeLedgerEvent CreateEvent(
        HistoricalEncounterKey key,
        string eventType,
        DateTimeOffset occurredAt,
        int revision,
        string? adminNote = null) =>
        new(
            LedgerId: 0,
            Key: key,
            EventType: eventType,
            OccurredAt: occurredAt,
            ActorClass: eventType == HistoricalAdministrativeLedgerEventTypes.SystemFinding
                ? HistoricalAdministrativeActorClasses.System
                : HistoricalAdministrativeActorClasses.LocalAdmin,
            ReasonSource: eventType == HistoricalAdministrativeLedgerEventTypes.SystemFinding
                ? HistoricalAdministrativeReasonSources.System
                : HistoricalAdministrativeReasonSources.LocalAdmin,
            StructuredReason: null,
            PreviousValue: null,
            NewValue: null,
            AdminNote: adminNote,
            AdministrativeRevision: revision);

    private static long InsertCompletedSource(
        SqliteConnection connection,
        int roomId,
        DateTimeOffset seatedAt,
        bool isException = false,
        bool requiresReview = false,
        string? exceptionReason = null,
        string reviewStatus = ReviewStatuses.PendingReview,
        string? reviewedAt = null,
        string? reviewedBy = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO completed_room_cycles (
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                seated_at,
                seated_to_doctor_seconds,
                final_wait_state,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                reviewed_at,
                reviewed_by,
                created_at,
                updated_at
            )
            VALUES (
                $roomId,
                'otte',
                'Dr. Otte',
                'CON',
                'Consult',
                $seatedAt,
                300,
                'ReadyForDoctor',
                $isException,
                $requiresReview,
                $exceptionReason,
                $reviewStatus,
                $reviewedAt,
                $reviewedBy,
                $createdAt,
                $createdAt
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$roomId", roomId);
        command.Parameters.AddWithValue("$seatedAt", seatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$isException", isException ? 1 : 0);
        command.Parameters.AddWithValue("$requiresReview", requiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$exceptionReason", (object?)exceptionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewStatus", reviewStatus);
        command.Parameters.AddWithValue("$reviewedAt", (object?)reviewedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewedBy", (object?)reviewedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", seatedAt.ToUniversalTime().ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long InsertAbortedSource(
        SqliteConnection connection,
        string episodeId,
        DateTimeOffset terminatedAt,
        bool isException = false,
        bool requiresReview = false,
        string? exceptionReason = null,
        string reviewStatus = ReviewStatuses.PendingReview,
        string? reviewedAt = null,
        string? reviewedBy = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aborted_room_assignments (
                episode_id,
                room_id,
                terminated_at,
                terminated_from_state,
                termination_kind,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                reviewed_at,
                reviewed_by,
                created_at,
                updated_at
            )
            VALUES (
                $episodeId,
                1,
                $terminatedAt,
                'prestaging',
                'StaffCanceled',
                $isException,
                $requiresReview,
                $exceptionReason,
                $reviewStatus,
                $reviewedAt,
                $reviewedBy,
                $terminatedAt,
                $terminatedAt
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$episodeId", episodeId);
        command.Parameters.AddWithValue("$terminatedAt", terminatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$isException", isException ? 1 : 0);
        command.Parameters.AddWithValue("$requiresReview", requiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$exceptionReason", (object?)exceptionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewStatus", reviewStatus);
        command.Parameters.AddWithValue("$reviewedAt", (object?)reviewedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewedBy", (object?)reviewedBy ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static HashSet<string> ReadNames(SqliteConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static (
        bool IsException,
        bool RequiresReview,
        string? ExceptionReason,
        string ReviewStatus,
        string? ReviewedAt,
        string? ReviewedBy) ReadLegacyReviewEvidence(
            SqliteConnection connection,
            string tableName,
            long sourceRecordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT is_exception, requires_review, exception_reason, review_status, reviewed_at, reviewed_by
            FROM {tableName}
            WHERE id = $sourceRecordId;
            """;
        command.Parameters.AddWithValue("$sourceRecordId", sourceRecordId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }
}
