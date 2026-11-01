using System.Globalization;
using CodeLogic.Core.Results;

namespace CL.Common.Time;

/// <summary>
/// Provides date and time utility methods including Unix timestamps,
/// ISO 8601 parsing, business day calculations, and relative time strings.
/// </summary>
public static class DateTimeHelper
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Unix timestamps ───────────────────────────────────────────────────────

    /// <summary>Converts a <see cref="DateTime"/> to a Unix timestamp (seconds since 1970-01-01 UTC).</summary>
    public static long ToUnixTimestamp(DateTime dt) =>
        (long)(dt.ToUniversalTime() - UnixEpoch).TotalSeconds;

    /// <summary>Converts a Unix timestamp (seconds) to a UTC <see cref="DateTime"/>.</summary>
    public static DateTime FromUnixTimestamp(long timestamp) =>
        UnixEpoch.AddSeconds(timestamp);

    /// <summary>Converts a <see cref="DateTime"/> to a Unix timestamp in milliseconds.</summary>
    public static long ToUnixMilliseconds(DateTime dt) =>
        (long)(dt.ToUniversalTime() - UnixEpoch).TotalMilliseconds;

    // ── ISO 8601 ──────────────────────────────────────────────────────────────

    /// <summary>Formats a <see cref="DateTime"/> as an ISO 8601 round-trip string (e.g. <c>2026-04-06T12:00:00.000Z</c>).</summary>
    public static string ToIso8601(DateTime dt) => dt.ToUniversalTime().ToString("o");

    /// <summary>Parses an ISO 8601 string to a UTC <see cref="DateTime"/>.</summary>
    public static Result<DateTime> FromIso8601(string iso)
    {
        if (DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var result))
            return result.ToUniversalTime();
        return Error.Validation(ErrorCode.InvalidFormat, $"'{iso}' is not a valid ISO 8601 date/time");
    }

    // ── Age / calendar ────────────────────────────────────────────────────────

    /// <summary>Calculates age in whole years from a birth date to today.</summary>
    public static int GetAge(DateTime birthDate)
    {
        var today = DateTime.Today;
        int age   = today.Year - birthDate.Year;
        if (birthDate.Date > today.AddYears(-age)) age--;
        return age;
    }

    /// <summary>Returns true if the date falls on a Saturday or Sunday.</summary>
    public static bool IsWeekend(DateTime dt) =>
        dt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Returns true if the date is a Monday–Friday (no public holiday awareness).</summary>
    public static bool IsBusinessDay(DateTime dt) => !IsWeekend(dt);

    /// <summary>
    /// Returns the first Monday–Friday strictly after <paramref name="dt"/> (time of day preserved), so Friday
    /// gives Monday and Monday gives Tuesday. Use <see cref="IsBusinessDay"/> to test the day itself.
    /// </summary>
    public static DateTime NextBusinessDay(DateTime dt)
    {
        var next = dt.AddDays(1);
        while (IsWeekend(next)) next = next.AddDays(1);
        return next;
    }

    // ── Boundary helpers ──────────────────────────────────────────────────────

    /// <summary>Returns midnight (00:00:00) on the same day.</summary>
    public static DateTime StartOfDay(DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Kind);

    /// <summary>Returns 23:59:59.999 on the same day.</summary>
    public static DateTime EndOfDay(DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, 23, 59, 59, 999, dt.Kind);

    /// <summary>Returns the first day of the week containing <paramref name="dt"/>.</summary>
    public static DateTime StartOfWeek(DateTime dt, DayOfWeek startOfWeek = DayOfWeek.Monday)
    {
        int diff = (7 + (dt.DayOfWeek - startOfWeek)) % 7;
        return StartOfDay(dt.AddDays(-diff));
    }

    /// <summary>Returns the first day of the month.</summary>
    public static DateTime StartOfMonth(DateTime dt) =>
        new(dt.Year, dt.Month, 1, 0, 0, 0, dt.Kind);

    /// <summary>Returns the last day of the month at 23:59:59.999.</summary>
    public static DateTime EndOfMonth(DateTime dt) =>
        EndOfDay(new DateTime(dt.Year, dt.Month, DateTime.DaysInMonth(dt.Year, dt.Month), 0, 0, 0, dt.Kind));

    // ── Relative time ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable relative time string.
    /// Examples: "just now", "3 minutes ago", "in 2 hours", "yesterday".
    /// </summary>
    public static string ToRelativeString(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt.ToUniversalTime();
        var abs  = Math.Abs(diff.TotalSeconds);
        bool past = diff.TotalSeconds >= 0;

        string label = abs switch
        {
            < 5      => "just now",
            < 60     => $"{(int)abs} seconds",
            < 3600   => $"{(int)(abs / 60)} minute{((int)(abs / 60) == 1 ? "" : "s")}",
            < 86400  => $"{(int)(abs / 3600)} hour{((int)(abs / 3600) == 1 ? "" : "s")}",
            < 172800 => "a day",
            < 604800 => $"{(int)(abs / 86400)} days",
            < 1209600 => "a week",
            < 2592000 => $"{(int)(abs / 604800)} weeks",
            < 31536000 => $"{(int)(abs / 2592000)} month{((int)(abs / 2592000) == 1 ? "" : "s")}",
            _ => $"{(int)(abs / 31536000)} year{((int)(abs / 31536000) == 1 ? "" : "s")}"
        };

        if (label == "just now") return label;
        return past ? $"{label} ago" : $"in {label}";
    }
}
