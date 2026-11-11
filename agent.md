# 🤖 AGENT.MD — Руководство по работе с кодовой базой Fotoprint AI Copilot

Данный документ предназначен для **ИИ-агентов (нейросетей, LLM-ассистентов)**, работающих с проектом Fotoprint AI Copilot. В нем описана архитектура, структура каталогов, инварианты безопасности и строгие правила внесения изменений в код.

---

## 🧭 1. Назначение и контекст проекта

**Fotoprint AI Copilot** — корпоративная AI-платформа для типографии полного цикла («Фотопринт»).
Система автоматизирует:
1. **Протоколирование планерок (MoM)**: транскрибация аудиозаписей, выделение задач, ответственных, дедлайнов и постановка поручений в CRM.
2. **Семантический поиск (RBAC RAG)**: поиск по внутренней базе знаний, технологическим картам и регламентам оборудования с жесткой фильтрацией по уровню допуска сотрудника.
3. **Безопасный доступ к производственным данным (MCP)**: предоставление контекста из 1С:УНФ и Битрикс24 языковым моделям через защищенные фасады с проверкой ролей.
4. **LLM Observability**: сквозной аудит всех запросов к нейросетям через Langfuse v3 с фиксацией инцидентов безопасности.

---

## 📁 2. Детальная структура проекта

```
Fotoprint/
├── .env.example                       # Шаблон переменных окружения
├── docker-compose.yml                 # Манифест оркестрации всех контейнеров
├── Fotoprint.slnx                     # Спецификация решения .NET 9 (новый формат slnx)
├── libraries.yaml                     # Декларативный реестр всех зависимостей (NuGet, npm, MCP)
├── README.md                          # Пользовательская документация по запуску/остановке
├── agent.md                           # Данный файл: инструкции для ИИ-агента
│
├── infra/                             # Инфраструктурные скрипты и конфигурации
│   ├── postgres/
│   │   └── init/                      # SQL-скрипты автоматической инициализации PostgreSQL
│   │       ├── 01_init_databases.sql  # Создание БД: fotoprint, n8n, langfuse
│   │       ├── 02_schema.sql          # Таблицы: сотрудники, роли, документы, эмбеддинги, протоколы
│   │       └── 03_seed_data.sql       # Начальные данные типографии и тестовые пользователи
│   └── scripts/
│       └── bootstrap.sh               # Скрипт автоматической предзагрузки NuGet и npm модулей
│
├── n8n/                               # Сценарии оркестрации n8n
│   └── workflows/
│       ├── 01_main_router_and_auth.json       # Маршрутизация Telegram-сообщений и auth
│       ├── 02_rbac_rag_pipeline.json          # Векторный RAG-поиск с фильтрацией по ролям
│       ├── 03_pre_meeting_prep_cron.json      # Утренний CRON-сбор повестки и KPI цехов
│       ├── 04_live_meeting_copilot.json       # Интерактивный ассистент прямо во время планерки
│       ├── 05_post_meeting_mom_and_tasks.json # Постобработка аудио, MoM и постановка задач
│       └── 06_executive_summary.json          # Аналитическая сводка для генерального директора
│
├── src/                               # Исходный код сервисов на .NET 9 (C# 13)
│   ├── Fotoprint.Core/                # Общая библиотека предметной области
│   │   ├── Data/
│   │   │   └── FotoprintRepository.cs         # Dapper репозиторий (SQL, pgvector, сотрудники)
│   │   ├── Documents/
│   │   │   └── DocumentParser.cs              # Парсинг DOCX (OpenXML), PDF (PdfPig), XLSX
│   │   ├── Mcp/
│   │   │   └── McpModels.cs                   # Контракты протокола MCP (запросы, ответы, инструменты)
│   │   ├── Models/
│   │   │   └── Entities.cs                    # Доменные модели (Employee, DocumentChunk, Meeting)
│   │   ├── Protocols/
│   │   │   └── MeetingProtocolGenerator.cs    # Генератор структурированных протоколов и задач
│   │   ├── Security/
│   │   │   ├── EmployeeSecurityContext.cs     # Паспорт прав сотрудника, роли, группы доступа
│   │   │   └── SecurityInterceptorMiddleware.cs # Перехватчик запросов, валидатор Telegram ID / RBAC
│   │   └── Telemetry/
│   │       └── LangfuseClient.cs              # Клиент логирования трейсов, спанов и security-алертов
│   │
│   ├── Fotoprint.AiGateway.Api/       # Входной шлюз API (порт 8080)
│   │   ├── Dockerfile
│   │   └── Program.cs                         # Эндпоинты: /api/telegram/webhook, /api/meetings/*
│   │
│   ├── Fotoprint.Mcp.EmployeeReader/  # Сервис аутентификации и прав (порт 8081)
│   │   ├── Dockerfile
│   │   └── Program.cs                         # Валидация прав сотрудников, /api/security/validate
│   │
│   ├── Fotoprint.Mcp.OneC/            # MCP-фасад к 1С:Управление нашей типографией (порт 8082)
│   │   ├── Dockerfile
│   │   └── Program.cs                         # Инструменты: get_order_status, get_production_kpis
│   │
│   ├── Fotoprint.Mcp.Bitrix24/        # MCP-фасад к CRM Битрикс24 (порт 8083)
│   │   ├── Dockerfile
│   │   └── Program.cs                         # Инструменты: get_crm_deal_info, create_meeting_task
│   │
│   └── Fotoprint.Mcp.Calendar/        # MCP-фасад корпоративного календаря (порт 8084)
│       ├── Dockerfile
│       └── Program.cs                         # Инструменты: get_daily_schedule, get_meeting_agenda
│
└── tests/
    └── Fotoprint.Tests/               # Модульные и интеграционные тесты (xUnit, 43 теста)
        ├── ContextSwitchDetectorTests.cs      # Тесты классификации смены контекста через LLM
        ├── MasterIdAndSessionSwitchingTests.cs # Тесты резолвинга MasterId и ротации UUID SessionId
        ├── LangfuseTelemetryAndJudgesTests.cs # Тесты Эпика 6: трейсинг, FinOps, Latency, LLM-as-a-Judge
        ├── FotoprintAiAgentTests.cs           # Тесты автономного ReAct Tool Calling агента
        ├── LoadBalancerAndRollbackTests.cs    # Тесты очереди, Token Bucket и роллбэка на 3 повтора
        ├── PerimeterDefenseTests.cs           # Тесты защиты от сканеров портов и honeypot-ловушек
        ├── GeminiClientTests.cs               # Тесты скользящего окна вызовов Gemini RPM
        ├── FotoprintConfigTests.cs            # Тесты загрузки единого манифеста config.yaml
        ├── DocumentParserAndProtocolTests.cs  # Тесты парсинга документов и генератора MoM
        ├── SecurityContextTests.cs            # Тесты RBAC-прав и групп доступа
        └── SecurityInterceptorTests.cs        # Тесты блокировки неавторизованных запросов (403 Forbidden)
```

---

## 🔒 3. Главные архитектурные инварианты (НЕ НАРУШАТЬ!)

### 3.1. Принцип Zero Trust и RBAC (Контроль доступа)
- **Каждый вызов к любому MCP-инструменту обязан проходить проверку прав доступа**.
- Проверка осуществляется через `SecurityInterceptorMiddleware` и заголовок `X-Telegram-Id`.
- **Запрещено** отключать middleware или возвращать данные в обход проверки прав `EmployeeSecurityContext.HasPermission(...)` или `HasAccessGroup(...)`.
- Если у пользователя нет прав:
  1. Возвращается статус `403 Forbidden` с JSON: `{"error": "Access Denied: ...", "required_permission": "..."}`.
  2. В Langfuse обязательно логируется инцидент безопасности через `ILangfuseClient.LogSecurityAlertAsync`.

### 3.2. Матрица ролей типографии
| Роль / Группа | Доступные действия | Примеры прав |
|---|---|---|
| **Director** (Генеральный директор, C-level) | Полный доступ ко всем модулям, регламентам и финансам | `*` (любые) |
| **Production** (Начальник производства, печатники) | Заказы в 1С, производственные KPI, техкарты оборудования | `onec:read_orders`, `onec:read_kpis`, `rag:production` |
| **Sales** (Менеджеры по продажам) | Сделки в Битрикс24, коммерческие регламенты | `bitrix:read_deals`, `bitrix:create_task`, `rag:sales` |
| **Accounting** (Бухгалтерия) | Финансовая отчетность, взаиморасчеты | `onec:read_finance`, `rag:confidential` |
| **Гости / Внешние** | Блокировка доступа ко всем корпоративным MCP | `403 Forbidden` |

### 3.3. Векторная база данных и RAG
- Векторный тип данных: `vector(1536)` (совместим с моделями OpenAI text-embedding-3-small / ada-002).
- Поиск похожих фрагментов выполняется оператором косинусного расстояния: `<=>`.
- Обязательно накладывается SQL-фильтр по группам доступа сотрудника:
  `WHERE access_group = ANY(@AllowedGroups) OR access_group = 'all'`

### 3.4. Единый манифест зависимостей (`libraries.yaml`)
- Любая новая библиотека (NuGet-пакет, npm-пакет для n8n, Python-зависимость) **ОБЯЗАНА** быть зафиксирована в [libraries.yaml](file:///c:/Users/jotry/Photoprint/libraries.yaml).
- Запрещено добавлять пакеты только в `.csproj`, забывая обновить `libraries.yaml`.

### 3.5. Балансировка нагрузки, Rate Limiting и Роллбэк-система (3 повтора)
- **Защита от Rate Limit нейросетей (HTTP 429)**: Все вызовы к LLM или сценариям n8n должны планироваться через `ILlmLoadBalancer.ScheduleAsync(...)`. Запрещено слать всплески прямых вызовов без контроля частоты (Token/Leaky Bucket) и ограничения параллелизма (`MaxConcurrentRequests`).
- **Сетевой уровень Nginx**: Контейнер `nginx` выполняет входную фильтрацию и сглаживание burst-трафика (`limit_req`).
- **Политика 3 повторов (3 retries)**: На всех потенциально сбойных узлах (HTTP-вызовы к n8n, MCP 1C, MCP Bitrix24, внешним API) обязательно используется `IRollbackManager.ExecuteWithRollbackAsync` с лимитом ровно 3 повтора с экспоненциальным backoff. В n8n нодах `httpRequest` обязательно выставляются флаги `"retryOnFail": true`, `"maxTries": 3`, `"waitBetweenTries": 1000`.
- **Обязательный откат (Compensating Rollback)**: Если узел падает после 3 повторов (всего 4 попытки), система обязана выполнить компенсирующее действие (вернуть статус поручения в безопасное состояние, записать уведомление об откате в сессию и отправить алерт в Langfuse `Node:RollbackTriggered`).

### 3.6. Единый файл конфигурации (`config.yaml`)
- Все динамические параметры (температура, `max_tokens`, модели LLM, прокси, лимиты RPM/TPM, задержки сглаживания, пороги RAG) настраиваются через [config.yaml](file:///c:/Users/jotry/Photoprint/config.yaml).
- Для типизированной загрузки в C# используется `FotoprintConfigLoader.Load()`. Запрещено хардкодить гиперпараметры модели непосредственно в коде эндпоинтов.

### 3.7. Интеграция с Google AI Studio (Gemini) и контроль Rate Limits
- **API Ключ**: берется строго из переменной окружения `GEMINI_API_KEY` (в `.env`). Заголовок авторизации: `x-goog-api-key`.
- **Счетчик и ограничение RPM**: Вызовы производятся через `IGeminiClient`. Клиент непрерывно ведет учет скользящего 60-секундного окна вызовов (`CallsInLast60Seconds`, `RemainingQuotaInCurrentMinute`). Лимит задается в `config.yaml` (по умолчанию 60 RPM, 5 параллельных запросов). Балансировщик обязан сглаживать обращения, предотвращая ошибку `HTTP 429`.

### 3.8. Трехэтапный конвейер и Детекция смены контекста через LLM (ContextSwitchDetector)
- **Этап 1 (Вход & Периметр)**: Прием Telegram/HTTP, Nginx reverse-proxy, проверка прав и защита от сканеров (`PerimeterDefenseMiddleware`), балансировщик нагрузки.
- **Этап 2 (LLM ContextSwitchDetector ДО n8n)**: 
  - Выполняется в `Fotoprint.AiGateway.Api` через `IContextSwitchDetector`.
  - Модели Gemini передаются история предыдущей сессии (`GetMessagesBySessionIdAsync`) и текущее сообщение.
  - LLM возвращает `true/false`, причину и целевую ветку (`suggested_branch`).
  - При `is_context_switched = true` сессия закрывается, создается новая сессия с **новым UUID `SessionId`**, а старые сообщения замораживаются (`is_frozen = true`).
- **Этап 3 (Оркестрация n8n & MCP)**: 
  - n8n получает обогащенный payload с флагом `is_context_switched`, `master_id` и актуальным `session_id`.
  - В n8n сосредоточены все вызовы защищенных MCP-микросервисов (1C, CRM, Calendar, RAG).

### 3.9. Единая БД диалогов, MasterId и ротация UUID SessionId
- **MasterId пользователя**: У каждого сотрудника есть уникальный числовой идентификатор `MasterId` (тип `BIGINT`, содержит **только цифры**).
- **Резолвинг через MCP EmployeeReader**: При входящем сообщении из Telegram (или другого мессенджера) сервис обращается в `EmployeeReader` (`/tools/resolve_master_id?messenger=telegram&messenger_id=...`) для получения `MasterId`.
- **Поиск диалогов по MasterId**: В БД диалогов активная сессия ищется по `master_id` (`WHERE master_id = @MasterId AND is_active = TRUE`).
- **Ротация UUID SessionId при смене диалога**: Диалог идентифицируется уникальным UUID `SessionId`. История для `ContextSwitchDetector` берется строго в рамках текущего `SessionId`. **При детекции смены контекста старая сессия закрывается (`is_active = false`), генерируется НОВЫЙ UUID `SessionId`**, и последующий диалог продолжается в новой ветке.

### 3.10. Телеметрия Langfuse v3, FinOps и LLM-as-a-Judge (Эпик 6)
- **Сквозной распределенный трейсинг (Distributed Tracing)**:
  - Иерархия: `SessionId` (ветка контекста) $\rightarrow$ `TraceId` (сообщение/вебхук) $\rightarrow$ `Span/Generation` (вызовы MCP и LLM).
  - Сквозные HTTP-заголовки между Gateway, n8n и MCP: `X-Trace-Id`, `X-Session-Id`, `X-Master-Id`, `X-Telegram-Id`, `X-Internal-Service-Key`.
  - Каждый трейс обогащается тегами сотрудника: `dept:<department>`, `role:<roles>`, `branch:<branch>`, `context_switched:true/false`.
- **FinOps и учет токенов по 4 узлам**:
  - `Gen:ContextSwitchDetector` (классификация контекста).
  - `Gen:Embedding_RAG` (векторизация запросов и документов).
  - `Gen:Main_Agent` (автономный ReAct агент, выбор инструментов и чат).
  - `Gen:Protocol_And_Executive_Summarizer` (MoM и управленческие протоколы).
  - Раздельный учет: prompt, completion, total, cached токены с расчетом стоимости (`calculatedTotalCost`) по тарифам из `config.yaml`.
- **Замер Latency и Security-события**:
  - Замер Latency каждого вызова: `Span:MCP-EmployeeReader`, `Span:MCP-1C`, `Span:MCP-Bitrix24`, `Span:MCP-Calendar`, `Span:PgVector_Search`.
  - Событие `Event:Security_Violation` логируется при блокировке в .NET Security Interceptor.
- **Оценка качества (Evaluations & LLM-as-a-Judge)**:
  - Пользовательский скоринг: `user_feedback` (`[👍]` / `[👎]`) и `task_extraction_accuracy` (1.0 при нажатии `[✅ В Битрикс]`).
  - Автоматические судьи (`ILlmJudgeService`):
    1. `Faithfulness` (проверка фактов/цифр ответа с сырым JSON от 1C/RAG, отсечка галлюцинаций).
    2. `Context Switch Accuracy` (проверка корректности склейки/разделения тем).
    3. `Formatting & Policy Check` (корпоративный формат, отсутствие утечки системных промптов и API-ключей).

---

## 🛠 4. Правила изменения кода для нейросети

При выполнении задач по модификации кода следуйте этим шагам:

### 4.1. Изменения в C# (.NET 9)
1. **Стиль кода**:
   - Используйте C# 13 и .NET 9 возможности: Minimal APIs, Primary Constructors, Target-typed `new()`, collection expressions `[]`, pattern matching.
   - Используйте File-scoped namespaces (`namespace Fotoprint.Core.Security;`).
   - Всегда поддерживайте включенный режим Nullable Reference Types (`<Nullable>enable</Nullable>`). Не игнорируйте предупреждения компилятора о null.
2. **Асинхронность**:
   - Все операции ввода-вывода (БД, HTTP, чтение файлов) должны быть асинхронными (`async Task<T>`) с передачей `CancellationToken`.
3. **Безопасность SQL**:
   - Все запросы к PostgreSQL через Dapper должны быть строго параметризованными.
   - **Категорически запрещена** интерполяция строк (`$"SELECT * FROM ... WHERE id = '{id}'"`). Используйте `new { id }`.
4. **Контракты MCP инструментов**:
   - При добавлении нового MCP-инструмента в `Program.cs` соответствующего сервиса:
     - Опишите инструмент в списке `tools` (`McpToolDefinition`) с четкой JSON Schema параметров.
     - Добавьте обработчик в ветку эндпоинта `app.MapPost("/execute", ...)`.
     - Проверьте требуемое право через `context.HasPermission(...)`.

### 4.2. Изменения в сценариях n8n (`n8n/workflows/`)
1. Все воркфлоу хранятся в формате JSON в директории `n8n/workflows/`.
2. При редактировании JSON сохраняйте уникальность `id` нод и валидность связей в секции `connections`.
3. Используйте параметры окружения вместо жестко зашитых URL:
   - `{{ $env.GATEWAY_API_URL }}`
   - `{{ $env.MCP_1C_URL }}`
   - `{{ $env.MCP_BITRIX24_URL }}`
   - `{{ $env.MCP_CALENDAR_URL }}`
   - `{{ $env.MCP_EMPLOYEE_READER_URL }}`

### 4.3. Изменения в схеме БД (`infra/postgres/init/`)
1. Изменения схемы вносите в `02_schema.sql` (или создавайте новый миграционный скрипт с инкрементным номером).
2. Тестовые записи для демонстрации и проверки добавляйте в `03_seed_data.sql`.
3. Следите за внешними ключами (`ON DELETE CASCADE` / `ON DELETE SET NULL`) и индексами (`ivfflat` или `hnsw` для pgvector).

---

## 🧪 5. Обязательный чек-лист верификации после внесения правок

Каждый раз перед завершением ответа пользователю ИИ-агент обязан выполнить следующие шаги:

1. **Проверить сборку всего .NET решения**:
   ```powershell
   dotnet build Fotoprint.slnx
   ```
   *Результат должен быть: 0 Ошибок (0 Errors).*

2. **Запустить модульные и интеграционные тесты**:
   ```powershell
   dotnet test
   ```
   *Все тесты (100%) должны успешно проходить.*

3. **Если добавлены новые функции**:
   - Написать соответствующие тесты в `tests/Fotoprint.Tests/`.
   - Проверить как позитивный сценарий (успешное выполнение), так и негативный сценарий (отклонение при отсутствии прав `403 Forbidden`).

4. **Проверить целостность конфигурации**:
   - Убедиться, что новые переменные окружения задокументированы в `.env.example` и проброшены в `docker-compose.yml`.
   - Убедиться, что новые библиотеки добавлены в `libraries.yaml`.

---

## ⚠️ 6. Что категорически ЗАПРЕЩЕНО делать нейросети

❌ **Хардкодить секреты, токены и пароли** в коде C#, docker-compose или воркфлоу n8n. Все секреты берутся исключительно из переменных окружения.
❌ **Отключать или комментировать тесты**, чтобы «скрыть» ошибки компиляции или поломку логики.
❌ **Удалять логику проверки RBAC/безопасности** в угоду «упрощению кода».
❌ **Использовать устаревшие методы .NET Framework** вместо современных практик .NET 9.
❌ **Нарушать обратную совместимость API** без явного требования пользователя.

