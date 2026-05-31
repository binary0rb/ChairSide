namespace ChairSide.Board.Options;

public sealed class RoomDeviceBindingOptions
{
    public const string SectionName = "RoomDeviceBindingOptions";

    public bool Enabled { get; set; }

    public Dictionary<string, string> RoomTokens { get; set; } = [];
}
