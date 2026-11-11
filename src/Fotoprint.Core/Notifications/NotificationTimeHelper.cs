using Fotoprint.Core.Models;

namespace Fotoprint.Core.Notifications;

/// <summary>
/// Provides timezone conversion, MSK day windowing, and deadline tier classification helpers.
/// </summary>
public static class NotificationTimeHelper
{
    private static readonly HashSet<string> TerminalTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancelled",
        "completed",
        "done",
        "closed"
    };

    /// <summary>
    /// Resolves a configured timezone ID (e.g. "Europe/Moscow") with cross-platform fallback to UTC+3.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId = "Europe/Moscow")
    {
        var candidates = new[]
        {
            timeZoneId,
            "Europe/Moscow",
            "Russian Standard Time"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Fotoprint-MSK",
            TimeSpan.FromHours(3),
            "Moscow Standard Time",
            "Moscow Standard Time");
    }

    /// <summary>
    /// Normalizes a <see cref="DateTime"/> value to UTC (treating <see cref="DateTimeKind.Unspecified"/> as UTC).
    /// </summary>
    public static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// Converts a UTC timestamp into the specified target timezone.
    /// </summary>
    public static DateTime ConvertUtcToZone(DateTime dateTime, TimeZoneInfo tz)
    {
        var utc = EnsureUtc(dateTime);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
    }

    /// <summary>
    /// Computes UTC boundaries matching the current calendar day in the target timezone.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc, DateOnly LocalDate) GetDayBoundsUtc(
        DateTimeOffset utcNow,
        TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, tz);
        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, tz);
        return (startUtc, endUtc, DateOnly.FromDateTime(localStart));
    }

    /// <summary>
    /// Classifies an action item's deadline urgency and builds a reschedule-aware deduplication key.
    /// Tasks overdue beyond <paramref name="maxOverdueDays"/> are skipped to prevent alert floods.
    /// </summary>
    public static (TaskDeadlineTier Tier, string? DedupKey) EvaluateTaskDeadline(
        ActionItem item,
        int employeeId,
        DateTimeOffset utcNow,
        TimeZoneInfo tz,
        int urgentHours = 2,
        int dueSoonHours = 24,
        int maxOverdueDays = 30)
    {
        if (!item.Deadline.HasValue || TerminalTaskStatuses.Contains(item.Status ?? string.Empty))
        {
            return (TaskDeadlineTier.None, null);
        }

        var nowUtc = utcNow.UtcDateTime;
        var deadlineUtc = EnsureUtc(item.Deadline.Value);
        var effectiveUrgentHours = Math.Max(1, urgentHours);
        var effectiveDueSoonHours = Math.Max(effectiveUrgentHours + 1, dueSoonHours);
        var effectiveMaxOverdueDays = Math.Max(1, maxOverdueDays);

        if (deadlineUtc < nowUtc)
        {
            if (deadlineUtc < nowUtc.AddDays(-effectiveMaxOverdueDays))
            {
                return (TaskDeadlineTier.None, null);
            }

            var localDate = GetDayBoundsUtc(utcNow, tz).LocalDate;
            return (TaskDeadlineTier.Overdue, $"task_overdue:{localDate:yyyy-MM-dd}:{item.Id:N}:emp:{employeeId}");
        }

        if (deadlineUtc <= nowUtc.AddHours(effectiveUrgentHours))
        {
            return (TaskDeadlineTier.DueUrgent, $"task_due_{effectiveUrgentHours}h:{deadlineUtc:yyyyMMddHHmm}:{item.Id:N}:emp:{employeeId}");
        }

        if (deadlineUtc <= nowUtc.AddHours(effectiveDueSoonHours))
        {
            return (TaskDeadlineTier.DueSoon, $"task_due_{effectiveDueSoonHours}h:{deadlineUtc:yyyyMMddHHmm}:{item.Id:N}:emp:{employeeId}");
        }

        return (TaskDeadlineTier.None, null);
    }

    /// <summary>
    /// Builds a deduplication key for pre-meeting reminders keyed by meeting start timestamp (UTC minute).
    /// </summary>
    public static string BuildPreMeetingDedupKey(Guid meetingId, DateTime startTimeUtc, int employeeId) =>
        $"pre_meeting:{EnsureUtc(startTimeUtc):yyyyMMddHHmm}:{meetingId:N}:emp:{employeeId}";

    /// <summary>
    /// Builds a daily deduplication key for the employee's morning schedule digest.
    /// </summary>
    public static string BuildMorningDigestDedupKey(DateOnly localDateMsk, int employeeId) =>
        $"morning_digest:{localDateMsk:yyyy-MM-dd}:emp:{employeeId}";
}
