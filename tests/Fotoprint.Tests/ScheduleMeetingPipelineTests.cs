using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fotoprint.Core.Agents;
using Fotoprint.Core.Configuration;
using Fotoprint.Core.Data;
using Fotoprint.Core.Llm;
using Fotoprint.Core.Mcp;
using Fotoprint.Core.Models;
using Fotoprint.Core.Security;
using Fotoprint.Core.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fotoprint.Tests;

public class ScheduleMeetingPipelineTests
{
    private static readonly TimeZoneInfo MskZone = FotoprintRepository.GetMoscowTimeZone();

    [Fact]
    public async Task Agent_Blocks_ScheduleMeeting_Without_Confirmation_And_Returns_ActionPlan()
    {
        var repo = new InMemoryCalendarRepository();
        var config = new FotoprintConfig();
        var pipelineHandler = new McpCalendarPipelineHandler(repo, config, new AllowAllEmployeeValidator());
        var httpFactory = new PipelineHttpClientFactory(pipelineHandler);
        var gemini = new ScheduleMeetingFakeGeminiClient();

        var agent = new FotoprintAiAgent(gemini, repo, httpFactory, config, new NullLangfuseClient(), null, NullLogger<FotoprintAiAgent>.Instance);

        var result = await agent.ProcessMessageAsync(
            telegramId: 100005, // Рядовой сотрудник (Employee)
            chatId: 100005,
            messageText: "Назначь встречу с Кузнецовым завтра в 11:00 на 45 минут по макетам");

        Assert.Contains("План действий (требуется подтверждение)", result.AnswerText);
        Assert.Equal(0, pipelineHandler.RequestCount);
        Assert.Empty(repo.CreatedMeetings);
    }

    [Fact]
    public async Task Agent_Executes_ScheduleMeeting_After_Confirmation_Through_Full_Mcp_Pipeline()
    {
        var repo = new InMemoryCalendarRepository();
        repo.HistoryMessages.Add(new ChatMessage { Role = "user", Content = "Назначь встречу с Кузнецовым завтра в 11:00 на 45 минут по макетам" });
        repo.HistoryMessages.Add(new ChatMessage { Role = "assistant", Content = "📋 **План действий (требуется подтверждение):**\n1. Назначение встречи «Согласование макетов»\nПожалуйста, подтвердите выполнение плана (напишите «Да, подтверждаю»)." });
        var config = new FotoprintConfig();
        var pipelineHandler = new McpCalendarPipelineHandler(repo, config, new AllowAllEmployeeValidator());
        var httpFactory = new PipelineHttpClientFactory(pipelineHandler);
        var gemini = new ScheduleMeetingFakeGeminiClient();

        var agent = new FotoprintAiAgent(gemini, repo, httpFactory, config, new NullLangfuseClient(), null, NullLogger<FotoprintAiAgent>.Instance);

        var result = await agent.ProcessMessageAsync(
            telegramId: 100005, // Рядовой сотрудник (Employee) — право calendar:create_meetings выдано всем
            chatId: 100005,
            messageText: "Да, подтверждаю");

        Assert.Equal(1, pipelineHandler.RequestCount);
        Assert.Equal(HttpStatusCode.OK, pipelineHandler.LastStatusCode);
        Assert.Equal("true", pipelineHandler.LastUserConfirmedHeader);
        Assert.Equal("100005", pipelineHandler.LastTelegramIdHeader);
        Assert.Contains("Встреча", result.AnswerText);

        var created = Assert.Single(repo.CreatedMeetings);
        Assert.Equal("Согласование макетов", created.Title);
        Assert.Equal("scheduled", created.Status);
        // Организатор строго вызывающий сотрудник (#5), защита от IDOR
        Assert.Equal(5, created.OrganizerId);
        // Участник Кузнецов (#2) найден через умный поиск
        Assert.Contains(2, created.ParticipantIds);
        Assert.DoesNotContain(5, created.ParticipantIds);
        Assert.Equal(TimeSpan.FromMinutes(45), created.EndTime - created.StartTime);

        var startMsk = TimeZoneInfo.ConvertTimeFromUtc(created.StartTime, MskZone);
        Assert.Equal(11, startMsk.Hour);
        Assert.Equal(0, startMsk.Minute);
    }

    [Fact]
    public async Task Mcp_ScheduleMeeting_Blocks_Missing_InternalServiceKey_With_403()
    {
        var repo = new InMemoryCalendarRepository();
        repo.HistoryMessages.Add(new ChatMessage { Role = "user", Content = "Назначь встречу с Кузнецовым завтра в 11:00 на 45 минут по макетам" });
        repo.HistoryMessages.Add(new ChatMessage { Role = "assistant", Content = "📋 **План действий (требуется подтверждение):**\n1. Назначение встречи «Согласование макетов»\nПожалуйста, подтвердите выполнение плана (напишите «Да, подтверждаю»)." });
        var config = new FotoprintConfig();
        var langfuse = new NullLangfuseClient();

        var authMiddleware = new InternalServiceAuthMiddleware(
            _ => Task.CompletedTask,
            config,
            NullLogger<InternalServiceAuthMiddleware>.Instance);

        var ctx = CreateHttpContextWithServices(repo);
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/tools/schedule_meeting";
        // Не передаём X-Internal-Service-Key

        await authMiddleware.InvokeAsync(ctx, langfuse);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Mcp_ScheduleMeeting_Blocks_User_Without_Permission_With_403()
    {
        var repo = new InMemoryCalendarRepository();
        var handlerCalled = false;

        var interceptor = new SecurityInterceptorMiddleware(
            _ => { handlerCalled = true; return Task.CompletedTask; },
            NullLogger<SecurityInterceptorMiddleware>.Instance);

        var ctx = CreateHttpContextWithServices(repo);
        ctx.Request.Headers["X-Telegram-Id"] = "999999"; // Нет права calendar:create_meetings
        ctx.Request.Headers["X-User-Confirmed"] = "true";
        ctx.SetEndpoint(CreateScheduleMeetingEndpoint());

        await interceptor.InvokeAsync(ctx, new AllowAllEmployeeValidator(denyTelegramId: 999999), new NullLangfuseClient());

        Assert.False(handlerCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Mcp_ScheduleMeeting_Blocks_Without_UserConfirmed_Header_With_ConfirmationRequired()
    {
        var repo = new InMemoryCalendarRepository();
        var handlerCalled = false;

        var interceptor = new SecurityInterceptorMiddleware(
            _ => { handlerCalled = true; return Task.CompletedTask; },
            NullLogger<SecurityInterceptorMiddleware>.Instance);

        var ctx = CreateHttpContextWithServices(repo);
        ctx.Request.Headers["X-Telegram-Id"] = "100005";
        // X-User-Confirmed отсутствует
        ctx.SetEndpoint(CreateScheduleMeetingEndpoint());

        await interceptor.InvokeAsync(ctx, new AllowAllEmployeeValidator(), new NullLangfuseClient());

        Assert.False(handlerCalled);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("confirmation_required", body);
        Assert.Empty(repo.CreatedMeetings);
    }

    [Fact]
    public async Task Mcp_ScheduleMeeting_Rejects_Past_StartTime_With_400()
    {
        var repo = new InMemoryCalendarRepository();
        var ctx = CreateHttpContextWithServices(repo);
        ctx.Items["SecurityContext"] = new EmployeeSecurityContext
        {
            EmployeeId = 5,
            FullName = "Петров Пётр Петрович",
            TelegramId = 100005,
            Roles = new() { "Employee" },
            Permissions = new() { "calendar:create_meetings" }
        };

        var req = new ScheduleMeetingRequest("Совещание", null, "2020-01-01 10:00", 30, "Кузнецов");
        var result = await CalendarScheduleHandler.HandleAsync(req, repo, new NullLangfuseClient(), ctx, CancellationToken.None);

        await result.ExecuteAsync(ctx);
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Empty(repo.CreatedMeetings);
    }

    [Fact]
    public async Task Mcp_ScheduleMeeting_Returns_ParticipantsUnresolved_When_Unknown_Or_Ambiguous()
    {
        var repo = new InMemoryCalendarRepository();
        var ctx = CreateHttpContextWithServices(repo);
        ctx.Items["SecurityContext"] = new EmployeeSecurityContext
        {
            EmployeeId = 5,
            FullName = "Петров Пётр Петрович",
            TelegramId = 100005,
            Roles = new() { "Employee" },
            Permissions = new() { "calendar:create_meetings" }
        };

        var futureDate = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd 12:00");

        // 1. Неизвестный участник
        var reqUnknown = new ScheduleMeetingRequest("Совещание", null, futureDate, 30, "Несуществующий");
        var resUnknown = await CalendarScheduleHandler.HandleAsync(reqUnknown, repo, new NullLangfuseClient(), ctx, CancellationToken.None);
        await resUnknown.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var bodyUnknown = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("participants_unresolved", bodyUnknown);
        Assert.Contains("Несуществующий", bodyUnknown);
        Assert.Empty(repo.CreatedMeetings);

        // 2. Неоднозначный запрос ("Ольга" — 0? Нет, добавим в fake "Иван" -> совпадают 2 человека)
        var ctx2 = CreateHttpContextWithServices(repo);
        ctx2.Items["SecurityContext"] = ctx.Items["SecurityContext"];
        var reqAmbiguous = new ScheduleMeetingRequest("Совещание", null, futureDate, 30, "Сергей");
        var resAmbiguous = await CalendarScheduleHandler.HandleAsync(reqAmbiguous, repo, new NullLangfuseClient(), ctx2, CancellationToken.None);
        await resAmbiguous.ExecuteAsync(ctx2);
        ctx2.Response.Body.Seek(0, SeekOrigin.Begin);
        var bodyAmbiguous = await new StreamReader(ctx2.Response.Body).ReadToEndAsync();
        Assert.Contains("participants_unresolved", bodyAmbiguous);
        Assert.Contains("candidates", bodyAmbiguous);
        Assert.Empty(repo.CreatedMeetings);
    }

    private static Endpoint CreateScheduleMeetingEndpoint() => new(
        _ => Task.CompletedTask,
        new EndpointMetadataCollection(
            new RequirePermissionAttribute("calendar:create_meetings"),
            new RequireConfirmationAttribute(true)),
        "ScheduleMeetingEndpoint");

    private static DefaultHttpContext CreateHttpContextWithServices(IFotoprintRepository repo)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repo);
        services.AddSingleton<ILangfuseClient>(new NullLangfuseClient());
        var sp = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private class McpCalendarPipelineHandler : HttpMessageHandler
    {
        private readonly InMemoryCalendarRepository _repo;
        private readonly FotoprintConfig _config;
        private readonly ISecurityValidator _validator;

        public int RequestCount { get; private set; }
        public HttpStatusCode LastStatusCode { get; private set; }
        public string? LastUserConfirmedHeader { get; private set; }
        public string? LastTelegramIdHeader { get; private set; }

        public McpCalendarPipelineHandler(InMemoryCalendarRepository repo, FotoprintConfig config, ISecurityValidator validator)
        {
            _repo = repo;
            _config = config;
            _validator = validator;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUserConfirmedHeader = request.Headers.TryGetValues("X-User-Confirmed", out var uc) ? uc.FirstOrDefault() : null;
            LastTelegramIdHeader = request.Headers.TryGetValues("X-Telegram-Id", out var tg) ? tg.FirstOrDefault() : null;

            var ctx = CreateHttpContextWithServices(_repo);
            ctx.Request.Method = request.Method.Method;
            ctx.Request.Path = request.RequestUri?.AbsolutePath ?? "/tools/schedule_meeting";

            foreach (var h in request.Headers)
            {
                ctx.Request.Headers[h.Key] = h.Value.ToArray();
            }

            string rawBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "{}";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
            ctx.Request.ContentType = "application/json";
            ctx.SetEndpoint(CreateScheduleMeetingEndpoint());

            var langfuse = new NullLangfuseClient();

            var authMiddleware = new InternalServiceAuthMiddleware(async c1 =>
            {
                var interceptor = new SecurityInterceptorMiddleware(async c2 =>
                {
                    var req = JsonSerializer.Deserialize<ScheduleMeetingRequest>(
                        rawBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    var result = await CalendarScheduleHandler.HandleAsync(req, _repo, langfuse, c2, cancellationToken);
                    await result.ExecuteAsync(c2);
                }, NullLogger<SecurityInterceptorMiddleware>.Instance);

                await interceptor.InvokeAsync(c1, _validator, langfuse);
            }, _config, NullLogger<InternalServiceAuthMiddleware>.Instance);

            await authMiddleware.InvokeAsync(ctx, langfuse);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(ctx.Response.Body).ReadToEndAsync(cancellationToken);
            LastStatusCode = (HttpStatusCode)ctx.Response.StatusCode;

            return new HttpResponseMessage(LastStatusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private class PipelineHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public PipelineHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private class AllowAllEmployeeValidator : ISecurityValidator
    {
        private readonly long? _denyTelegramId;
        public AllowAllEmployeeValidator(long? denyTelegramId = null) => _denyTelegramId = denyTelegramId;

        public Task<(bool IsAllowed, EmployeeSecurityContext? Context, string? Reason)> ValidateAccessAsync(
            long telegramId, string requiredPermission, string actionName, string? traceId = null, CancellationToken ct = default)
        {
            if (_denyTelegramId.HasValue && telegramId == _denyTelegramId.Value)
            {
                return Task.FromResult<(bool, EmployeeSecurityContext?, string?)>((false, null, "Access Denied"));
            }

            var ctx = new EmployeeSecurityContext
            {
                EmployeeId = 5,
                MasterId = 100005,
                FullName = "Петров Пётр Петрович",
                TelegramId = telegramId,
                Roles = new() { "Employee" },
                Permissions = new() { "bitrix24:read_tasks", "calendar:read_meetings", "calendar:create_meetings", "rag:search_department" }
            };
            return Task.FromResult<(bool, EmployeeSecurityContext?, string?)>((true, ctx, null));
        }
    }

    private class ScheduleMeetingFakeGeminiClient : IGeminiClient
    {
        private readonly string _futureStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddDays(1), MskZone).ToString("yyyy-MM-dd 11:00");
        private int _turnCount;

        public Task<GeminiGenerateResponse> GenerateContentAsync(string prompt, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
            => Task.FromResult(new GeminiGenerateResponse(true, "ok", "STOP", 1, 1, 2, 1, null));

        public Task<GeminiTurnResponse> ChatTurnAsync(List<object> contents, object? tools = null, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
        {
            _turnCount++;
            var argsJson = JsonSerializer.SerializeToElement(new
            {
                title = "Согласование макетов",
                description = "Проверить цветопробу",
                start_time = _futureStart,
                duration_minutes = 45,
                participants = "Кузнецов"
            });

            if (_turnCount == 1)
            {
                return Task.FromResult(new GeminiTurnResponse(
                    IsSuccess: true,
                    Text: null,
                    FunctionCall: new GeminiFunctionCall("schedule_meeting", argsJson),
                    FinishReason: "STOP",
                    PromptTokens: 10,
                    CandidatesTokens: 10,
                    TotalTokens: 20,
                    AttemptsMade: 1,
                    ErrorMessage: null));
            }

            return Task.FromResult(new GeminiTurnResponse(
                IsSuccess: true,
                Text: "Встреча «Согласование макетов» успешно создана.",
                FunctionCall: null,
                FinishReason: "STOP",
                PromptTokens: 10,
                CandidatesTokens: 10,
                TotalTokens: 20,
                AttemptsMade: 1,
                ErrorMessage: null));
        }

        public Task<GeminiTurnResponse> ChatTurnNoThinkingAsync(List<object> contents, object? tools = null, string? systemInstruction = null, double? temperature = null, int? maxOutputTokens = null, string? traceId = null, CancellationToken ct = default)
            => ChatTurnAsync(contents, tools, systemInstruction, temperature, maxOutputTokens, traceId, ct);

        public GeminiCallStats GetStats() => new(0, 0, 15, 15, 0, null, 0);
    }

    private class NullLangfuseClient : ILangfuseClient
    {
        public string GenerateTraceId() => "test-trace-id";
        public Task CreateTraceAsync(string traceId, string name, string? userId = null, string? sessionId = null, object? metadata = null, string[]? tags = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateSpanAsync(string traceId, string name, DateTime startTime, DateTime endTime, object? input = null, object? output = null, object? metadata = null, string? parentObservationId = null, string? spanId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateGenerationAsync(string traceId, string name, string model, DateTime startTime, DateTime endTime, int promptTokens, int completionTokens, int totalTokens, int cachedTokens = 0, object? input = null, object? output = null, object? metadata = null, double? calculatedCost = null, string? parentObservationId = null, string? generationId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogEventAsync(string traceId, string name, string level = "DEFAULT", string? message = null, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogSecurityAlertAsync(string traceId, long telegramId, string toolOrAction, string requiredPermission, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogScoreAsync(string traceId, string name, double value, string? comment = null, string? observationId = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class InMemoryCalendarRepository : IFotoprintRepository
    {
        public record RecordedMeeting(Guid Id, string Title, string? Description, DateTime StartTime, DateTime EndTime, string Status, int? OrganizerId, List<int> ParticipantIds);

        public List<RecordedMeeting> CreatedMeetings { get; } = new();
        public List<ChatMessage> HistoryMessages { get; } = new();

        private readonly List<User> _employees = new()
        {
            new User { Id = 1, MasterId = 100001, TelegramId = 100001, FirstName = "Алексей", LastName = "Смирнов", MiddleName = "Сергеевич", Department = "Руководство", Position = "Директор", IsActive = true },
            new User { Id = 2, MasterId = 100002, TelegramId = 100002, FirstName = "Иван", LastName = "Кузнецов", MiddleName = "Петрович", Department = "Производство", Position = "Начальник производства", IsActive = true },
            new User { Id = 5, MasterId = 100005, TelegramId = 100005, FirstName = "Пётр", LastName = "Петров", MiddleName = "Петрович", Department = "Производство", Position = "Оператор", IsActive = true },
            new User { Id = 6, MasterId = 100006, TelegramId = 100006, FirstName = "Сергей", LastName = "Иванов", MiddleName = "Иванович", Department = "Склад", Position = "Кладовщик", IsActive = true },
            new User { Id = 7, MasterId = 100007, TelegramId = 100007, FirstName = "Сергей", LastName = "Волков", MiddleName = "Андреевич", Department = "Продажи", Position = "Менеджер", IsActive = true }
        };

        public Task<Guid> CreateMeetingAsync(string title, string? description, DateTime startTime, DateTime? endTime = null, string status = "completed", int? organizerId = null, List<int>? participantIds = null, object? metadata = null, CancellationToken ct = default)
        {
            var id = Guid.NewGuid();
            CreatedMeetings.Add(new RecordedMeeting(id, title, description, startTime, endTime ?? startTime.AddMinutes(30), status, organizerId, participantIds ?? new List<int>()));
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<User>> SearchEmployeesAsync(string query, int limit = 10, CancellationToken ct = default)
        {
            var q = (query ?? string.Empty).Trim().ToLowerInvariant();
            var matched = _employees
                .Where(e => $"{e.LastName} {e.FirstName} {e.MiddleName} {e.Position} {e.Department}".ToLowerInvariant().Contains(q))
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<User>>(matched);
        }

        public Task<List<Employee>> GetAllEmployeesAsync(CancellationToken ct = default)
            => Task.FromResult(_employees.Select(u => new Employee
            {
                Id = u.Id,
                MasterId = u.MasterId,
                TelegramId = u.TelegramId,
                FirstName = u.FirstName,
                LastName = u.LastName,
                MiddleName = u.MiddleName,
                Department = u.Department,
                Position = u.Position,
                IsActive = u.IsActive
            }).ToList());

        public Task<EmployeeSecurityContext?> GetEmployeeSecurityContextAsync(long telegramId, CancellationToken ct = default)
            => Task.FromResult<EmployeeSecurityContext?>(new EmployeeSecurityContext
            {
                EmployeeId = 5,
                MasterId = 100005,
                TelegramId = telegramId,
                FullName = "Петров Пётр Петрович",
                Roles = new() { "Employee" },
                Permissions = new() { "calendar:read_meetings", "calendar:create_meetings" }
            });

        public Task<EmployeeSecurityContext?> GetEmployeeSecurityContextByMasterIdAsync(long masterId, CancellationToken ct = default) => GetEmployeeSecurityContextAsync(masterId, ct);
        public Task<long?> ResolveMasterIdByMessengerAsync(string messengerType, string messengerUserId, CancellationToken ct = default) => Task.FromResult<long?>(100005);
        public Task<Employee?> GetEmployeeByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<Employee?>(null);
        public Task<ChatSession> GetOrCreateActiveSessionByMasterIdAsync(long masterId, long messengerChatId, string messengerType = "telegram", CancellationToken ct = default) => Task.FromResult(new ChatSession { Id = Guid.NewGuid() });
        public Task<ChatSession> SwitchToNewSessionAsync(Guid currentSessionId, long masterId, long messengerChatId, string? newBranch = null, string messengerType = "telegram", CancellationToken ct = default) => Task.FromResult(new ChatSession { Id = Guid.NewGuid() });
        public Task<List<ChatMessage>> GetMessagesBySessionIdAsync(Guid sessionId, int limit = 20, CancellationToken ct = default) => Task.FromResult(HistoryMessages.ToList());
        public Task SaveMessageToSessionAsync(Guid sessionId, long masterId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ChatSession> GetOrCreateSessionAsync(long telegramChatId, long? telegramUserId = null, CancellationToken ct = default) => Task.FromResult(new ChatSession { Id = Guid.NewGuid() });
        public Task SaveMessageAsync(Guid sessionId, string contextId, string role, string content, bool isFrozen = false, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int limit = 20, CancellationToken ct = default) => Task.FromResult(HistoryMessages.ToList());
        public Task FreezeContextAsync(Guid sessionId, string contextId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> SaveDocumentAsync(string title, string sourceType, string? filePath, string? department, List<string> allowedAccessGroups, object? metadata = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task SaveDocumentChunksAsync(Guid documentId, List<string> chunkTexts, List<float[]?> embeddings, List<string> allowedAccessGroups, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<CorporateDocumentChunk>> SearchSimilarChunksAsync(float[] queryEmbedding, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default) => Task.FromResult(new List<CorporateDocumentChunk>());
        public Task<List<CorporateDocumentChunk>> SearchTextChunksAsync(string queryText, List<string> userAccessGroups, int topK = 5, CancellationToken ct = default) => Task.FromResult(new List<CorporateDocumentChunk>());
        public Task<List<Meeting>> GetUpcomingMeetingsAsync(int timeWindowMinutes = 30, CancellationToken ct = default) => Task.FromResult(new List<Meeting>());
        public Task<List<Meeting>> GetMeetingsForRangeAsync(DateTime fromUtc, DateTime toUtc, int? employeeId = null, CancellationToken ct = default) => Task.FromResult(new List<Meeting>());
        public Task<List<TimeSlot>> FindFreeSlotsAsync(int organizerId, List<int> participantIds, DateOnly targetDate, int durationMinutes, TimeOnly? workingStart = null, TimeOnly? workingEnd = null, int bufferMinutes = 10, CancellationToken ct = default) => Task.FromResult(new List<TimeSlot>());
        public Task<Meeting?> GetMeetingByIdAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult<Meeting?>(null);
        public Task<List<MeetingNote>> GetMeetingNotesAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult(new List<MeetingNote>());
        public Task SaveMeetingNoteAsync(Guid meetingId, int? authorId, string noteText, string category = "general", CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> SaveMeetingProtocolAsync(Guid meetingId, string title, string summary, List<string> decisions, string? rawTranscript = null, string? docxPath = null, string? pdfPath = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<MeetingProtocol?> GetMeetingProtocolAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult<MeetingProtocol?>(null);
        public Task<Guid> CreateActionItemAsync(Guid meetingId, Guid? protocolId, string title, string? description, int? responsibleId, DateTime? deadline, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<List<ActionItem>> GetActionItemsByMeetingAsync(Guid meetingId, CancellationToken ct = default) => Task.FromResult(new List<ActionItem>());
        public Task<List<ActionItem>> GetActionItemsByResponsibleAsync(int employeeId, string? statusFilter = null, CancellationToken ct = default) => Task.FromResult(new List<ActionItem>());
        public Task UpdateActionItemStatusAsync(Guid actionItemId, string status, int? bitrix24TaskId = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}

