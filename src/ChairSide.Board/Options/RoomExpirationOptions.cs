namespace ChairSide.Board.Options;

public sealed class RoomExpirationOptions
{
    public const string SectionName = "RoomExpirationOptions";

    /// <summary>
    /// Enables both the MaxActiveDuration check and the after-hours sweep.
    /// Set to false to disable all automatic expiration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Active room cycles that have been running longer than this duration
    /// are automatically archived as aborted history before arrival or review-required exception
    /// cycles after arrival.
    /// </summary>
    public int MaxActiveDurationHours { get; set; } = 8;

    /// <summary>
    /// Enables the daily after-hours sweep that expires any rooms still active
    /// at the configured sweep time.
    /// </summary>
    public bool AfterHoursSweepEnabled { get; set; } = true;

    /// <summary>
    /// Time-of-day (HH:mm, 24-hour) at which the after-hours sweep fires.
    /// Evaluated in the configured TimeZone.
    /// </summary>
    public string AfterHoursSweepTime { get; set; } = "19:00";

    /// <summary>
    /// IANA or Windows timezone identifier used for after-hours sweep scheduling.
    /// Examples: "America/Chicago" (IANA), "Central Standard Time" (Windows).
    /// Invalid non-UTC identifiers suppress the after-hours sweep instead of
    /// falling back to UTC.
    /// </summary>
    public string TimeZone { get; set; } = "America/Chicago";
}
