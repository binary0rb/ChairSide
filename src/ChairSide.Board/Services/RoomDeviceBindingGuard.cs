using Microsoft.AspNetCore.Http;

namespace ChairSide.Board.Services;

public static class RoomDeviceBindingGuard
{
    public static IResult? ValidateMutationRequest(
        int roomNumber,
        HttpRequest request,
        RoomDeviceTokenValidator roomDeviceTokenValidator)
    {
        var token = request.Headers[RoomDeviceTokenValidator.HeaderName].FirstOrDefault()
            ?? request.Query["roomToken"].FirstOrDefault()
            ?? request.Query["token"].FirstOrDefault();

        return roomDeviceTokenValidator.Validate(roomNumber, token) switch
        {
            RoomDeviceTokenValidationResult.Disabled or RoomDeviceTokenValidationResult.Valid => null,
            RoomDeviceTokenValidationResult.Missing => Results.Unauthorized(),
            _ => Results.StatusCode(StatusCodes.Status403Forbidden)
        };
    }
}
