# ChairSide generated file inventory

Generated output is deterministic. No timestamp is written.

This file is mechanical output from `tools/knowledge-graph/New-ChairSideKnowledgeGraph.ps1`. Review diffs before committing.

| Path | Kind | Discovered symbols | Routes / hubs |
| --- | --- | --- | --- |
| `.github/pull_request_template.md` | Markdown | headings: Knowledge impact, Validation | - |
| `AGENTS.md` | Markdown | headings: AGENTS, ChairSide Project Brief, Project name, Purpose, Scope, Repository orientation and token discipline, Private development knowledge graph, PR knowledge-impact check | - |
| `ChairSide.Board.sln` | Solution | - | - |
| `CLAUDE.md` | Markdown | headings: Claude Code note | - |
| `docs/executive-summary.md` | Markdown | headings: Executive Summary | - |
| `docs/knowledge/_meta/tag-dictionary.md` | Markdown | headings: ChairSide Knowledge Tag Dictionary, 1. Title and purpose, 2. Agent reading order, 3. Source-truth relationship, 4. Canonical tag rules, 5. Canonical project-area tags, 6. Canonical domain-concept tags, 7. Canonical artifact/status tags | - |
| `docs/knowledge/product/non-phi-boundary.md` | Markdown | headings: Non-PHI boundary, Intent, Constraints, Source anchors, Verification notes | - |
| `docs/knowledge/reports/observed-load.md` | Markdown | headings: Observed load, Intent, What it reports, Constraints, Source anchors, Verification notes | - |
| `docs/knowledge/reports/procedure-mix.md` | Markdown | headings: Procedure mix, Intent, What it reports, Constraints, Source anchors, Verification notes | - |
| `docs/knowledge/reports/reporting-population.md` | Markdown | headings: Reporting population, Intent, The population funnel, Constraints, Source anchors, Verification notes | - |
| `docs/knowledge/reports/sedation-as-modifier.md` | Markdown | headings: Sedation as a modifier, Intent, How it is represented, Constraints, Source anchors, Verification notes | - |
| `docs/knowledge/tests/deterministic-stress-fixtures.md` | Markdown | headings: Deterministic stress fixtures, Intent, Profiles, Constraints, Day-offset shift (all-scenarios only), Source anchors, Verification notes | - |
| `docs/knowledge/ui/doctor-view-operational-header.md` | Markdown | headings: Doctor View operational header, Intent, Layout rule, Current-room frame posture, Room counting: assignment-based, not state-filtered, Scope, Source anchors, Verification notes | - |
| `docs/knowledge/workflow/room-lifecycle.md` | Markdown | headings: Room lifecycle, Intent, Lifecycle events and states, Mutation surface and authorization, Conflict handling, Source anchors, Verification notes | - |
| `docs/knowledge-graph/backlog-signals.md` | Markdown | headings: ChairSide knowledge graph backlog signals, High-value future graph expansions, Risks to preserve, Candidate relationships to add later | - |
| `docs/knowledge-graph/chairside.graph.md` | Markdown | headings: ChairSide knowledge graph, Conceptual graph, Core invariants, Current reporting semantics captured by the graph, Known development affordances | - |
| `docs/knowledge-graph/decisions.md` | Markdown | headings: ChairSide durable decisions, Knowledge graph scope, Runtime isolation, Human-readable first, Non-PHI boundary, Reporting UI philosophy, Reporting population semantics | - |
| `docs/knowledge-graph/README.md` | Markdown | headings: ChairSide private knowledge graph, What lives here, What does not live here, Node types, Edge labels, Update rule | - |
| `docs/Production-Pilot-Checklist.md` | Markdown | headings: ChairSide Board Production Pilot Checklist, Server Layout, IIS, Permissions, Publish And Config, Backup And Restore, Access Control Decisions, Room Tablets | - |
| `docs/TrainingDataReset.md` | Markdown | headings: ChairSide Training Data Reset Runbook, Data lifecycle, What the two modes do, What gets backed up, What gets cleared / seeded, Prerequisites, Usage, Training fixture reset (seed synthetic data) | - |
| `docs/ui-cohesion-audit.md` | Markdown | headings: ChairSide UI Cohesion Audit, 1. Summary, 2. Existing visual strengths, 3. Reusable assets and patterns already available, 4. Visual inconsistencies by category, 5. Protected visual semantics, 6. Reuse-first design principles, 7. Proposed design tokens/components to standardize later | - |
| `docs/visual-language.md` | Markdown | headings: Visual Language | - |
| `docs/workflow.md` | Markdown | headings: Workflow | - |
| `README.md` | Markdown | headings: ChairSide Board, Scaffold, Seed Data, Run Locally, IIS Deployment, Reset Demo Data, Deterministic Stress Fixtures (Maintenance), Reports | - |
| `scripts/Backup-ChairSideSqlite.ps1` | PowerShell | - | - |
| `scripts/Reset-ChairSideTrainingData.ps1` | PowerShell | - | - |
| `scripts/Restore-ChairSideSqlite.ps1` | PowerShell | - | - |
| `src/ChairSide.Board/appsettings.json` | Json | - | - |
| `src/ChairSide.Board/appsettings.Production.json` | Json | - | - |
| `src/ChairSide.Board/ChairSide.Board.csproj` | Project | - | - |
| `src/ChairSide.Board/Hubs/BoardHub.cs` | CSharp | types: BoardHub | - |
| `src/ChairSide.Board/Options/AdminAccessOptions.cs` | CSharp | types: AdminAccessOptions, AdminAccessOptionsValidator<br>methods: Validate | - |
| `src/ChairSide.Board/Options/BoardOptions.cs` | CSharp | types: BoardOptions | - |
| `src/ChairSide.Board/Options/BoardPersistenceOptions.cs` | CSharp | types: BoardPersistenceOptions | - |
| `src/ChairSide.Board/Options/BoardThresholdOptions.cs` | CSharp | types: BoardThresholdOptions | - |
| `src/ChairSide.Board/Options/BoardUiOptions.cs` | CSharp | types: BoardUiOptions | - |
| `src/ChairSide.Board/Options/DiagnosticOptions.cs` | CSharp | types: DiagnosticOptions | - |
| `src/ChairSide.Board/Options/DoctorRosterOptions.cs` | CSharp | types: DoctorRosterOptions, DoctorRosterItem, DoctorRosterOptionsValidator<br>methods: DefaultDoctors, Validate | - |
| `src/ChairSide.Board/Options/ProcedureRosterOptions.cs` | CSharp | types: AllocationBehaviors, ProcedureRosterOptions, ProcedureRosterItem, ProcedureRosterOptionsValidator<br>methods: IsValid, DefaultProcedures, Validate | - |
| `src/ChairSide.Board/Options/RoomDeviceBindingOptions.cs` | CSharp | types: RoomDeviceBindingOptions | - |
| `src/ChairSide.Board/Options/RoomDeviceBindingOptionsValidator.cs` | CSharp | types: RoomDeviceBindingOptionsValidator<br>methods: Validate | - |
| `src/ChairSide.Board/Options/RoomExpirationOptions.cs` | CSharp | types: RoomExpirationOptions | - |
| `src/ChairSide.Board/Program.cs` | CSharp | types: SeatRoomRequest, UpdateAssignmentRequest, MarkExceptionRequest, DoctorArrivedConflictResponse, ResolveDoctorArrivalConflictRequest, ClientErrorRequest, DoctorArrivalConflictEndpointHandler, AuditRequestContext<br>methods: From, Build | routes: /boardHub, /api/board, /api/reports, /api/dev/seed-report-data, /api/reports/cycles/mark-exception, /api/reports/cycles/{completedCycleId:long}/confirm-exclusion, /api/rooms/{roomNumber:int}, /api/client-errors<br>hubs: BoardHub |
| `src/ChairSide.Board/Services/AdminAccessGuard.cs` | CSharp | types: AdminAccessGuard<br>methods: IsProtectedPath, ValidateRequest | - |
| `src/ChairSide.Board/Services/AdminAccessTokenValidator.cs` | CSharp | types: AdminAccessTokenValidator, AdminAccessTokenValidationResult<br>methods: Validate, FixedTimeEquals | - |
| `src/ChairSide.Board/Services/ClientErrorRateLimiter.cs` | CSharp | types: ClientErrorRateLimiter, Bucket<br>methods: IsAllowed | - |
| `src/ChairSide.Board/Services/DemoBoardStore.cs` | CSharp | types: ExceptionReasons, ReportingExceptionReasons, DemoBoardStore, BoardSnapshot, Doctor, ProcedureCategory, RoomStatus, RoomEvent<br>methods: GetSnapshot, GetRoom, IsConfiguredRoom, SeatRoom, UpdateAssignment, CancelSeating, MarkReadyForDoctor, MarkDoctorArrived | - |
| `src/ChairSide.Board/Services/DiagnosticLogger.cs` | CSharp | types: DiagnosticLogger, ClientErrorEntry, RoomAuditEntry<br>methods: LogClientErrorAsync, LogRoomAuditAsync, TryRotate, TryCreateDirectory, ResolveLogDirectory | - |
| `src/ChairSide.Board/Services/MaintenanceCommands.cs` | CSharp | types: MaintenanceOutcome, MaintenanceResolution, MaintenanceCommands<br>methods: Resolve, IsProductionForbidden, GetFlagValue | - |
| `src/ChairSide.Board/Services/ProjectionAssumptionChecklist.cs` | CSharp | types: ProjectionAssumptionChecklist, ObservedScheduleFitInputSummary, ProjectionAssumptionRequirement, ProjectionAssumptionChecklistBuilder<br>methods: Build, RequireNonBlank, BuildObservedSummary, BuildRequiredAssumptions, BuildMissingInputs | - |
| `src/ChairSide.Board/Services/ReportTrendSnapshot.cs` | CSharp | types: ReportTrendSnapshot, ReportTrendBucket, ReportTrendSnapshotBuilder<br>methods: BuildWeekly, WeekStart, FormatDate, Average, Median | - |
| `src/ChairSide.Board/Services/RoomDeviceBindingGuard.cs` | CSharp | types: RoomDeviceBindingGuard<br>methods: ValidateMutationRequest | - |
| `src/ChairSide.Board/Services/RoomDeviceTokenValidator.cs` | CSharp | types: RoomDeviceTokenValidator, RoomDeviceTokenValidationResult<br>methods: Validate, FixedTimeEquals | - |
| `src/ChairSide.Board/Services/RoomExpirationService.cs` | CSharp | types: RoomExpirationService<br>methods: CheckExpirationsAsync | - |
| `src/ChairSide.Board/Services/RoomMutationRequestValidator.cs` | CSharp | types: RoomMutationRequestValidator<br>methods: ValidateDoctorAndProcedure | - |
| `src/ChairSide.Board/Services/ScheduleFitCalculator.cs` | CSharp | types: ScheduleFitCalculator, ScheduleFitResult<br>methods: Calculate | - |
| `src/ChairSide.Board/Services/ScheduleFitReport.cs` | CSharp | types: ScheduleFitReport, ScheduleFitReportBuilder<br>methods: Build | - |
| `src/ChairSide.Board/Services/SqliteBoardRepository.cs` | CSharp | types: SqliteBoardRepository<br>methods: HasAnyRoomRows, LoadRooms, EnsureConfiguredRooms, ClearCompletedCycles, ResetActiveRooms, SaveRooms, SaveRoom, LoadCompletedCycles | - |
| `src/ChairSide.Board/wwwroot/assets/icons/README.md` | Markdown | headings: Procedure icons, Tabler Icons license (MIT), Note | - |
| `src/ChairSide.Board/wwwroot/board.js` | JavaScript | functions: getRoomNumber, getRoomToken, loadVersionBadge, boot, loadBoard, loadReports, initDateRange, reportsRequestUrl | - |
| `src/ChairSide.Board/wwwroot/doctor.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/index.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/master.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/reports.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/room.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/room-1.html` | Html | - | - |
| `src/ChairSide.Board/wwwroot/signalr-lite.js` | JavaScript | functions: toWebSocketUrl | - |
| `src/ChairSide.Board/wwwroot/styles.css` | Css | css vars: --bg, --ink, --muted, --line, --panel, --empty, --active-doctor, --neutral-100 | - |
| `src/ChairSide.Board/wwwroot/workshop.html` | Html | - | - |
| `tests/ChairSide.Board.Tests/BoardStoreTests.cs` | CSharp | types: BoardStoreTests, is, persisted, StoreContext, TestWorkspace, TestWebHostEnvironment, TestOptionsMonitor, ManualTimeProvider<br>methods: Lifecycle_actions_preserve_expected_state_and_report_behavior, Active_seated_room_survives_store_restart, Completed_report_survives_store_restart, Stale_elapsed_ready_for_doctor_room_reloads_as_stale, Doctor_in_room_and_turnover_rooms_survive_reload_without_wait_state_downgrade, Turnover_seconds_calculated_from_doctor_complete_to_room_available, Room_status_preserves_seated_at_through_doctor_in_room_and_turnover, Seated_room_does_not_escalate_to_aging_or_stale_regardless_of_elapsed_time | - |
| `tests/ChairSide.Board.Tests/ChairSide.Board.Tests.csproj` | Project | - | - |
| `tests/ChairSide.Board.Tests/ProjectionAssumptionChecklistTests.cs` | CSharp | types: ProjectionAssumptionChecklistTests<br>methods: Build_rejects_null_preset_id, Build_rejects_blank_preset_id, Build_rejects_null_preset_name, Build_rejects_blank_preset_name, Output_status_is_always_not_computed, Safety_warning_states_observed_slack_is_not_automatically_usable_capacity, Schedule_fit_totals_are_copied_into_observed_summary_without_projection_math, Missing_schedule_fit_produces_empty_observed_summary_and_missing_input | - |
| `tests/ChairSide.Board.Tests/ReportTrendSnapshotTests.cs` | CSharp | types: ReportTrendSnapshotTests<br>methods: Empty_population_returns_empty_weekly_snapshot, Cycles_group_into_monday_start_utc_weeks_by_doctor_complete_at, Median_seated_to_doctor_is_correct_for_odd_counts, Median_seated_to_doctor_is_correct_for_even_counts, Average_seated_to_doctor_is_correct, Median_turnover_is_correct, Average_turnover_is_correct, Turnover_count_uses_only_cycles_with_usable_turnover_values | - |
| `tests/ChairSide.Board.Tests/ScheduleFitCalculatorTests.cs` | CSharp | types: ScheduleFitCalculatorTests<br>methods: Cycle, Empty_cycle_list_returns_zero_result_and_null_utilization, At_expected_cycle_has_zero_variance_slack_and_debt, Over_expected_cycle_contributes_to_debt_not_slack, Under_expected_cycle_contributes_to_slack_not_debt, Mixed_cycles_keep_signed_net_variance_while_tracking_slack_and_debt_separately, Block_counts_use_the_supplied_block_size, Default_block_size_divides_minute_totals_by_ten | - |
| `tests/ChairSide.Board.Tests/ScheduleFitReportTests.cs` | CSharp | types: ScheduleFitReportTests<br>methods: Cycle, Empty_population_returns_zero_counts_and_zero_overall_result, Mixed_over_and_under_cycles_delegate_to_the_calculator, Included_count_exceeds_fit_count_when_some_cycles_are_not_allocation_calculable, Null_defensive_entries_do_not_count_as_included_cycles, Custom_block_size_flows_through_to_overall_block_totals, Invalid_block_size_propagates_argument_out_of_range, Schedule_fit_totals_agree_with_allocation_variance_summary_for_standard_population | - |
| `tools/knowledge-graph/New-ChairSideKnowledgeGraph.ps1` | PowerShell | - | - |
