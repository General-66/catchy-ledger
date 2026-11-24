namespace Ledger.Api;

/// <summary>
/// The operator keeps its books on South African time. A bet placed at 01:00 on 1 December counts for 1 December,
/// even though it's still 30 November in UTC, the time zone the servers and the database use.
/// </summary>
public sealed class BusinessCalendar(TimeProvider time)
{
    /// <summary>
    /// South Africa Standard Time: UTC+2 all year, with no daylight saving. "Africa/Johannesburg" is its IANA name, which
    /// Linux and modern Windows understand; a Windows machine without the ICU library only knows the Windows name.
    /// </summary>
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById("Africa/Johannesburg", out var zone)
        ? zone
        : TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");

    /// <summary>Today's date in South Africa: the effective date of a transaction that doesn't name one.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZone).DateTime);
}