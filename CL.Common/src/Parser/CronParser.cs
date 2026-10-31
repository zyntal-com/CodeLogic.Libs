using CodeLogic.Core.Results;

namespace CL.Common.Parser;

/// <summary>
/// A parsed cron expression with individual field components.
/// Cron format: <c>MINUTES HOURS DAY_OF_MONTH MONTH DAY_OF_WEEK</c>
/// </summary>
/// <param name="Raw">The original expression string.</param>
/// <param name="Minutes">Minutes field (0-59).</param>
/// <param name="Hours">Hours field (0-23).</param>
/// <param name="DayOfMonth">Day of month field (1-31).</param>
/// <param name="Month">Month field (1-12).</param>
/// <param name="DayOfWeek">Day of week field (0-6, Sunday = 0).</param>
public record CronExpression(string Raw, string Minutes, string Hours, string DayOfMonth, string Month, string DayOfWeek);

/// <summary>
/// Parses and evaluates cron expressions in standard 5-field format.
/// Supports: <c>*</c>, <c>*/n</c> (step), <c>a-b</c> (range), <c>a,b,c</c> (list), and exact values.
/// </summary>
public static class CronParser
{
    /// <summary>
    /// Parses a 5-field cron expression string into a <see cref="CronExpression"/>.
    /// </summary>
    /// <param name="expression">A 5-field cron expression, e.g. <c>"0 */6 * * *"</c>.</param>
    public static Result<CronExpression> Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Error.Validation(ErrorCode.InvalidArgument, "Cron expression cannot be empty");

        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return Error.Validation(ErrorCode.InvalidFormat,
                $"Cron expression must have exactly 5 fields, got {parts.Length}. Format: MINUTES HOURS DAY_OF_MONTH MONTH DAY_OF_WEEK");

        return new CronExpression(expression, parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    /// <summary>Returns true if the expression is a valid 5-field cron string.</summary>
    public static bool IsValid(string expression) => Parse(expression).IsSuccess;

    /// <summary>
    /// Returns the next UTC occurrence of the cron schedule after the given time.
    /// Searches up to 1 year ahead. Accuracy is to the minute.
    /// </summary>
    /// <param name="expression">A valid 5-field cron expression.</param>
    /// <param name="from">Start searching from this point. Defaults to <see cref="DateTime.UtcNow"/>.</param>
    public static Result<DateTime> GetNextOccurrence(string expression, DateTime? from = null)
    {
        var parseResult = Parse(expression);
        if (!parseResult.IsSuccess) return parseResult.Error!;

        var expr  = parseResult.Value!;
        var start = (from ?? DateTime.UtcNow).AddMinutes(1);
        // Truncate to the minute
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc);

        for (int i = 0; i < 525_600; i++) // max 1 year = 525,600 minutes
        {
            if (Matches(expr.Minutes,     start.Minute,          0, 59) &&
                Matches(expr.Hours,       start.Hour,            0, 23) &&
                Matches(expr.DayOfMonth,  start.Day,             1, 31) &&
                Matches(expr.Month,       start.Month,           1, 12) &&
                Matches(expr.DayOfWeek,   (int)start.DayOfWeek,  0,  6))
            {
                return start;
            }
            start = start.AddMinutes(1);
        }

        return Error.Internal(ErrorCode.Internal, "No next occurrence found within one year for the given expression");
    }

    /// <summary>
    /// Returns the next occurrence strictly after <paramref name="from"/>, evaluating the fields in
    /// <paramref name="timeZone"/>'s wall-clock time. Wall-clock times skipped by a daylight-saving change never
    /// match; times repeated by one match once per real minute they occur. Searches up to 1 year ahead, to the minute.
    /// </summary>
    /// <param name="expression">A valid 5-field cron expression.</param>
    /// <param name="from">Start searching after this instant.</param>
    /// <param name="timeZone">The zone the fields are written in; null means UTC.</param>
    /// <returns>The next occurrence (as a UTC instant), or a failure.</returns>
    public static Result<DateTimeOffset> GetNextOccurrence(string expression, DateTimeOffset from, TimeZoneInfo? timeZone)
    {
        var parseResult = Parse(expression);
        if (!parseResult.IsSuccess) return parseResult.Error!;

        var expr = parseResult.Value!;
        var utc = from.UtcDateTime;
        var start = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var zone = timeZone ?? TimeZoneInfo.Utc;

        for (int i = 0; i < 527_040; i++) // a leap year of minutes
        {
            var local = zone == TimeZoneInfo.Utc ? start : TimeZoneInfo.ConvertTimeFromUtc(start, zone);
            if (Matches(expr.Minutes,     local.Minute,          0, 59) &&
                Matches(expr.Hours,       local.Hour,            0, 23) &&
                Matches(expr.DayOfMonth,  local.Day,             1, 31) &&
                Matches(expr.Month,       local.Month,           1, 12) &&
                Matches(expr.DayOfWeek,   (int)local.DayOfWeek,  0,  6))
            {
                return new DateTimeOffset(start, TimeSpan.Zero);
            }
            start = start.AddMinutes(1);
        }

        return Error.Internal(ErrorCode.Internal, "No next occurrence found within one year for the given expression");
    }

    // ── Field matching ────────────────────────────────────────────────────────

    private static bool Matches(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        // Step: */n
        if (field.StartsWith("*/") && int.TryParse(field[2..], out var step) && step > 0)
            return (value - min) % step == 0;

        // List: a,b,c
        if (field.Contains(','))
            return field.Split(',').Any(p => MatchesSingle(p.Trim(), value, min, max));

        return MatchesSingle(field, value, min, max);
    }

    private static bool MatchesSingle(string field, int value, int min, int max)
    {
        // Range: a-b
        if (field.Contains('-'))
        {
            var parts = field.Split('-', 2);
            if (int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi))
                return value >= lo && value <= hi;
        }

        // Exact value
        return int.TryParse(field, out var exact) && exact == value;
    }
}
