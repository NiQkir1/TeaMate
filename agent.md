# 🤖 AGENT.MD — Руководство по работе с кодовой базой Fotoprint AI Copilot

Данный документ предназначен для **ИИ-агентов (нейросетей, LLM-ассистентов) и разработчиков**, работающих с проектом **Fotoprint AI Copilot**. В нем описана актуальная архитектура, структура каталогов, инварианты безопасности и правила внесения изменений в код.

---

## 🧭 1. Назначение и контекст проекта

**Fotoprint AI Copilot** — корпоративная мультиагентная AI-платформа для типографии полного цикла («Фотопринт»).
Система автоматизирует:
1. **Протоколирование планерок (MoM)**: транскрибация встреч, выделение решений, поручений, ответственных, дедлайнов, экспорт в DOCX и постановка задач в Битрикс24.
2. **Семантический и гибридный поиск (RBAC RAG)**: поиск по внутренней базе знаний, технологическим картам, нормам брака Heidelberg, коммерческой политике и трудовым регламентам с жесткой фильтрацией по группам доступа сотрудника (`allowed_access_groups`).
3. **Автономный доступ к производственным данным (ReAct + Parallel MCP Tool Calling)**: предоставление актуальных данных из **1С:УНТ**, **Битрикс24** и **Корпоративного календаря** через изолированные MCP-микросервисы с проверкой прав RBAC.
4. **LLM Observability & FinOps**: сквозной аудит всех запросов, спанов, стоимости токенов (`usageDetails` / `costDetails`) и операционных метрик (`scores`) через **Langfuse v3** (PostgreSQL + ClickHouse + Redis + MinIO).

---

## 📁 2. Детальная структура проекта

```text
Photoprint/
├── .env.example                       # Шаблон переменных окружения
├── config.yaml                        # Единый конфигурационный манифест (Single Source of Truth)
├── docker-compose.yml                 # Манифест оркестрации всех контейнеров (13 сервисов)
├── Fotoprint.slnx                     # Спецификация решения .NET 9 (формат slnx)
├── libraries.yaml                     # Декларативный реестр всех зависимостей (NuGet, npm, MCP)
├── telegram_bridge.py                 # Вспомогательный скрипт моста Telegram
├── README.md                          # Пользовательская документация и диаграмма последовательности
├── agent.md                           # Данный файл: инструкции и инварианты для ИИ-агента
│
├── infra/                             # Инфраструктурные скрипты и конфигурации
│   ├── nginx/
│   │   ├── nginx.conf                 # Reverse-proxy, Anti-Scanner (444), Honeypot, Rate Limiting (burst=200/150)
│   │   └── html/
│   │       └── index.html             # Веб-интерфейс Fotoprint AI Copilot (чат, STT, переключение ролей)
│   ├── postgres/
│   │   └── init/                      # SQL-скрипты автоматической инициализации PostgreSQL 16 + pgvector
│   │       ├── 01_init_databases.sql  # Создание БД: fotoprint, n8n, langfuse
│   │       ├── 02_schema.sql          # Таблицы: сотрудники, роли, сессии, документы, чанки, встречи
│   │       └── 03_seed_data.sql       # Начальные данные типографии, регламенты RAG и тестовые сотрудники
│   └── scripts/
│       └── bootstrap.sh               # Скрипт автоматической предзагрузки NuGet и npm модулей
│
├── n8n/                               # Сценарии оркестрации n8n
│   └── workflows/
│       ├── 01_main_router_and_auth.json       # Входной роутер, проверка сотрудника в EmployeeReader и вызов агента
│       ├── 02_rbac_rag_pipeline.json          # Векторный RAG-поиск с фильтрацией по ролям
│       ├── 03_pre_meeting_prep_cron.json      # Утренний CRON-сбор повестки и KPI цехов
│       ├── 04_live_meeting_copilot.json       # Интерактивный ассистент во время планерки
│       ├── 05_post_meeting_mom_and_tasks.json # Постобработка протокола (MoM) и постановка задач
│       └── 06_executive_summary.json          # Аналитическая сводка для генерального директора
│
├── src/                               # Исходный код сервисов на .NET 9 (C# 13)
│   ├── Fotoprint.Core/                # Общая библиотека ядра предметной области
│   │   ├── Agents/
│   │   │   ├── ContextSwitchDetector.cs       # Гибридный детектор смены контекста (FastPath + LLM fallback)
│   │   │   └── FotoprintAiAgent.cs            # Автономный ReAct-агент (история до 10 сообщений, RBAC-кэш 45с, параллельный MCP)
│   │   ├── Configuration/
│   │   │   └── FotoprintConfig.cs             # Строго типизированная модель и загрузчик config.yaml
│   │   ├── Data/
│   │   │   └── FotoprintRepository.cs         # Dapper-репозиторий (PostgreSQL, pgvector + гибридный стем-поиск RAG)
│   │   ├── Documents/
│   │   │   └── DocumentParser.cs              # Парсинг DOCX (OpenXML), PDF (PdfPig), XLSX и нарезка на чанки
│   │   ├── Llm/
│   │   │   └── GeminiClient.cs                # Клиент NVIDIA NIM / OpenAI API (NvidiaClient, Circuit Breaker, пул ключей, SSE)
│   │   ├── Mcp/
│   │   │   └── McpModels.cs                   # Контракты протокола MCP
│   │   ├── Models/
│   │   │   └── Entities.cs                    # Доменные модели (Employee, ChatSession, ChatMessage, CorporateDocumentChunk)
│   │   ├── Protocols/
│   │   │   └── MeetingProtocolGenerator.cs    # Генератор структурированных протоколов совещаний и DOCX
│   │   ├── Resilience/
│   │   │   ├── LlmLoadBalancer.cs             # Приоритетная очередь (PriorityQueue) + сглаживание 40 RPM (Token/Leaky Bucket)
│   │   │   └── RollbackSystem.cs              # Политика 3 повторов (Exponential Backoff) + компенсирующий роллбэк
│   │   ├── Security/
│   │   │   ├── EmployeeSecurityContext.cs     # Паспорт прав сотрудника (MasterId, Roles, Permissions, AccessGroups)
│   │   │   ├── PerimeterDefenseMiddleware.cs  # Защита от сканеров портов, ботов и запрещенных путей
│   │   │   └── SecurityInterceptorMiddleware.cs # Перехватчик RBAC и проверка X-Internal-Service-Key
│   │   ├── Telegram/
│   │   │   └── TelegramBotClient.cs           # Клиент Telegram Bot API, дедупликатор update_id, парсер Cloudflare Tunnel
│   │   └── Telemetry/
│   │       ├── LangfuseClient.cs              # Клиент Langfuse v3 (Traces с input/output, Spans, Generations с costDetails, Scores)
│   │       └── LlmJudgeService.cs             # Сервис автоматических оценок LLM-as-a-Judge
│   │
│   ├── Fotoprint.AiGateway.Api/       # Входной шлюз API (порт 8080 за Nginx)
│   │   ├── Dockerfile
│   │   ├── Program.cs                         # Эндпоинты: /api/agent/chat, /api/agent/execute, /api/rag/*, /api/meetings/*
│   │   └── Telegram/
│   │       ├── CloudflareWebhookRegistrarService.cs # Фоновая авторегистрация вебхука через Quick Tunnel cloudflared
│   │       ├── TelegramEndpoints.cs           # Эндпоинты /api/telegram/webhook, /api/telegram/info, /api/telegram/webhook/register
│   │       └── TelegramWebhookProcessor.cs    # Асинхронная обработка апдейтов Telegram с индикатором typing и доставкой ответов
│   │
│   ├── Fotoprint.Mcp.EmployeeReader/  # Сервис аутентификации и RBAC-контекста (внутренний порт 8081)
│   ├── Fotoprint.Mcp.OneC/            # MCP-фасад к 1С:УНТ (внутренний порт 8082: заказы, производственные KPI)
│   ├── Fotoprint.Mcp.Bitrix24/        # MCP-фасад к Битрикс24 (внутренний порт 8083: задачи, сделки CRM)
│   └── Fotoprint.Mcp.Calendar/        # MCP-фасад корпоративного календаря (внутренний порт 8084: расписание, история встреч)
│
└── tests/
    └── Fotoprint.Tests/               # Модульные и интеграционные тесты (xUnit, 65 тестов)
        ├── ContextSwitchDetectorTests.cs      # Тесты классификации смены контекста (FastPath + LLM)
        ├── DocumentParserAndProtocolTests.cs  # Тесты парсинга документов и генератора MoM
        ├── FotoprintAiAgentTests.cs           # Тесты автономного агента, пакетного вызова тулов и fail-closed устойчивости
        ├── FotoprintConfigTests.cs            # Тесты загрузки единого манифеста config.yaml
        ├── GeminiClientTests.cs               # Тесты клиента NVIDIA/Gemini, SSE-стриминга и сериализации тулов
        ├── LangfuseTelemetryAndJudgesTests.cs # Тесты трейсинга, FinOps, Latency и LLM-as-a-Judge
        ├── LoadBalancerAndRollbackTests.cs    # Тесты приоритетной очереди, сглаживания RPM и роллбэка на 3 повтора
        ├── MasterIdAndSessionSwitchingTests.cs # Тесты резолвинга MasterId и ротации UUID SessionId
        ├── PerimeterDefenseTests.cs           # Тесты защиты от сканеров портов и honeypot-ловушек
        ├── SecurityContextTests.cs            # Тесты RBAC-прав и групп доступа
        ├── SecurityInterceptorTests.cs        # Тесты блокировки неавторизованных запросов (403 Forbidden)
        └── TelegramBotClientTests.cs          # Тесты Telegram-клиента, дедупликации апдейтов и Cloudflare-туннеля
```

---

## 🔒 3. Главные архитектурные инварианты (НЕ НАРУШАТЬ!)

### 3.1. Принцип Zero Trust, Fail-Closed и RBAC (Контроль доступа)
- **Каждый вызов к любому MCP-инструменту обязан проходить проверку прав доступа** через `InternalServiceAuthMiddleware` (`X-Internal-Service-Key`) и `SecurityInterceptorMiddleware` (`X-Telegram-Id`).
- **Fail-Closed при сбое БД**: В `FotoprintAiAgent` при недоступности PostgreSQL сотрудник переводится в безопасный профиль `Guest` (`EmployeeId = 0`, роль `Guest`, группа доступа `["all"]`) без повышения привилегий.
- **Передача реальных групп доступа в RAG**: Поиск в базе знаний (`SearchTextChunksAsync` / `SearchSimilarChunksAsync`) всегда принимает реальный список `secContext.AccessGroups` сотрудника (объединенный с `"all"`).
- Если у пользователя нет прав на MCP-инструмент:
  1. Возвращается `403 Forbidden` с JSON-описанием требуемого разрешения.
  2. В Langfuse логируется инцидент `Event:Security_Violation` через `ILangfuseClient.LogSecurityAlertAsync`.

### 3.2. Матрица ролей типографии
| Сотрудник (Seed) | `MasterId` / `TelegramId` | Отдел / Роль | Группы доступа RAG (`AccessGroups`) | Разрешения (`Permissions`) |
| :--- | :---: | :--- | :--- | :--- |
| **Алексей Смирнов** | `100001` | Руководство / `Director` | `all`, `management`, `production`, `sales`, `accounting`, `confidential` | `*` (полный доступ) |
| **Иван Кузнецов** | `100002` | Производство / `Production` | `all`, `production` | `onec:read_orders`, `onec:read_production_kpis`, `bitrix:read_tasks`, `calendar:read` |
| **Елена Морозова** | `100003` | Продажи / `Sales` | `all`, `sales` | `onec:read_orders`, `bitrix:read_deals`, `bitrix:read_tasks`, `bitrix:create_task`, `calendar:read` |
| **Ольга Соколова** | `100004` | Бухгалтерия / `Accounting` | `all`, `accounting`, `confidential` | `onec:read_orders`, `onec:read_finance`, `calendar:read` |
| **Внешние / Гости** | Неизвестный ID | `Guest` | Только `all` (на уровне БД), отказ на входе в `n8n` | `403 Forbidden` в `01_main_router_and_auth` |

### 3.3. Гибридный и векторный поиск RAG (`FotoprintRepository.cs`)
- Таблица `corporate_document_chunks` хранит векторные эмбеддинги `vector(1536)` и массив групп допуска `allowed_access_groups TEXT[]`.
- Текстовый и каталожный поиск (`SearchTextChunksAsync`) выполняет `JOIN corporate_documents`, возвращает заголовок документа и отдел, поддерживает обзорные запросы (*«что лежит в раг»*, *«база знаний»*, `*`) и поиск по морфологическим основам русских слов (стемминг), строго соблюдая условие `c.allowed_access_groups && @UserGroups::text[]`.

### 3.4. Интеграция с NVIDIA NIM (`NvidiaClient`) и защита от квоты `40 RPM`
- **Модели семейства NVIDIA Nemotron 3.5**:
  - Для инструментальных шагов ReAct (`ChatTurnAsync` с `tools != null`) используется `nvidia/nemotron-3.5-lightning-30b-a3b` с **`enable_thinking = true`** — это гарантирует 100% точный выбор MCP-инструментов даже при длинной истории диалога.
  - Для синтеза финального ответа по данным MCP (`ChatTurnNoThinkingAsync`) и классификации (`GenerateJsonNoThinkingAsync`) используется `nvidia/nemotron-3.5-lightning-30b-a3b` с **`enable_thinking = false`** (быстрый ответ за ~1.5–2 сек).
  - Для прямой глубокой аналитики (`GenerateContentAsync` без тулов) используется `nvidia/nemotron-3.5-super-vl-120b-a12b` с автоматическим **Circuit Breaker (10 сек TTFT таймаут → переключение на `30b` на 3 минуты)** при сбоях `503 content safety screening`.
- **Чистая UTF-8 сериализация**: Все JSON-результаты инструментов в `FotoprintAiAgent` и тела запросов в `NvidiaClient` сериализуются с `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, чтобы кириллица передавалась в LLM без `\u04XX`-экранирования (экономия токенов в 3–4 раза и отсутствие искажений фактов).
- **Пул API-ключей (`Round-Robin`)**: Переменная `NVIDIA_API_KEY` (или `NVIDIA_API_KEYS`) поддерживает передачу нескольких ключей через запятую/точку с запятой с атомарной ротацией на каждом запросе.
- **Singleton-регистрация**: `NvidiaClient` регистрируется в DI как единый `Singleton`, чтобы счетчик `gemini_rpm_tracker` и состояние Circuit Breaker были общими для всего шлюза.

### 3.5. Ограничение истории контекста до 10 сообщений и RBAC-кэш
- **Лимит истории в промпте (`max_history_messages: 10`)**: В `FotoprintAiAgent.ProcessMessageAsync` загружаются активные (`!IsFrozen`) сообщения текущей сессии, отфильтровывается дубликат текущего входящего сообщения и берется `.TakeLast(10)`:
  - если в сессии $\le 10$ сообщений (например, 9) — отправляются все 9;
  - если в сессии $\ge 11$ сообщений — отправляются ровно 10 последних.
- **Краткосрочный RBAC-кэш (`45 секунд`)**: Идентичные read-only запросы сотрудников с одинаковым профилем прав `(Department, Roles, AccessGroups)` (или персональные запросы того же `EmployeeId`) отдаются из памяти за `~200 мс` с `0` вызовов к LLM.

### 3.6. Балансировка нагрузки (`Nginx` + `LlmLoadBalancer`) и Роллбэк (3 повтора)
- **Nginx (`infra/nginx/nginx.conf`)**:
  - `/api/telegram/webhook`: `rate=50r/s`, `burst=200 delay=50` — пачки из 40–200 сообщений в минуту принимаются без ошибок `429`.
  - `/api/`: `rate=30r/s`, `burst=150 nodelay`.
- **`LlmLoadBalancer` (`src/Fotoprint.Core/Resilience/LlmLoadBalancer.cs`)**:
  - Ограничивает **только непосредственные вызовы к LLM** внутри `NvidiaClient` (внешний вызов `n8n` не занимает слот семафора LLM, что исключает взаимную блокировку при 5+ одновременных пользователях).
  - Реализует приоритетную очередь `PriorityQueue` (запросы руководства с `priority > 0` проходят первыми) и точный расчет освобождения $N$-го слота скользящего минутного окна (`40 RPM`).
- **`RollbackManager` (`src/Fotoprint.Core/Resilience/RollbackSystem.cs`)**:
  - Выполняет 1 основную попытку + до 3 повторов с экспоненциальным backoff и джиттером, а при исчерпании попыток вызывает компенсирующее действие (`compensatingRollbackAction`) и пишет событие `Node:RollbackTriggered` в Langfuse.

### 3.7. Гибридный `ContextSwitchDetector` и ротация `SessionId`
- В `ContextSwitchDetector.DetectSwitchAsync`:
  1. Сначала выполняется мгновенный детерминированный классификатор `TryClassifyFastPath` (`0` вызовов LLM, `< 1 мс`), определяющий доменную ветку (`executive_report`, `task_management`, `meeting_prep`, `meeting_summary`, `doc_search`, `general_chat`) и проверяющий маркеры продолжения диалога.
  2. Мягкие переходы между рабочими вопросами (например, проверил заказ в 1С, затем спросил про задачи в Битрикс24) обновляют `SuggestedBranch`, но оставляют `IsSwitched = false`, сохраняя память сессии.
  3. Явные команды смены задачи (*«поставь задачу...»*, *«назначь встречу...»*) ставят `IsSwitched = true`, архивируют старую сессию (`SwitchToNewSessionAsync`) и создают новую сессию с новым UUID `SessionId`.
  4. Для нестандартных фраз без доменных маркеров вызывается резервный LLM-классификатор (`GenerateJsonNoThinkingAsync`).

### 3.8. Телеметрия Langfuse v3
- **Traces (`CreateTraceAsync`)**: обязательно заполняются поля `input` (вопрос пользователя) и `output` (финальный ответ ассистента), а также теги `status:ok` / `status:error` / `status:forbidden`, `branch:<branch>`, `orchestrator:n8n`.
- **Generations (`CreateGenerationAsync`)**: передаются поля спецификации Langfuse v3 `usageDetails` (`input`, `output`, `total`) и `costDetails` (`input`, `output`, `total` в USD), а в метаданные `Gen:Main_Agent` пишется `history_messages_count`.
- **Spans (`CreateSpanAsync`)**: логируются `Span:N8n_Orchestrator`, `Span:MCP-EmployeeReader`, `Span:MCP-get_order_status`, `Span:MCP-get_production_kpis`, `Span:MCP-get_user_tasks`, `Span:MCP-get_crm_deal_info`, `Span:MCP-create_task`, `Span:MCP-get_upcoming_meetings`, `Span:MCP-get_meeting_history`, `Span:MCP-search_typography_regulations`.
- **Детерминированные Scores (`LogScoreAsync`)**: на каждый запрос без затрат токенов записываются 4 метрики: `agent_success` (`1`/`0`), `tools_invoked_count`, `context_switched` (`1`/`0`), `response_latency_sec`.

---

## 🧪 4. Обязательный чек-лист верификации после внесения правок

1. **Проверить сборку и прогнать все 65 тестов**:
   ```powershell
   dotnet test
   ```
   *Результат должен быть: не пройдено 0, пройдено 65.*

2. **При изменении Docker-сервисов пересобрать контейнер шлюза и перезапустить Nginx**:
   ```powershell
   docker compose up -d --build --no-deps ai-gateway
   docker restart fotoprint-nginx
   ```

3. **Проверить health и сквозной запрос**:
   ```powershell
   Invoke-RestMethod -Uri "http://localhost/health"
   ```

---

## ⚠️ 5. Что категорически ЗАПРЕЩЕНО делать

- ❌ **Хардкодить секреты, токены и пароли** в исходном коде C# или конфигурациях.
- ❌ **Сериализовать русскоязычные данные для LLM без `UnsafeRelaxedJsonEscaping`** (это приводит к `\u04XX`-экранированию, росту токенов в 4 раза и искажению фактов).
- ❌ **Оборачивать внешний вызов `n8n` в тот же семафор `LlmLoadBalancer`, который используется внутри `NvidiaClient`** (это вызывает взаимную блокировку при параллельных запросах).
- ❌ **Отключать проверки RBAC (`SecurityInterceptorMiddleware`, `InternalServiceAuthMiddleware`) или фильтрацию `allowed_access_groups` в SQL-запросах RAG**.
