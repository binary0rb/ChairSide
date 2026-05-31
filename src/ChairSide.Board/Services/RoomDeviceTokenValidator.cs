using System.Security.Cryptography;
using System.Text;

using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public sealed class RoomDeviceTokenValidator(IOptionsMonitor<RoomDeviceBindingOptions> options)
{
    public const string HeaderName = "X-ChairSide-Room-Token";

    public RoomDeviceTokenValidationResult Validate(int roomNumber, string? token)
    {
        var currentOptions = options.CurrentValue;
        if (!currentOptions.Enabled)
        {
            return RoomDeviceTokenValidationResult.Disabled;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return RoomDeviceTokenValidationResult.Missing;
        }

        if (!currentOptions.RoomTokens.TryGetValue(roomNumber.ToString(), out var configuredToken)
            || string.IsNullOrWhiteSpace(configuredToken))
        {
            return RoomDeviceTokenValidationResult.Invalid;
        }

        return FixedTimeEquals(configuredToken, token)
            ? RoomDeviceTokenValidationResult.Valid
            : RoomDeviceTokenValidationResult.Invalid;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

public enum RoomDeviceTokenValidationResult
{
    Disabled,
    Valid,
    Missing,
    Invalid
}
