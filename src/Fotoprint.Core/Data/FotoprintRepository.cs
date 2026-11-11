using System.Data;
using System.Text.Json;
using Dapper;
using Fotoprint.Core.Models;
using Fotoprint.Core.Security;
using Npgsql;
using Pgvector;
using Pgvector.Dapper;

namespace Fotoprint.Core.Data;

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
    Task<List<CorporateDocumentChunk>> GetChunksWithoutEmbeddingAsync(int limit = 200, CancellationToken ct = default);
    Task UpdateChunkEmbeddingAsync(Guid chunkId, float[] embedding, CancellationToken ct = default);
    Task<List<CorporateDocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default);
    Task<List<CorporateDocumentChunk>> SearchTextChunksAsync(string queryText, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default);

    // Meeting Chunks (Isolated Meeting RAG)
    Task SaveMeetingChunksAsync(Guid meetingId, Guid? protocolId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
    Task<List<CorporateDocumentChunk>> GetMeetingChunksWithoutEmbeddingAsync(int limit = 200, CancellationToken ct = default) => Task.FromResult(new List<CorporateDocumentChunk>());
    Task UpdateMeetingChunkEmbeddingAsync(Guid chunkId, float[] embedding, CancellationToken ct = default) => Task.CompletedTask;
    Task<List<CorporateDocumentChunk>> SearchMeetingSimilarChunksAsync(float[] queryEmbedding, List<string> meetingAccessGroups, int topK = 5, CancellationToken ct = default);

    // Meetings, Notes, Protocols, Action Items
    Task<Guid> CreateMeetingAsync(
        string title,
        string? description,
        DateTime startTime,
        DateTime? endTime = null,
        string status = "completed",
        int? organizerId = null,
        List<int>? participantIds = null,
        CancellationToken ct = default);
    Task<List<Meeting>> GetUpcomingMeetingsAsync(int timeWindowMinutes = 30, CancellationToken ct = default);
    Task<Meeting?> GetMeetingByIdAsync(Guid meetingId, CancellationToken ct = default);
    Task<List<MeetingNote>> GetMeetingNotesAsync(Guid meetingId, CancellationToken ct = default);
    Task SaveMeetingNoteAsync(Guid meetingId, int? authorId, string noteText, string category = "general", CancellationToken ct = default);
    Task<Guid> SaveMeetingProtocolAsync(Guid meetingId, string title, string summary, List<string> decisions, string? rawTranscript = null, string? docxPath = null, string? pdfPath = null, CancellationToken ct = default);
    Task<MeetingProtocol?> GetMeetingProtocolAsync(Guid meetingId, CancellationToken ct = default);
    Task<Guid> CreateActionItemAsync(Guid meetingId, Guid? protocolId, string title, string? description, int? responsibleId, DateTime? deadline, CancellationToken ct = default);
    Task<List<ActionItem>> GetActionItemsByMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task<List<ActionItem>> GetActionItemsByResponsibleAsync(int employeeId, string? statusFilter = null, CancellationToken ct = default);
    Task UpdateActionItemStatusAsync(Guid actionItemId, string status, int? bitrix24TaskId = null, CancellationToken ct = default);

    // Проактивные уведомления (Telegram Notifications)
    Task EnsureNotificationSchemaAsync(CancellationToken ct = default);
    Task<Employee?> GetEmployeeByTelegramIdAsync(long telegramId, CancellationToken ct = default);
    Task<bool> SetEmployeeNotificationsEnabledAsync(long masterId, bool enabled, CancellationToken ct = default);
    Task<List<Meeting>> GetMeetingsInRangeUtcAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    Task<List<ActionItem>> GetOpenActionItemsAsync(int? employeeId = null, CancellationToken ct = default);
    Task<Guid?> TryClaimNotificationDeliveryAsync(
        string dedupKey,
        string notificationType,
        int? recipientEmployeeId,
        long recipientTelegramId,
        Guid? entityId = null,
        CancellationToken ct = default);
    Task CompleteNotificationDeliveryAsync(
        Guid deliveryId,
        string status,
        string? errorMessage = null,
        CancellationToken ct = default);
}

public class FotoprintRepository : IFotoprintRepository
{
    private readonly string _connectionString;
    private static bool _meetingChunksEnsured;
    private static readonly SemaphoreSlim _meetingSchemaLock = new(1, 1);

    static FotoprintRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new VectorTypeHandler());
        SqlMapper.AddTypeHandler(new JsonbListTypeHandler<int>());
        SqlMapper.AddTypeHandler(new JsonbListTypeHandler<string>());
    }

    public FotoprintRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();
        return await dataSource.OpenConnectionAsync(ct);
    }

    public async Task<EmployeeSecurityContext?> GetEmployeeSecurityContextAsync(long telegramId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT 
                e.id AS EmployeeId,
                e.master_id AS MasterId,
                TRIM(CONCAT(e.last_name, ' ', e.first_name, ' ', COALESCE(e.middle_name, ''))) AS FullName,
                e.telegram_id AS TelegramId,
                e.telegram_username AS TelegramUsername,
                e.department AS Department,
                e.position AS Position,
                COALESCE(e.access_groups::text, '[]') AS AccessGroupsJson
            FROM employees e
            WHERE e.telegram_id = @TelegramId AND e.is_active = TRUE;

            SELECT r.name 
            FROM employee_roles er
            JOIN roles r ON er.role_id = r.id
            JOIN employees e ON er.employee_id = e.id
            WHERE e.telegram_id = @TelegramId;

            SELECT DISTINCT p.code
            FROM employee_roles er
            JOIN role_permissions rp ON er.role_id = rp.role_id
            JOIN permissions p ON rp.permission_id = p.id
            JOIN employees e ON er.employee_id = e.id
            WHERE e.telegram_id = @TelegramId;
        """;

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { TelegramId = telegramId }, cancellationToken: ct));
        
        var emp = await multi.ReadFirstOrDefaultAsync<dynamic>();
        if (emp == null) return null;

        var roles = (await multi.ReadAsync<string>()).ToList();
        var permissions = (await multi.ReadAsync<string>()).ToList();

        var accessGroups = new List<string>();
        if (emp.accessgroupsjson != null)
        {
            try
            {
                accessGroups = JsonSerializer.Deserialize<List<string>>((string)emp.accessgroupsjson) ?? new();
            }
            catch { }
        }

        return new EmployeeSecurityContext
        {
            EmployeeId = (int)emp.employeeid,
            MasterId = emp.masterid != null ? (long)emp.masterid : 0L,
            FullName = (string)emp.fullname,
            TelegramId = (long)emp.telegramid,
            TelegramUsername = (string?)emp.telegramusername,
            Department = (string)emp.department,
            Position = (string)emp.position,
            Roles = roles,
            Permissions = permissions,
            AccessGroups = accessGroups
        };
    }

    public async Task<EmployeeSecurityContext?> GetEmployeeSecurityContextByMasterIdAsync(long masterId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT 
                e.id AS EmployeeId,
                e.master_id AS MasterId,
                TRIM(CONCAT(e.last_name, ' ', e.first_name, ' ', COALESCE(e.middle_name, ''))) AS FullName,
                COALESCE(e.telegram_id, 0) AS TelegramId,
                e.telegram_username AS TelegramUsername,
                e.department AS Department,
                e.position AS Position,
                COALESCE(e.access_groups::text, '[]') AS AccessGroupsJson
            FROM employees e
            WHERE e.master_id = @MasterId AND e.is_active = TRUE;

            SELECT r.name 
            FROM employee_roles er
            JOIN roles r ON er.role_id = r.id
            JOIN employees e ON er.employee_id = e.id
            WHERE e.master_id = @MasterId;

            SELECT DISTINCT p.code
            FROM employee_roles er
            JOIN role_permissions rp ON er.role_id = rp.role_id
            JOIN permissions p ON rp.permission_id = p.id
            JOIN employees e ON er.employee_id = e.id
            WHERE e.master_id = @MasterId;
        """;

        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { MasterId = masterId }, cancellationToken: ct));
        
        var emp = await multi.ReadFirstOrDefaultAsync<dynamic>();
        if (emp == null) return null;

        var roles = (await multi.ReadAsync<string>()).ToList();
        var permissions = (await multi.ReadAsync<string>()).ToList();

        var accessGroups = new List<string>();
        if (emp.accessgroupsjson != null)
        {
            try
            {
                accessGroups = JsonSerializer.Deserialize<List<string>>((string)emp.accessgroupsjson) ?? new();
            }
            catch { }
        }

        return new EmployeeSecurityContext
        {
            EmployeeId = (int)emp.employeeid,
            MasterId = emp.masterid != null ? (long)emp.masterid : masterId,
            FullName = (string)emp.fullname,
            TelegramId = (long)emp.telegramid,
            TelegramUsername = (string?)emp.telegramusername,
            Department = (string)emp.department,
            Position = (string)emp.position,
            Roles = roles,
            Permissions = permissions,
            AccessGroups = accessGroups
        };
    }

    public async Task<long?> ResolveMasterIdByMessengerAsync(string messengerType, string messengerUserId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        if (string.Equals(messengerType, "telegram", StringComparison.OrdinalIgnoreCase) && long.TryParse(messengerUserId, out var tgId))
        {
            const string sql = "SELECT master_id FROM employees WHERE telegram_id = @TgId AND is_active = TRUE LIMIT 1;";
            return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { TgId = tgId }, cancellationToken: ct));
        }
        else if (string.Equals(messengerType, "whatsapp", StringComparison.OrdinalIgnoreCase))
        {
            const string sql = "SELECT master_id FROM employees WHERE whatsapp_id = @WpId AND is_active = TRUE LIMIT 1;";
            return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { WpId = messengerUserId }, cancellationToken: ct));
        }
        else
        {
            if (long.TryParse(messengerUserId, out var directMasterId))
            {
                const string sql = "SELECT master_id FROM employees WHERE master_id = @MasterId AND is_active = TRUE LIMIT 1;";
                return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { MasterId = directMasterId }, cancellationToken: ct));
            }
            return null;
        }
    }

    public async Task<Employee?> GetEmployeeByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT
                id AS Id,
                master_id AS MasterId,
                first_name AS FirstName,
                last_name AS LastName,
                middle_name AS MiddleName,
                email AS Email,
                telegram_id AS TelegramId,
                telegram_username AS TelegramUsername,
                whatsapp_id AS WhatsappId,
                department AS Department,
                position AS Position,
                notifications_enabled AS NotificationsEnabled,
                is_active AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM employees
            WHERE id = @Id;
        """;
        return await conn.QueryFirstOrDefaultAsync<Employee>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<List<Employee>> GetAllEmployeesAsync(CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT
                id AS Id,
                master_id AS MasterId,
                first_name AS FirstName,
                last_name AS LastName,
                middle_name AS MiddleName,
                email AS Email,
                telegram_id AS TelegramId,
                telegram_username AS TelegramUsername,
                whatsapp_id AS WhatsappId,
                department AS Department,
                position AS Position,
                notifications_enabled AS NotificationsEnabled,
                is_active AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM employees
            WHERE is_active = TRUE
            ORDER BY id;
        """;
        var results = await conn.QueryAsync<Employee>(new CommandDefinition(sql, cancellationToken: ct));
        return results.ToList();
    }

    public async Task<ChatSession> GetOrCreateActiveSessionByMasterIdAsync(
        long masterId, 
        long messengerChatId, 
        string messengerType = "telegram", 
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        
        // 1. Ищем существующую активную сессию пользователя по MasterId
        const string selectSql = """
            SELECT * FROM chat_sessions 
            WHERE master_id = @MasterId AND is_active = TRUE 
            ORDER BY updated_at DESC LIMIT 1;
        """;
        var session = await conn.QueryFirstOrDefaultAsync<ChatSession>(
            new CommandDefinition(selectSql, new { MasterId = masterId }, cancellationToken: ct));

        if (session != null) return session;

        // 2. Ищем employee_id для связи
        var employeeId = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition("SELECT id FROM employees WHERE master_id = @MasterId LIMIT 1;", new { MasterId = masterId }, cancellationToken: ct));

        // 3. Создаем новую активную сессию с новым UUID SessionId
        const string insertSql = """
            INSERT INTO chat_sessions (id, master_id, employee_id, messenger_type, messenger_chat_id, is_active, title, current_branch)
            VALUES (gen_random_uuid(), @MasterId, @EmployeeId, @MessengerType, @MessengerChatId, TRUE, 'Диалог ' || @MasterId, 'general')
            RETURNING *, messenger_chat_id AS TelegramChatId;
        """;

        return await conn.QuerySingleAsync<ChatSession>(
            new CommandDefinition(insertSql, new 
            { 
                MasterId = masterId, 
                EmployeeId = employeeId, 
                MessengerType = messengerType, 
                MessengerChatId = messengerChatId 
            }, cancellationToken: ct));
    }

    public async Task<ChatSession> SwitchToNewSessionAsync(
        Guid currentSessionId, 
        long masterId, 
        long messengerChatId, 
        string? newBranch = null, 
        string messengerType = "telegram", 
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);

        // 1. Закрываем текущую сессию и изолируем старые сообщения
        const string closeOldSql = """
            UPDATE chat_sessions 
            SET is_active = FALSE, closed_at = NOW(), updated_at = NOW() 
            WHERE id = @CurrentSessionId;

            UPDATE messages 
            SET is_frozen = TRUE 
            WHERE session_id = @CurrentSessionId;
        """;
        await conn.ExecuteAsync(new CommandDefinition(closeOldSql, new { CurrentSessionId = currentSessionId }, cancellationToken: ct));

        // 2. Создаем НОВУЮ сессию с новым UUID SessionId
        var employeeId = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition("SELECT id FROM employees WHERE master_id = @MasterId LIMIT 1;", new { MasterId = masterId }, cancellationToken: ct));

        var branch = !string.IsNullOrWhiteSpace(newBranch) ? newBranch : "general";
        const string insertNewSql = """
            INSERT INTO chat_sessions (id, master_id, employee_id, messenger_type, messenger_chat_id, is_active, title, current_branch)
            VALUES (gen_random_uuid(), @MasterId, @EmployeeId, @MessengerType, @MessengerChatId, TRUE, 'Диалог (' || @Branch || ') ' || @MasterId, @Branch)
            RETURNING *, messenger_chat_id AS TelegramChatId;
        """;

        return await conn.QuerySingleAsync<ChatSession>(
            new CommandDefinition(insertNewSql, new 
            { 
                MasterId = masterId, 
                EmployeeId = employeeId, 
                MessengerType = messengerType, 
                MessengerChatId = messengerChatId, 
                Branch = branch 
            }, cancellationToken: ct));
    }

    public async Task<List<ChatMessage>> GetMessagesBySessionIdAsync(Guid sessionId, int limit = 20, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT * FROM messages 
            WHERE session_id = @SessionId 
            ORDER BY created_at DESC 
            LIMIT @Limit;
        """;
        var list = (await conn.QueryAsync<ChatMessage>(
            new CommandDefinition(sql, new { SessionId = sessionId, Limit = limit }, cancellationToken: ct))).ToList();
        list.Reverse();
        return list;
    }

    public async Task SaveMessageToSessionAsync(
        Guid sessionId, 
        long masterId, 
        string role, 
        string content, 
        bool isFrozen = false, 
        object? metadata = null, 
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";
        const string sql = """
            INSERT INTO messages (id, session_id, master_id, context_id, role, content, is_frozen, metadata)
            VALUES (gen_random_uuid(), @SessionId, @MasterId, @ContextId, @Role, @Content, @IsFrozen, @Metadata::jsonb);

            UPDATE chat_sessions SET updated_at = NOW() WHERE id = @SessionId;
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new 
        { 
            SessionId = sessionId, 
            MasterId = masterId, 
            ContextId = sessionId.ToString(), 
            Role = role, 
            Content = content, 
            IsFrozen = isFrozen, 
            Metadata = metaJson 
        }, cancellationToken: ct));
    }

    public async Task<ChatSession> GetOrCreateSessionAsync(long telegramChatId, long? telegramUserId = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string selectSql = "SELECT *, messenger_chat_id AS TelegramChatId FROM chat_sessions WHERE messenger_chat_id = @TelegramChatId ORDER BY updated_at DESC LIMIT 1;";
        var session = await conn.QueryFirstOrDefaultAsync<ChatSession>(new CommandDefinition(selectSql, new { TelegramChatId = telegramChatId }, cancellationToken: ct));

        if (session != null) return session;

        int? employeeId = null;
        long masterId = telegramUserId ?? telegramChatId;
        if (telegramUserId.HasValue)
        {
            var emp = await conn.QueryFirstOrDefaultAsync<(int Id, long MasterId)>(
                new CommandDefinition("SELECT id AS Id, master_id AS MasterId FROM employees WHERE telegram_id = @TgId LIMIT 1;", new { TgId = telegramUserId.Value }, cancellationToken: ct));
            if (emp.Id > 0)
            {
                employeeId = emp.Id;
                masterId = emp.MasterId;
            }
        }

        const string insertSql = """
            INSERT INTO chat_sessions (master_id, employee_id, messenger_type, messenger_chat_id, title, current_branch)
            VALUES (@MasterId, @EmployeeId, 'telegram', @TelegramChatId, 'Чат-сессия ' || @TelegramChatId, 'general')
            RETURNING *, messenger_chat_id AS TelegramChatId;
        """;
        return await conn.QuerySingleAsync<ChatSession>(new CommandDefinition(insertSql, new { MasterId = masterId, TelegramChatId = telegramChatId, EmployeeId = employeeId }, cancellationToken: ct));
    }

    public async Task SaveMessageAsync(Guid sessionId, string contextId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";
        const string sql = """
            INSERT INTO messages (session_id, context_id, role, content, is_frozen, metadata)
            VALUES (@SessionId, @ContextId, @Role, @Content, @IsFrozen, @Metadata::jsonb);

            UPDATE chat_sessions SET updated_at = NOW() WHERE id = @SessionId;
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new { SessionId = sessionId, ContextId = contextId, Role = role, Content = content, IsFrozen = isFrozen, Metadata = metaJson }, cancellationToken: ct));
    }

    public async Task<List<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int limit = 20, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT * FROM messages 
            WHERE session_id = @SessionId 
            ORDER BY created_at DESC 
            LIMIT @Limit;
        """;
        var list = (await conn.QueryAsync<ChatMessage>(new CommandDefinition(sql, new { SessionId = sessionId, Limit = limit }, cancellationToken: ct))).ToList();
        list.Reverse();
        return list;
    }

    public async Task FreezeContextAsync(Guid sessionId, string contextId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = "UPDATE messages SET is_frozen = TRUE WHERE session_id = @SessionId AND context_id = @ContextId;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { SessionId = sessionId, ContextId = contextId }, cancellationToken: ct));
    }

    public async Task<Guid> SaveDocumentAsync(string title, string sourceType, string? filePath, string? department, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";
        var groupsJson = JsonSerializer.Serialize(allowedAccessGroups);
        const string sql = """
            INSERT INTO corporate_documents (title, source_type, file_path, department, allowed_access_groups, metadata)
            VALUES (@Title, @SourceType, @FilePath, @Department, @Groups::jsonb, @Metadata::jsonb)
            RETURNING id;
        """;
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new { Title = title, SourceType = sourceType, FilePath = filePath, Department = department, Groups = groupsJson, Metadata = metaJson }, cancellationToken: ct));
    }

    public async Task SaveDocumentChunksAsync(Guid documentId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var groupsJson = JsonSerializer.Serialize(allowedAccessGroups);

        for (int i = 0; i < chunkTexts.Count; i++)
        {
            var text = chunkTexts[i];
            Vector? vec = embeddings.Count > i && embeddings[i] != null ? new Vector(embeddings[i]!) : null;

            const string sql = """
                INSERT INTO corporate_document_chunks (document_id, chunk_index, chunk_text, embedding, allowed_access_groups)
                VALUES (@DocumentId, @ChunkIndex, @ChunkText, @Embedding, @Groups::jsonb);
            """;
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                DocumentId = documentId,
                ChunkIndex = i + 1,
                ChunkText = text,
                Embedding = vec,
                Groups = groupsJson
            }, cancellationToken: ct));
        }
    }

    public async Task<List<CorporateDocumentChunk>> GetChunksWithoutEmbeddingAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT c.id, c.document_id AS DocumentId, c.chunk_index AS ChunkIndex,
                   c.chunk_text AS ChunkText, c.metadata::text AS MetadataJson
            FROM corporate_document_chunks c
            WHERE c.embedding IS NULL
            ORDER BY c.created_at ASC, c.chunk_index ASC
            LIMIT @Limit;
        """;
        var chunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct));
        return chunks.ToList();
    }

    public async Task UpdateChunkEmbeddingAsync(Guid chunkId, float[] embedding, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var vec = new Vector(embedding);
        const string sql = "UPDATE corporate_document_chunks SET embedding = @Embedding WHERE id = @Id;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = chunkId, Embedding = vec }, cancellationToken: ct));
    }

    private static async Task EnsureMeetingChunksSchemaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_meetingChunksEnsured) return;
        await _meetingSchemaLock.WaitAsync(ct);
        try
        {
            if (_meetingChunksEnsured) return;
            const string ddl = """
                CREATE TABLE IF NOT EXISTS meeting_chunks (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    meeting_id UUID NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                    protocol_id UUID REFERENCES meeting_protocols(id) ON DELETE SET NULL,
                    chunk_index INT NOT NULL,
                    chunk_text TEXT NOT NULL,
                    embedding vector(1536),
                    allowed_access_groups JSONB NOT NULL DEFAULT '[]'::jsonb,
                    metadata JSONB DEFAULT '{}'::jsonb,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_meeting_chunks_meeting_id ON meeting_chunks(meeting_id);
                CREATE INDEX IF NOT EXISTS idx_meeting_chunks_allowed_groups ON meeting_chunks USING GIN(allowed_access_groups);
                CREATE INDEX IF NOT EXISTS idx_meeting_chunks_embedding ON meeting_chunks USING hnsw (embedding vector_cosine_ops);
            """;
            await conn.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: ct));
            _meetingChunksEnsured = true;
        }
        finally
        {
            _meetingSchemaLock.Release();
        }
    }

    public async Task SaveMeetingChunksAsync(Guid meetingId, Guid? protocolId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        await EnsureMeetingChunksSchemaAsync(conn, ct);
        var groupsJson = JsonSerializer.Serialize(allowedAccessGroups ?? new List<string>());
        var metaJson = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";

        for (int i = 0; i < chunkTexts.Count; i++)
        {
            var text = chunkTexts[i];
            Vector? vec = embeddings.Count > i && embeddings[i] != null ? new Vector(embeddings[i]!) : null;

            const string sql = """
                INSERT INTO meeting_chunks (meeting_id, protocol_id, chunk_index, chunk_text, embedding, allowed_access_groups, metadata)
                VALUES (@MeetingId, @ProtocolId, @ChunkIndex, @ChunkText, @Embedding, @Groups::jsonb, @Metadata::jsonb);
            """;
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                MeetingId = meetingId,
                ProtocolId = protocolId,
                ChunkIndex = i + 1,
                ChunkText = text,
                Embedding = vec,
                Groups = groupsJson,
                Metadata = metaJson
            }, cancellationToken: ct));
        }
    }

    public async Task<List<CorporateDocumentChunk>> GetMeetingChunksWithoutEmbeddingAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        await EnsureMeetingChunksSchemaAsync(conn, ct);
        const string sql = """
            SELECT mc.id, mc.meeting_id AS DocumentId, mc.chunk_index AS ChunkIndex,
                   mc.chunk_text AS ChunkText, mc.metadata::text AS MetadataJson
            FROM meeting_chunks mc
            WHERE mc.embedding IS NULL
            ORDER BY mc.created_at ASC, mc.chunk_index ASC
            LIMIT @Limit;
        """;
        var chunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct));
        return chunks.ToList();
    }

    public async Task UpdateMeetingChunkEmbeddingAsync(Guid chunkId, float[] embedding, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        await EnsureMeetingChunksSchemaAsync(conn, ct);
        var vec = new Vector(embedding);
        const string sql = "UPDATE meeting_chunks SET embedding = @Embedding WHERE id = @Id;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = chunkId, Embedding = vec }, cancellationToken: ct));
    }

    public static bool IsGeneralCatalogQuery(string? queryText)
    {
        var normalizedQuery = (queryText ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedQuery)
            || normalizedQuery is "*" or "all" or "все" or "раг" or "rag"
            || normalizedQuery.Contains("что лежит")
            || normalizedQuery.Contains("что есть")
            || normalizedQuery.Contains("какие документ")
            || normalizedQuery.Contains("какие регламент")
            || normalizedQuery.Contains("список документ")
            || normalizedQuery.Contains("база знаний")
            || normalizedQuery.Contains(" в раг")
            || normalizedQuery.Contains(" в rag");
    }

    public async Task<List<CorporateDocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var vec = new Vector(queryEmbedding);
        var effectiveGroups = (userAccessGroups != null && userAccessGroups.Count > 0)
            ? userAccessGroups.Union(new[] { "all" }, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { "all" };
        var groupsJson = JsonSerializer.Serialize(effectiveGroups);

        const string sql = """
            SELECT c.id, c.document_id AS DocumentId, c.chunk_index AS ChunkIndex,
                   CONCAT('[' || d.title || ' (' || COALESCE(d.department, 'Общий') || ')]: ', c.chunk_text) AS ChunkText,
                   COALESCE(NULLIF(c.metadata::text, '{}'), d.metadata::text) AS MetadataJson
            FROM corporate_document_chunks c
            JOIN corporate_documents d ON d.id = c.document_id
            WHERE d.source_type <> 'google_meet'
              AND c.embedding IS NOT NULL
              AND (
                  c.allowed_access_groups ? 'all' 
                  OR c.allowed_access_groups ?| ARRAY(SELECT jsonb_array_elements_text(@Groups::jsonb))
              )
            ORDER BY c.embedding <=> @QueryVec
            LIMIT @TopK;
        """;

        var chunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(sql, new
        {
            QueryVec = vec,
            Groups = groupsJson,
            TopK = topK
        }, cancellationToken: ct));

        return chunks.ToList();
    }

    public async Task<List<CorporateDocumentChunk>> SearchMeetingSimilarChunksAsync(float[] queryEmbedding, List<string> meetingAccessGroups, int topK = 5, CancellationToken ct = default)
    {
        if (meetingAccessGroups == null || meetingAccessGroups.Count == 0)
        {
            return new List<CorporateDocumentChunk>();
        }

        await using var conn = await CreateConnectionAsync(ct);
        await EnsureMeetingChunksSchemaAsync(conn, ct);
        var vec = new Vector(queryEmbedding);
        var groupsJson = JsonSerializer.Serialize(meetingAccessGroups.Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        // Строгая изоляция созвонов в отдельной таблице meeting_chunks: доступ только по master:<id> участника или c-level (Директор), без 'all'
        const string sql = """
            SELECT mc.id, mc.meeting_id AS DocumentId, mc.chunk_index AS ChunkIndex,
                   mc.chunk_text AS ChunkText,
                   mc.metadata::text AS MetadataJson
            FROM meeting_chunks mc
            JOIN meetings m ON m.id = mc.meeting_id
            WHERE mc.embedding IS NOT NULL
              AND mc.allowed_access_groups ?| ARRAY(SELECT jsonb_array_elements_text(@Groups::jsonb))
            ORDER BY mc.embedding <=> @QueryVec
            LIMIT @TopK;
        """;

        var chunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(sql, new
        {
            QueryVec = vec,
            Groups = groupsJson,
            TopK = topK
        }, cancellationToken: ct));

        return chunks.ToList();
    }

    public async Task<List<CorporateDocumentChunk>> SearchTextChunksAsync(string queryText, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var effectiveGroups = (userAccessGroups != null && userAccessGroups.Count > 0)
            ? userAccessGroups.Union(new[] { "all" }, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { "all" };
        var groupsJson = JsonSerializer.Serialize(effectiveGroups);

        // Если запрос пустой или общий («что лежит в раг», «все документы», «база знаний») — возвращаем все доступные пользователю регламенты
        bool isGeneralCatalogQuery = IsGeneralCatalogQuery(queryText);

        const string allAccessibleSql = """
            SELECT c.id, c.document_id AS DocumentId, c.chunk_index AS ChunkIndex,
                   CONCAT('[' || d.title || ' (' || COALESCE(d.department, 'Общий') || ')]: ', c.chunk_text) AS ChunkText,
                   c.metadata::text AS MetadataJson
            FROM corporate_document_chunks c
            JOIN corporate_documents d ON d.id = c.document_id
            WHERE d.source_type <> 'google_meet'
              AND (
                  c.allowed_access_groups ? 'all'
                  OR c.allowed_access_groups ?| ARRAY(SELECT jsonb_array_elements_text(@Groups::jsonb))
              )
            ORDER BY d.created_at ASC, c.chunk_index ASC
            LIMIT @TopK;
        """;

        if (isGeneralCatalogQuery)
        {
            var allChunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(allAccessibleSql, new
            {
                Groups = groupsJson,
                TopK = Math.Max(topK, 10)
            }, cancellationToken: ct));
            return allChunks.ToList();
        }

        const string sql = """
            SELECT c.id, c.document_id AS DocumentId, c.chunk_index AS ChunkIndex,
                   CONCAT('[' || d.title || ' (' || COALESCE(d.department, 'Общий') || ')]: ', c.chunk_text) AS ChunkText,
                   c.metadata::text AS MetadataJson
            FROM corporate_document_chunks c
            JOIN corporate_documents d ON d.id = c.document_id
            WHERE d.source_type <> 'google_meet'
              AND (
                  c.allowed_access_groups ? 'all' 
                  OR c.allowed_access_groups ?| ARRAY(SELECT jsonb_array_elements_text(@Groups::jsonb))
              )
              AND (c.chunk_text ILIKE '%' || @QueryText || '%' OR d.title ILIKE '%' || @QueryText || '%')
            LIMIT @TopK;
        """;

        var chunks = (await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(sql, new
        {
            QueryText = queryText,
            Groups = groupsJson,
            TopK = topK
        }, cancellationToken: ct))).ToList();

        if (chunks.Count > 0)
        {
            return chunks;
        }

        // Фолбэк по отдельным ключевым словам (>= 3 символов), включая корни слов (первые 4-5 букв для русской морфологии)
        var rawWords = (queryText ?? "")
            .Split(new[] { ' ', ',', '.', ';', '?', '!', '-', '"', '\'', '«', '»' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patternSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in rawWords)
        {
            patternSet.Add($"%{word}%");
            if (word.Length >= 5)
            {
                patternSet.Add($"%{word[..^1]}%");
            }
            if (word.Length >= 6)
            {
                patternSet.Add($"%{word[..^2]}%");
            }
        }

        if (patternSet.Count == 0)
        {
            return chunks;
        }

        const string kwSql = """
            SELECT c.id, c.document_id AS DocumentId, c.chunk_index AS ChunkIndex,
                   CONCAT('[' || d.title || ' (' || COALESCE(d.department, 'Общий') || ')]: ', c.chunk_text) AS ChunkText,
                   c.metadata::text AS MetadataJson
            FROM corporate_document_chunks c
            JOIN corporate_documents d ON d.id = c.document_id
            WHERE d.source_type <> 'google_meet'
              AND (
                  c.allowed_access_groups ? 'all'
                  OR c.allowed_access_groups ?| ARRAY(SELECT jsonb_array_elements_text(@Groups::jsonb))
              )
              AND (c.chunk_text ILIKE ANY(@Patterns) OR d.title ILIKE ANY(@Patterns))
            LIMIT @TopK;
        """;

        var kwChunks = await conn.QueryAsync<CorporateDocumentChunk>(new CommandDefinition(kwSql, new
        {
            Patterns = patternSet.ToArray(),
            Groups = groupsJson,
            TopK = topK
        }, cancellationToken: ct));

        return kwChunks.ToList();
    }

    public async Task<Guid> CreateMeetingAsync(
        string title,
        string? description,
        DateTime startTime,
        DateTime? endTime = null,
        string status = "completed",
        int? organizerId = null,
        List<int>? participantIds = null,
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var participantsJson = JsonSerializer.Serialize(participantIds ?? new List<int>());
        const string sql = """
            INSERT INTO meetings (title, description, start_time, end_time, status, organizer_id, participant_ids)
            VALUES (@Title, @Description, @StartTime, @EndTime, @Status, @OrganizerId, @Participants::jsonb)
            RETURNING id;
        """;
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            Title = title,
            Description = description,
            StartTime = startTime,
            EndTime = endTime,
            Status = status,
            OrganizerId = organizerId,
            Participants = participantsJson
        }, cancellationToken: ct));
    }

    public async Task<List<Meeting>> GetUpcomingMeetingsAsync(int timeWindowMinutes = 30, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT * FROM meetings
            WHERE status = 'scheduled'
              AND start_time >= NOW() - INTERVAL '5 minutes'
              AND start_time <= NOW() + (@WindowMinutes || ' minutes')::interval
            ORDER BY start_time ASC;
        """;
        var meetings = await conn.QueryAsync<Meeting>(new CommandDefinition(sql, new { WindowMinutes = timeWindowMinutes }, cancellationToken: ct));
        return meetings.ToList();
    }

    public async Task<Meeting?> GetMeetingByIdAsync(Guid meetingId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = "SELECT * FROM meetings WHERE id = @Id;";
        return await conn.QueryFirstOrDefaultAsync<Meeting>(new CommandDefinition(sql, new { Id = meetingId }, cancellationToken: ct));
    }

    public async Task<List<MeetingNote>> GetMeetingNotesAsync(Guid meetingId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = "SELECT * FROM meeting_notes WHERE meeting_id = @MeetingId ORDER BY created_at ASC;";
        var notes = await conn.QueryAsync<MeetingNote>(new CommandDefinition(sql, new { MeetingId = meetingId }, cancellationToken: ct));
        return notes.ToList();
    }

    public async Task SaveMeetingNoteAsync(Guid meetingId, int? authorId, string noteText, string category = "general", CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            INSERT INTO meeting_notes (meeting_id, author_id, note_text, category)
            VALUES (@MeetingId, @AuthorId, @NoteText, @Category);
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new { MeetingId = meetingId, AuthorId = authorId, NoteText = noteText, Category = category }, cancellationToken: ct));
    }

    public async Task<Guid> SaveMeetingProtocolAsync(Guid meetingId, string title, string summary, List<string> decisions, string? rawTranscript = null, string? docxPath = null, string? pdfPath = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var decisionsJson = JsonSerializer.Serialize(decisions);
        const string sql = """
            INSERT INTO meeting_protocols (meeting_id, title, summary, decisions, raw_transcript, file_docx_path, file_pdf_path)
            VALUES (@MeetingId, @Title, @Summary, @Decisions::jsonb, @RawTranscript, @DocxPath, @PdfPath)
            RETURNING id;
        """;
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            MeetingId = meetingId,
            Title = title,
            Summary = summary,
            Decisions = decisionsJson,
            RawTranscript = rawTranscript,
            DocxPath = docxPath,
            PdfPath = pdfPath
        }, cancellationToken: ct));
    }

    public async Task<MeetingProtocol?> GetMeetingProtocolAsync(Guid meetingId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = "SELECT * FROM meeting_protocols WHERE meeting_id = @MeetingId ORDER BY created_at DESC LIMIT 1;";
        return await conn.QueryFirstOrDefaultAsync<MeetingProtocol>(new CommandDefinition(sql, new { MeetingId = meetingId }, cancellationToken: ct));
    }

    public async Task<Guid> CreateActionItemAsync(Guid meetingId, Guid? protocolId, string title, string? description, int? responsibleId, DateTime? deadline, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            INSERT INTO action_items (meeting_id, protocol_id, title, description, responsible_id, deadline, status)
            VALUES (@MeetingId, @ProtocolId, @Title, @Description, @ResponsibleId, @Deadline, 'pending')
            RETURNING id;
        """;
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            MeetingId = meetingId,
            ProtocolId = protocolId,
            Title = title,
            Description = description,
            ResponsibleId = responsibleId,
            Deadline = deadline
        }, cancellationToken: ct));
    }

    public async Task<List<ActionItem>> GetActionItemsByMeetingAsync(Guid meetingId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = "SELECT * FROM action_items WHERE meeting_id = @MeetingId ORDER BY created_at ASC;";
        var items = await conn.QueryAsync<ActionItem>(new CommandDefinition(sql, new { MeetingId = meetingId }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task<List<ActionItem>> GetActionItemsByResponsibleAsync(int employeeId, string? statusFilter = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var sql = "SELECT * FROM action_items WHERE responsible_id = @ResponsibleId";
        if (!string.IsNullOrWhiteSpace(statusFilter) && !statusFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            sql += " AND status = @StatusFilter";
        }
        sql += " ORDER BY deadline ASC NULLS LAST;";

        var items = await conn.QueryAsync<ActionItem>(new CommandDefinition(sql, new { ResponsibleId = employeeId, StatusFilter = statusFilter }, cancellationToken: ct));
        return items.ToList();
    }

    private sealed record MeetingRow(
        Guid Id,
        string Title,
        string? Description,
        DateTime StartTime,
        DateTime? EndTime,
        string Status,
        int? OrganizerId,
        string? ParticipantIdsJson,
        string? CalendarEventId,
        string? MetadataJson,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    public async Task EnsureNotificationSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            ALTER TABLE employees
                ADD COLUMN IF NOT EXISTS notifications_enabled BOOLEAN NOT NULL DEFAULT TRUE;

            CREATE TABLE IF NOT EXISTS notification_deliveries (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                dedup_key VARCHAR(255) NOT NULL UNIQUE,
                notification_type VARCHAR(100) NOT NULL,
                recipient_employee_id INT REFERENCES employees(id) ON DELETE SET NULL,
                recipient_telegram_id BIGINT NOT NULL,
                entity_id UUID,
                status VARCHAR(30) NOT NULL DEFAULT 'pending',
                error_message TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                sent_at TIMESTAMPTZ
            );

            CREATE INDEX IF NOT EXISTS idx_notification_deliveries_type_created
                ON notification_deliveries(notification_type, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_notification_deliveries_telegram_id
                ON notification_deliveries(recipient_telegram_id);
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<Employee?> GetEmployeeByTelegramIdAsync(long telegramId, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT
                id AS Id,
                master_id AS MasterId,
                first_name AS FirstName,
                last_name AS LastName,
                middle_name AS MiddleName,
                email AS Email,
                telegram_id AS TelegramId,
                telegram_username AS TelegramUsername,
                whatsapp_id AS WhatsappId,
                department AS Department,
                position AS Position,
                notifications_enabled AS NotificationsEnabled,
                is_active AS IsActive,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM employees
            WHERE telegram_id = @TelegramId
            ORDER BY is_active DESC, id ASC
            LIMIT 1;
        """;
        return await conn.QueryFirstOrDefaultAsync<Employee>(
            new CommandDefinition(sql, new { TelegramId = telegramId }, cancellationToken: ct));
    }

    public async Task<bool> SetEmployeeNotificationsEnabledAsync(long masterId, bool enabled, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            UPDATE employees
            SET notifications_enabled = @Enabled,
                updated_at = NOW()
            WHERE master_id = @MasterId;
        """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql, new { MasterId = masterId, Enabled = enabled }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<List<Meeting>> GetMeetingsInRangeUtcAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            SELECT
                id AS Id,
                title AS Title,
                description AS Description,
                start_time AS StartTime,
                end_time AS EndTime,
                status AS Status,
                organizer_id AS OrganizerId,
                participant_ids::text AS ParticipantIdsJson,
                calendar_event_id AS CalendarEventId,
                metadata::text AS MetadataJson,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM meetings
            WHERE status = 'scheduled'
              AND start_time >= @StartUtc
              AND start_time <= @EndUtc
            ORDER BY start_time ASC;
        """;

        var rows = await conn.QueryAsync<MeetingRow>(new CommandDefinition(sql, new
        {
            StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)
        }, cancellationToken: ct));

        return rows.Select(row => new Meeting
        {
            Id = row.Id,
            Title = row.Title,
            Description = row.Description,
            StartTime = row.StartTime,
            EndTime = row.EndTime,
            Status = row.Status,
            OrganizerId = row.OrganizerId,
            ParticipantIds = ParseParticipantIds(row.ParticipantIdsJson),
            CalendarEventId = row.CalendarEventId,
            MetadataJson = row.MetadataJson ?? "{}",
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        }).ToList();
    }

    public async Task<List<ActionItem>> GetOpenActionItemsAsync(int? employeeId = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        var sql = """
            SELECT
                id AS Id,
                meeting_id AS MeetingId,
                protocol_id AS ProtocolId,
                title AS Title,
                description AS Description,
                responsible_id AS ResponsibleId,
                deadline AS Deadline,
                status AS Status,
                bitrix24_task_id AS Bitrix24TaskId,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM action_items
            WHERE responsible_id IS NOT NULL
              AND LOWER(status) NOT IN ('cancelled', 'completed', 'done', 'closed')
        """;

        if (employeeId.HasValue)
        {
            sql += "\n  AND responsible_id = @EmployeeId";
        }

        sql += "\nORDER BY deadline ASC NULLS LAST, created_at ASC;";

        var items = await conn.QueryAsync<ActionItem>(
            new CommandDefinition(sql, new { EmployeeId = employeeId }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task<Guid?> TryClaimNotificationDeliveryAsync(
        string dedupKey,
        string notificationType,
        int? recipientEmployeeId,
        long recipientTelegramId,
        Guid? entityId = null,
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            INSERT INTO notification_deliveries (
                dedup_key,
                notification_type,
                recipient_employee_id,
                recipient_telegram_id,
                entity_id,
                status
            )
            VALUES (
                @DedupKey,
                @NotificationType,
                @RecipientEmployeeId,
                @RecipientTelegramId,
                @EntityId,
                'pending'
            )
            ON CONFLICT (dedup_key) DO UPDATE
            SET status = 'pending',
                created_at = NOW(),
                error_message = NULL
            WHERE notification_deliveries.status = 'pending'
              AND notification_deliveries.created_at < NOW() - INTERVAL '10 minutes'
            RETURNING id;
        """;

        return await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(sql, new
        {
            DedupKey = dedupKey,
            NotificationType = notificationType,
            RecipientEmployeeId = recipientEmployeeId,
            RecipientTelegramId = recipientTelegramId,
            EntityId = entityId
        }, cancellationToken: ct));
    }

    public async Task CompleteNotificationDeliveryAsync(
        Guid deliveryId,
        string status,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            UPDATE notification_deliveries
            SET status = @Status,
                error_message = @ErrorMessage,
                sent_at = CASE WHEN @Status = 'sent' THEN NOW() ELSE sent_at END
            WHERE id = @Id;
        """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = deliveryId,
            Status = status,
            ErrorMessage = errorMessage
        }, cancellationToken: ct));
    }

    private static List<int> ParseParticipantIds(string? participantIdsJson)
    {
        if (string.IsNullOrWhiteSpace(participantIdsJson))
        {
            return new List<int>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(participantIdsJson) ?? new List<int>();
        }
        catch (JsonException)
        {
            return new List<int>();
        }
    }

    public async Task UpdateActionItemStatusAsync(Guid actionItemId, string status, int? bitrix24TaskId = null, CancellationToken ct = default)
    {
        await using var conn = await CreateConnectionAsync(ct);
        const string sql = """
            UPDATE action_items 
            SET status = @Status,
                bitrix24_task_id = COALESCE(@BitrixTaskId, bitrix24_task_id),
                updated_at = NOW()
            WHERE id = @Id;
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = actionItemId, Status = status, BitrixTaskId = bitrix24TaskId }, cancellationToken: ct));
    }
}

internal sealed class JsonbListTypeHandler<T> : SqlMapper.TypeHandler<List<T>>
{
    public override void SetValue(System.Data.IDbDataParameter parameter, List<T>? value)
    {
        parameter.Value = value == null ? "[]" : JsonSerializer.Serialize(value);
    }

    public override List<T> Parse(object value)
    {
        if (value is null or DBNull) return new List<T>();
        if (value is List<T> typed) return typed;
        var json = value.ToString();
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }
}

