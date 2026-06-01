namespace ChairSide.Board.Services;

public static class RoomMutationRequestValidator
{
    public const int MaxDoctorIdLength = 64;
    public const int MaxProcedureCodeLength = 32;

    public static string? ValidateDoctorAndProcedure(string? doctorId, string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(doctorId))
        {
            return "Doctor id is required.";
        }

        if (doctorId.Length > MaxDoctorIdLength)
        {
            return $"Doctor id must be {MaxDoctorIdLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return "Procedure code is required.";
        }

        if (procedureCode.Length > MaxProcedureCodeLength)
        {
            return $"Procedure code must be {MaxProcedureCodeLength} characters or fewer.";
        }

        return null;
    }
}
