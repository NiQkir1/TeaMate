using Fotoprint.Core.Models;
using Fotoprint.Core.Security;

namespace Fotoprint.Core.Data;

public record TimeSlot
{
    public DateTime StartTimeUtc { get; init; }
    public DateTime EndTimeUtc { get; init; }
    public string Label { get; init; } = string.Empty;
}

public interface IFotoprintRepository
{
    // Employee & RBAC
    Task<EmployeeSecurityContext?> GetEmployeeSecurityContextAsync(long telegramId, CancellationToken ct = default);
    Task<EmployeeSecurityContext?> GetEmployeeSecurityContextByMasterIdAsync(long masterId, CancellationToken ct = default);
    Task<long?> ResolveMasterIdByMessengerAsync(string messengerType, string messengerUserId, CancellationToken ct = default);
    Task<Employee?> GetEmployeeByIdAsync(int id, CancellationToken ct = default);
    Task<List<Employee>> GetAllEmployeesAsync(CancellationToken ct = default);

    // Chat Sessions & Messages (MasterId & UUID SessionId)
    Task<ChatSession> GetOrCreateActiveSessionByMasterIdAsync(long masterId, long messengerChatId, string messengerType = "telegram", CancellationToken ct = default);
    Task<ChatSession> SwitchToNewSessionAsync(Guid currentSessionId, long masterId, long messengerChatId, string? newBranch = null, string messengerType = "telegram", CancellationToken ct = default);
    Task<List<ChatMessage>> GetMessagesBySessionIdAsync(Guid sessionId, int limit = 20, CancellationToken ct = default);
    Task SaveMessageToSessionAsync(Guid sessionId, long masterId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default);
    Task<ChatSession> GetOrCreateSessionAsync(long telegramChatId, long? telegramUserId = null, CancellationToken ct = default);
    Task SaveMessageAsync(Guid sessionId, string contextId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default);
    Task<List<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int limit = 20, CancellationToken ct = default);
    Task FreezeContextAsync(Guid sessionId, string contextId, CancellationToken ct = default);

    // Corporate Documents & Chunks (RAG)
    Task<Guid> SaveDocumentAsync(string title, string sourceType, string? filePath, string? department, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default);
    Task SaveDocumentChunksAsync(Guid documentId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, CancellationToken ct = default);
    Task<List<CorporateDocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default);
    Task<List<CorporateDocumentChunk>> SearchTextChunksAsync(string queryText, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default);

    // Meetings, Notes, Protocols, Action Items & Calendar
    Task<Guid> CreateMeetingAsync(
        string title,
        string? description,
        DateTime startTime,
        DateTime? endTime = null,
        string status = "completed",
        int? organizerId = null,
        List<int>? participantIds = null,
        object? metadata = null,
        CancellationToken ct = default);
    Task<List<Meeting>> GetUpcomingMeetingsAsync(int timeWindowMinutes = 30, CancellationToken ct = default);
    Task<List<Meeting>> GetMeetingsForRangeAsync(DateTime fromUtc, DateTime toUtc, int? employeeId = null, CancellationToken ct = default);
    Task<List<TimeSlot>> FindFreeSlotsAsync(
        int organizerId,
        List<int> participantIds,
        DateOnly targetDate,
        int durationMinutes,
        TimeOnly? workingStart = null,
        TimeOnly? workingEnd = null,
        int bufferMinutes = 10,
        CancellationToken ct = default);
    Task<Meeting?> GetMeetingByIdAsync(Guid meetingId, CancellationToken ct = default);
    Task<List<MeetingNote>> GetMeetingNotesAsync(Guid meetingId, CancellationToken ct = default);
    Task SaveMeetingNoteAsync(Guid meetingId, int? authorId, string noteText, string category = "general", CancellationToken ct = default);
    Task<Guid> SaveMeetingProtocolAsync(Guid meetingId, string title, string summary, List<string> decisions, string? rawTranscript = null, string? docxPath = null, string? pdfPath = null, CancellationToken ct = default);
    Task<MeetingProtocol?> GetMeetingProtocolAsync(Guid meetingId, CancellationToken ct = default);
    Task<Guid> CreateActionItemAsync(Guid meetingId, Guid? protocolId, string title, string? description, int? responsibleId, DateTime? deadline, CancellationToken ct = default);
    Task<List<ActionItem>> GetActionItemsByMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task<List<ActionItem>> GetActionItemsByResponsibleAsync(int employeeId, string? statusFilter = null, CancellationToken ct = default);
    Task UpdateActionItemStatusAsync(Guid actionItemId, string status, int? bitrix24TaskId = null, CancellationToken ct = default);
}
