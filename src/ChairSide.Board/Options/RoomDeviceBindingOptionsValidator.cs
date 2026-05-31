using Microsoft.Extensions.Options;

namespace ChairSide.Board.Options;

public sealed class RoomDeviceBindingOptionsValidator(IOptions<BoardOptions> boardOptions)
    : IValidateOptions<RoomDeviceBindingOptions>
{
    public ValidateOptionsResult Validate(string? name, RoomDeviceBindingOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        for (var roomNumber = 1; roomNumber <= boardOptions.Value.RoomCount; roomNumber++)
        {
            var roomKey = roomNumber.ToString();
            if (!options.RoomTokens.ContainsKey(roomKey))
            {
                failures.Add($"RoomDeviceBindingOptions:RoomTokens:{roomKey} is required when room-device binding is enabled.");
            }
        }

        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (roomKey, token) in options.RoomTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                failures.Add($"RoomDeviceBindingOptions:RoomTokens:{roomKey} must not be blank when room-device binding is enabled.");
                continue;
            }

            if (!seenTokens.Add(token))
            {
                failures.Add("RoomDeviceBindingOptions:RoomTokens must not contain duplicate token values.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
