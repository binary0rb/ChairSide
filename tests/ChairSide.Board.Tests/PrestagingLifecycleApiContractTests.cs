using System.Text.Json;
using ChairSide.Board.Options;
using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class PrestagingLifecycleApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Assignment_parser_treats_omitted_and_explicit_null_fields_equally()
    {
        var omitted = PrestagingLifecycleRequestParser.ParseAssignment("{}");
        var explicitNulls = PrestagingLifecycleRequestParser.ParseAssignment(
            """
            {
              "doctorId": null,
              "procedureCode": null,
              "sedationChoice": null,
              "confirmedExpectedAllocationUnits": null
            }
            """);

        Assert.Null(omitted.Error);
        Assert.Null(explicitNulls.Error);
        Assert.Equal(omitted.Value, explicitNulls.Value);
        Assert.Equal(new CanonicalAssignmentRequest(null, null, null, null), omitted.Value);
    }

    [Fact]
    public void Missing_assignment_details_body_is_malformed_but_empty_object_is_explicitly_absent()
    {
        var missing = PrestagingLifecycleRequestParser.ParseAssignment(body: null);
        var absent = PrestagingLifecycleRequestParser.ParseAssignment("{}");

        Assert.Null(missing.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.MalformedRequest, missing.Error?.Code);
        Assert.Null(absent.Error);
        Assert.NotNull(absent.Value);
        Assert.Null(absent.Value.DoctorId);
        Assert.Null(absent.Value.ProcedureCode);
    }

    [Theory]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"doctorId\":null,\"DoctorId\":null}")]
    [InlineData("[]")]
    [InlineData("{")]
    public void Assignment_parser_rejects_unknown_duplicate_and_malformed_json(string json)
    {
        var result = PrestagingLifecycleRequestParser.ParseAssignment(json);

        Assert.Null(result.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.MalformedRequest, result.Error?.Code);
        Assert.Empty(result.Error!.UnresolvedFields);
        Assert.Empty(result.Error.IntegrityFaults);
    }

    [Theory]
    [InlineData("{\"sedationChoice\":\"maybe\"}")]
    [InlineData("{\"sedationChoice\":true}")]
    [InlineData("{\"sedationChoice\":\"YES\"}")]
    public void Assignment_parser_rejects_invalid_sedation_values(string json)
    {
        var result = PrestagingLifecycleRequestParser.ParseAssignment(json);

        Assert.Null(result.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.InvalidAssignment, result.Error?.Code);
    }

    [Theory]
    [InlineData("{\"confirmedExpectedAllocationUnits\":0}", "invalid-assignment")]
    [InlineData("{\"confirmedExpectedAllocationUnits\":-1}", "invalid-assignment")]
    [InlineData("{\"confirmedExpectedAllocationUnits\":1.5}", "malformed-request")]
    [InlineData("{\"confirmedExpectedAllocationUnits\":\"3\"}", "malformed-request")]
    public void Assignment_parser_rejects_invalid_allocation_values(string json, string code)
    {
        var result = PrestagingLifecycleRequestParser.ParseAssignment(json);

        Assert.Null(result.Value);
        Assert.Equal(code, result.Error?.Code);
    }

    [Fact]
    public void Add_on_is_an_optional_boolean_that_defaults_false_and_survives_conversion()
    {
        var omitted = PrestagingLifecycleRequestParser.ParseAssignment(
            "{\"doctorId\":\"otte\",\"procedureCode\":\"CON\",\"confirmedExpectedAllocationUnits\":1}");
        var selected = PrestagingLifecycleRequestParser.ParseAssignment(
            "{\"doctorId\":\"otte\",\"procedureCode\":\"CON\",\"confirmedExpectedAllocationUnits\":1,\"isAddOn\":true}");
        var invalid = PrestagingLifecycleRequestParser.ParseAssignment("{\"isAddOn\":\"yes\"}");

        Assert.False(omitted.Value!.IsAddOn);
        Assert.True(selected.Value!.IsAddOn);
        Assert.Equal(PrestagingLifecycleErrorCodes.MalformedRequest, invalid.Error?.Code);

        var converted = CanonicalAssignmentRequestConverter.Convert(
            selected.Value!,
            Procedure("CON", sedationEligible: false, defaultUnits: 1));

        Assert.Null(converted.Error);
        Assert.True(converted.Value!.IsAddOn);
        Assert.Equal(AssignmentCompleteness.Complete, converted.Value?.Completeness);
    }

    [Fact]
    public void Assignment_conversion_normalizes_eligible_unchecked_to_no_and_preserves_confirmed_states()
    {
        var absent = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest(null, null, null, null),
            procedure: null);
        var eligibleProcedure = Procedure("EXT", sedationEligible: true, defaultUnits: 3);
        var partial = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "EXT", null, null),
            eligibleProcedure);
        var confirmed = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "EXT", "yes", 3),
            eligibleProcedure);
        var adjusted = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "EXT", "no", 5),
            eligibleProcedure);

        Assert.Equal(AssignmentCompleteness.Absent, absent.Value?.Completeness);
        Assert.Equal(SedationState.UnavailableNoProcedure, absent.Value?.Sedation.State);
        Assert.Equal(ExpectedAllocationState.Unknown, absent.Value?.ExpectedAllocation.State);

        Assert.Equal(AssignmentCompleteness.Partial, partial.Value?.Completeness);
        Assert.Equal(SedationState.EligibleNo, partial.Value?.Sedation.State);
        Assert.Equal(ExpectedAllocationState.Suggested, partial.Value?.ExpectedAllocation.State);
        Assert.Equal(3, partial.Value?.ExpectedAllocation.SuggestedValue);

        Assert.Equal(AssignmentCompleteness.Complete, confirmed.Value?.Completeness);
        Assert.Equal(SedationState.EligibleYes, confirmed.Value?.Sedation.State);
        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, confirmed.Value?.ExpectedAllocation.State);

        Assert.Equal(AssignmentCompleteness.Complete, adjusted.Value?.Completeness);
        Assert.Equal(SedationState.EligibleNo, adjusted.Value?.Sedation.State);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, adjusted.Value?.ExpectedAllocation.State);
        Assert.Equal(3, adjusted.Value?.ExpectedAllocation.SuggestedValue);
        Assert.Equal(5, adjusted.Value?.ExpectedAllocation.ConfirmedValue);
    }

    [Fact]
    public void Assignment_conversion_derives_unavailable_sedation_for_ineligible_procedure()
    {
        var converted = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "CON", null, 1),
            Procedure("CON", sedationEligible: false, defaultUnits: 1));

        Assert.Null(converted.Error);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, converted.Value?.Sedation.State);
        Assert.Equal(AssignmentCompleteness.Complete, converted.Value?.Completeness);
    }

    [Theory]
    [InlineData(null, "yes", null)]
    [InlineData(null, null, 2)]
    [InlineData("CON", "no", 1)]
    public void Assignment_conversion_rejects_impossible_domain_combinations(
        string? procedureCode,
        string? sedationChoice,
        int? confirmedUnits)
    {
        var procedure = procedureCode is null
            ? null
            : Procedure(procedureCode, sedationEligible: false, defaultUnits: 1);

        var converted = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", procedureCode, sedationChoice, confirmedUnits),
            procedure);

        Assert.Null(converted.Value);
        Assert.Equal("invalid-assignment", converted.Error?.Code);
    }

    [Fact]
    public void Canonical_requests_reject_decorated_procedure_codes()
    {
        var parsed = PrestagingLifecycleRequestParser.ParseAssignment(
            "{\"procedureCode\":\"EXT+SED\",\"sedationChoice\":\"yes\"}");
        var converted = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "EXT+SED", "yes", 3),
            Procedure("EXT", sedationEligible: true, defaultUnits: 3));

        Assert.Null(parsed.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.InvalidAssignment, parsed.Error?.Code);
        Assert.Null(converted.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.InvalidAssignment, converted.Error?.Code);
    }

    [Fact]
    public void Doctor_identifier_is_the_same_string_type_in_domain_request_and_json()
    {
        var doctor = new Doctor("otte", "Dr. Otte", "Otte", "#fff");
        var parsed = PrestagingLifecycleRequestParser.ParseAssignment("{\"doctorId\":\"otte\"}");
        var numeric = PrestagingLifecycleRequestParser.ParseAssignment("{\"doctorId\":42}");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Value, JsonOptions));

        Assert.IsType<string>(doctor.Id);
        Assert.Equal(doctor.Id, parsed.Value?.DoctorId);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("doctorId").ValueKind);
        Assert.Equal(PrestagingLifecycleErrorCodes.MalformedRequest, numeric.Error?.Code);
    }

    [Fact]
    public void Action_parsers_support_empty_actions_and_optional_nested_assignments()
    {
        var empty = PrestagingLifecycleRequestParser.ParseEmptyAction(body: null);
        var seat = PrestagingLifecycleRequestParser.ParseSeatAction(
            """
            {
              "assignment": {
                "doctorId": "otte",
                "procedureCode": "EXT",
                "sedationChoice": "yes",
                "confirmedExpectedAllocationUnits": 4
              }
            }
            """);
        var bareReady = PrestagingLifecycleRequestParser.ParseReadyForDoctorAction("{}");
        var readyWithExplicitNull = PrestagingLifecycleRequestParser.ParseReadyForDoctorAction(
            "{\"assignment\":null}");

        Assert.NotNull(empty.Value);
        Assert.Null(empty.Error);

        Assert.Equal("yes", seat.Value?.Assignment?.SedationChoice);
        Assert.Null(bareReady.Value?.Assignment);
        Assert.Equal(bareReady.Value, readyWithExplicitNull.Value);
    }

    [Theory]
    [InlineData("{\"assignment\":{},\"Assignment\":{}}")]
    [InlineData("{\"assignment\":{},\"unexpected\":true}")]
    [InlineData("{\"assignment\":5}")]
    [InlineData("{\"demoElapsedMinutes\":1}")]
    public void Ready_action_parser_is_strict(string json)
    {
        var result = PrestagingLifecycleRequestParser.ParseReadyForDoctorAction(json);

        Assert.Null(result.Value);
        Assert.Equal(PrestagingLifecycleErrorCodes.MalformedRequest, result.Error?.Code);
    }

    [Fact]
    public void Canonical_seat_serialization_contains_only_optional_assignment()
    {
        var request = new SeatRoomActionRequest(
            new CanonicalAssignmentRequest("otte", "EXT", "yes", 4));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonOptions));

        Assert.Equal(["assignment"], PropertyNames(document.RootElement));
        Assert.False(document.RootElement.TryGetProperty("demoElapsedMinutes", out _));
        Assert.Equal(
            PrestagingLifecycleErrorCodes.MalformedRequest,
            PrestagingLifecycleRequestParser.ParseSeatAction("{\"demoElapsedMinutes\":1}").Error?.Code);
    }

    [Fact]
    public void Unresolved_field_projection_identifies_only_incomplete_requirements()
    {
        var partial = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest(null, "EXT", null, null),
            Procedure("EXT", sedationEligible: true, defaultUnits: 3)).Value!;

        var unresolved = CanonicalAssignmentRequirements.GetUnresolvedFields(partial);

        Assert.Equal(["doctorId", "confirmedExpectedAllocationUnits"], unresolved);
    }

    [Fact]
    public void Canonical_action_response_serializes_additively_with_locked_assignment_and_handoff()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-14T15:00:00Z");
        var assignment = CanonicalAssignmentRequestConverter.Convert(
            new CanonicalAssignmentRequest("otte", "CON", null, 1),
            Procedure("CON", sedationEligible: false, defaultUnits: 1)).Value!;
        var handoff = ReadyHandoffContract.Active("handoff-1", readyAt, assignment);
        var response = PrestagingLifecycleResponseProjector.Create(
            Room(RoomStates.ReadyForDoctor, readyAt),
            CanonicalRoomLifecycleState.ReadyForDoctor,
            assignment,
            handoff);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(["room", "lifecycle", "handoff"], PropertyNames(root));
        Assert.Equal(
            ["state", "assignment", "assignmentLocked", "readyUrgency", "integrityFaults"],
            PropertyNames(root.GetProperty("lifecycle")));
        Assert.Equal("readyForDoctor", root.GetProperty("room").GetProperty("state").GetString());
        Assert.Equal("ReadyForDoctor", root.GetProperty("lifecycle").GetProperty("state").GetString());
        Assert.True(root.GetProperty("lifecycle").GetProperty("assignmentLocked").GetBoolean());
        Assert.Equal("UnavailableProcedureIneligible", root.GetProperty("lifecycle").GetProperty("assignment")
            .GetProperty("sedation").GetProperty("state").GetString());
        Assert.Equal("ConfirmedSuggestedValue", root.GetProperty("lifecycle").GetProperty("assignment")
            .GetProperty("expectedAllocation").GetProperty("state").GetString());
        Assert.Equal("Active", root.GetProperty("handoff").GetProperty("status").GetString());
    }

    [Fact]
    public void Affirmative_sedation_response_uses_only_the_undecorated_base_procedure_code()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-14T15:00:00Z");
        var internalAssignment = RoomAssignmentContract.Create(
            "otte",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));
        var response = PrestagingLifecycleResponseProjector.Create(
            Room(RoomStates.ReadyForDoctor, readyAt) with { ProcedureCode = "EXT+SED" },
            CanonicalRoomLifecycleState.ReadyForDoctor,
            internalAssignment,
            ReadyHandoffContract.Active("handoff-1", readyAt, internalAssignment));

        var json = JsonSerializer.Serialize(response, JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.DoesNotContain("+SED", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("EXT", document.RootElement.GetProperty("room").GetProperty("procedureCode").GetString());
        Assert.Equal("EXT", document.RootElement.GetProperty("lifecycle").GetProperty("assignment")
            .GetProperty("procedureCode").GetString());
        Assert.Equal("EXT", document.RootElement.GetProperty("handoff").GetProperty("assignment")
            .GetProperty("procedureCode").GetString());
    }

    [Fact]
    public void Stable_error_codes_are_the_approved_lowercase_kebab_case_values()
    {
        Assert.Contains(PrestagingLifecycleErrorCodes.MalformedRequest, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.RoomNotFound, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.InvalidAssignment, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.AssignmentIncomplete, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.LifecycleConflict, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.AssignmentLocked, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.IntegrityFault, PrestagingLifecycleErrorCodes.All);
        Assert.Contains(PrestagingLifecycleErrorCodes.StaleWrite, PrestagingLifecycleErrorCodes.All);
        Assert.All(
            PrestagingLifecycleErrorCodes.All,
            code => Assert.Matches("^[a-z]+(?:-[a-z]+)*$", code));
    }

    [Fact]
    public void Canonical_error_response_serializes_the_approved_exact_shape()
    {
        var error = new PrestagingLifecycleErrorResponse(
            "assignment-incomplete",
            "Ready for Doctor requires a complete assignment.",
            ["sedationChoice", "confirmedExpectedAllocationUnits"],
            []);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(error, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(["code", "message", "unresolvedFields", "integrityFaults"], PropertyNames(root));
        Assert.Equal("assignment-incomplete", root.GetProperty("code").GetString());
        Assert.Equal(2, root.GetProperty("unresolvedFields").GetArrayLength());
        Assert.Equal(0, root.GetProperty("integrityFaults").GetArrayLength());
    }

    private static ProcedureCategory Procedure(string code, bool sedationEligible, int defaultUnits) =>
        new(code.ToLowerInvariant(), code, code, "test", sedationEligible, AllocationBehaviors.Variable, defaultUnits);

    private static RoomStatus Room(string state, DateTimeOffset readyAt) =>
        new(
            RoomId: 1,
            Number: 1,
            AssignedDoctor: "otte",
            ProcedureCode: "CON",
            State: state,
            Doctor: null,
            Procedure: null,
            SeatedAt: readyAt.AddMinutes(-5),
            AgingStartedAt: null,
            StaleStartedAt: null,
            ReadyForDoctorAt: readyAt,
            DoctorArrivedAt: null,
            DoctorCompleteAt: null,
            RoomAvailableAt: null,
            Elapsed: TimeSpan.FromMinutes(5),
            ReadyUrgency: ReadyUrgency.None,
            IntegrityFaults: []);

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();
}
