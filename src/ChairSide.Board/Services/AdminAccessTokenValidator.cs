using System.Security.Cryptography;
using System.Text;

using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public sealed class AdminAccessTokenValidator(IOptionsMonitor<AdminAccessOptions> options)
{
    public const string HeaderName = "X-ChairSide-Admin-Token";

    public AdminAccessTokenValidationResult Validate(string? token)
    {
        var currentOptions = options.CurrentValue;
        if (!currentOptions.Enabled)
        {
            return AdminAccessTokenValidationResult.Disabled;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return AdminAccessTokenValidationResult.Missing;
        }

        return FixedTimeEquals(currentOptions.SharedToken, token)
            ? AdminAccessTokenValidationResult.Valid
            : AdminAccessTokenValidationResult.Invalid;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

public enum AdminAccessTokenValidationResult
{
    Disabled,
    Valid,
    Missing,
    Invalid
}
