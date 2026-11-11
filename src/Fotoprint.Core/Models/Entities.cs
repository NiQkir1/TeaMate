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

