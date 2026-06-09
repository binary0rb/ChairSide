using Microsoft.AspNetCore.Http;

namespace ChairSide.Board.Services;

public static class AdminAccessGuard
{
    public static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/api/reports", StringComparison.OrdinalIgnoreCase);

    public static IResult? ValidateRequest(
        HttpRequest request,
        AdminAccessTokenValidator adminAccessTokenValidator)
    {
        var token = request.Headers[AdminAccessTokenValidator.HeaderName].FirstOrDefault();

        return adminAccessTokenValidator.Validate(token) switch
        {
            AdminAccessTokenValidationResult.Disabled or AdminAccessTokenValidationResult.Valid => null,
            AdminAccessTokenValidationResult.Missing => Results.Unauthorized(),
            _ => Results.StatusCode(StatusCodes.Status403Forbidden)
        };
    }
}
