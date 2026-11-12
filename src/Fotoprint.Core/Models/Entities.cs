namespace Fotoprint.Core.Models;

public class Employee
{
    public int Id { get; set; }
    public long MasterId { get; set; } // Уникальный числовой идентификатор (только цифры)
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? Email { get; set; }
    public long? TelegramId { get; set; }
    public string? TelegramUsername { get; set; }
    public string? WhatsappId { get; set; }
    public string Department { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public List<string> AccessGroups { get; set; } = new();
    public bool NotificationsEnabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string FullName => string.IsNullOrWhiteSpace(MiddleName)
        ? $"{LastName} {FirstName}"
        : $"{LastName} {FirstName} {MiddleName}";
}

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class Permission
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = "general";
}

public class ChatSession
{
    public Guid Id { get; set; } // Уникальный UUID SessionId текущего диалога
    public long MasterId { get; set; } // Привязка к MasterId пользователя
    public int? EmployeeId { get; set; }
    public string MessengerType { get; set; } = "telegram";
    public long MessengerChatId { get; set; }
    public long TelegramChatId { get; set; } // Legacy fallback
    public bool IsActive { get; set; } = true;
    public DateTime? ClosedAt { get; set; }
    public string Title { get; set; } = "Новая сессия";
    public string CurrentBranch { get; set; } = "general";
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; } // UUID сессии (при смене диалога меняется)
    public long MasterId { get; set; } // Цифровой MasterId автора
    public string ContextId { get; set; } = string.Empty;
    public string Role { get; set; } = "user"; // user, assistant, system
    public string Content { get; set; } = string.Empty;
    public bool IsFrozen { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class ContextBatch
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string? Summary { get; set; }
    public int MessageCount { get; set; }
    public bool IsContextSwitched { get; set; }
    public string? Branch { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class CorporateDocument
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty; // pdf, docx, xlsx, txt
    public string? FilePath { get; set; }
    public string? Department { get; set; }
    public List<string> AllowedAccessGroups { get; set; } = new();
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CorporateDocumentChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
    public List<string> AllowedAccessGroups { get; set; } = new();
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class Meeting
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "scheduled"; // scheduled, in_progress, completed, cancelled
    public int? OrganizerId { get; set; }
    public List<int> ParticipantIds { get; set; } = new();
    public string? CalendarEventId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class MeetingNote
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public int? AuthorId { get; set; }
    public string NoteText { get; set; } = string.Empty;
    public string Category { get; set; } = "general"; // discussion, decision, question, action_point
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class MeetingProtocol
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Decisions { get; set; } = new();
    public string? RawTranscript { get; set; }
    public string? FileDocxPath { get; set; }
    public string? FilePdfPath { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ActionItem
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid? ProtocolId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ResponsibleId { get; set; }
    public DateTime? Deadline { get; set; }
    public string Status { get; set; } = "pending"; // pending, confirmed, exported_to_bitrix, cancelled
    public int? Bitrix24TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record EmployeeNotificationPreferences
{
    public int EmployeeId { get; init; }
    public long MasterId { get; init; }
    public bool DigestEnabled { get; init; } = true;
    public string DigestTimeMsk { get; init; } = "09:00";
    public bool DigestWeekdaysOnly { get; init; } = true;
    public bool PreMeetingEnabled { get; init; } = true;
    public int PreMeetingWindowMinutes { get; init; } = 30;
    public bool TaskDeadlinesEnabled { get; init; } = true;
    public int TaskDueSoonHours { get; init; } = 24;
    public int TaskUrgentHours { get; init; } = 2;
    public string? QuietHoursStartMsk { get; init; }
    public string? QuietHoursEndMsk { get; init; }
    public string PreferredChannel { get; init; } = "telegram";
    public string? ChannelAddress { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    public static EmployeeNotificationPreferences CreateDefault(int employeeId, long masterId, int defaultDigestHourMsk = 9, int defaultPreMeetingMinutes = 30, int defaultDueSoonHours = 24, int defaultUrgentHours = 2) => new()
    {
        EmployeeId = employeeId,
        MasterId = masterId,
        DigestEnabled = true,
        DigestTimeMsk = $"{Math.Clamp(defaultDigestHourMsk, 0, 23):D2}:00",
        DigestWeekdaysOnly = true,
        PreMeetingEnabled = true,
        PreMeetingWindowMinutes = Math.Clamp(defaultPreMeetingMinutes, 5, 240),
        TaskDeadlinesEnabled = true,
        TaskDueSoonHours = Math.Clamp(defaultDueSoonHours, 1, 168),
        TaskUrgentHours = Math.Clamp(defaultUrgentHours, 1, 48),
        PreferredChannel = "telegram"
    };
}

public sealed record UpdateNotificationPreferencesPatch(
    [property: System.Text.Json.Serialization.JsonPropertyName("notifications_enabled")] bool? NotificationsEnabled = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("digest_enabled")] bool? DigestEnabled = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("digest_time_msk")] string? DigestTimeMsk = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("digest_weekdays_only")] bool? DigestWeekdaysOnly = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("pre_meeting_enabled")] bool? PreMeetingEnabled = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("pre_meeting_window_minutes")] int? PreMeetingWindowMinutes = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("task_deadlines_enabled")] bool? TaskDeadlinesEnabled = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("task_due_soon_hours")] int? TaskDueSoonHours = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("task_urgent_hours")] int? TaskUrgentHours = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("quiet_hours_start_msk")] string? QuietHoursStartMsk = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("quiet_hours_end_msk")] string? QuietHoursEndMsk = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("clear_quiet_hours")] bool ClearQuietHours = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("preferred_channel")] string? PreferredChannel = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("channel_address")] string? ChannelAddress = null);

public sealed record UserIdentityMapping(
    int Id,
    int EmployeeId,
    long MasterId,
    string Provider,
    string ExternalUserId,
    DateTime CreatedAt);


