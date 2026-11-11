# 🖨️ Fotoprint AI Copilot

**Fotoprint AI Copilot** — интеллектуальная мультиагентная платформа и корпоративный AI-ассистент типографии полного цикла («Фотопринт»).

Основные модели системы:
- **Чат, выбор инструментов и саммаризация (LLM):** `nvidia/nemotron-3-ultra-550b-a55b` (NVIDIA NIM, `enable_thinking: true`)
- **Быстрый синтез ответов, детекция смены контекста и Fallback:** `nvidia/nemotron-3.5-bolt-e4b`
- **Векторные эмбеддинги базы знаний и созвонов (RAG):** `gemini-embedding-2` (Google Gemini API, `1536` измерений, L2-нормализация, cosine HNSW в `pgvector`)
- **Распознавание речи (STT):** локальный движок Whisper / Vulkan GPU с облачным fallback `gemini-1.5-flash` и словарём терминологии типографии

Система автоматизирует:
- подготовку к планеркам (`pre-meeting`), живого ассистента на встречах (`live-copilot`) и автоматическое протоколирование (**Minutes of Meeting / DOCX / PDF-HTML**);
- пост-обработку записей **Google Meet** (`Whisper STT → саммари LLM → PostgreSQL + pgvector RAG`) и выдачу саммари / полной стенограммы файлом строго **участникам созвона и директору**;
- оперативный контроль тиражей, выработки и процента брака в **1С:Управление нашей типографией**;
- управление задачами и CRM-сделками в **Битрикс24** (с обязательным **Human-in-the-Loop** подтверждением перед записью) и проверку расписания в **Корпоративном календаре**;
- гибридный и векторный поиск по технологическим регламентам и базе знаний с жестким разграничением прав доступа (**RBAC RAG на базе PostgreSQL 16 + pgvector**);
- голосовой ввод в Telegram и Web UI (`STT`);
- сквозную наблюдаемость, расчет стоимости токенов (**FinOps**) и мониторинг качества в **Langfuse v3** (ClickHouse + PostgreSQL + Redis + MinIO).

---

## 🤖 Полный функционал бота и инструменты агента

Автономный агент [`FotoprintAiAgent`](src/Fotoprint.Core/Agents/FotoprintAiAgent.cs) работает в режиме Tool Calling (до 3 итераций), сохраняет историю диалога до 10 незамороженных сообщений в сессии (по `MasterId` сотрудника) и управляет следующими возможностями:

### 1. Инструменты корпоративных систем (10 MCP / RAG tools)

| Домен | Инструмент | Что делает | Защита / подтверждение |
| :--- | :--- | :--- | :--- |
| **1С:УНТ** | `get_order_status` | Актуальный статус заказа/тиража: этап офсета, % готовности, дата отгрузки, наличие бумаги и краски | Read-only, RBAC (`mcp-1c`) |
| **1С:УНТ** | `get_production_kpis` | Показатели выработки и % брака по цехам за указанный период | Read-only, RBAC (`mcp-1c`) |
| **Битрикс24** | `get_user_tasks` | Список активных и просроченных задач сотрудника | Read-only, RBAC (`mcp-bitrix24`) |
| **Битрикс24** | `get_crm_deal_info` | Карточка сделки и история по клиенту/заказу | Read-only, RBAC (`mcp-bitrix24`) |
| **Битрикс24** | `create_task` | Постановка задачи/поручения в Битрикс24 | **[RequireConfirmation(true)] HITL**: сначала план действий и запрос подтверждения |
| **Календарь и Созвоны** | `get_upcoming_meetings` | Список предстоящих совещаний, тем и участников | Read-only, RBAC (`mcp-calendar`) |
| **Календарь и Созвоны** | `get_meeting_history` | Поиск прошлых встреч, протоколов и поручений по ключевым словам | Read-only, RBAC (`mcp-calendar`) |
| **Календарь и Созвоны** | `search_meeting_rag` | Векторный RAG-поиск (`gemini-embedding-2`) по итогам и стенограммам созвонов в изолированной таблице `meeting_chunks` | **[RequireConfirmation(false)]** строго участники `master:{MasterId}` + Директор (`c-level`) (`mcp-calendar`) |
| **Календарь и Созвоны** | `schedule_meeting` | Назначение новой встречи в корпоративном календаре | **[RequireConfirmation(true)] HITL**: сначала план и подтверждение пользователя |
| **База знаний (RAG)** | `search_typography_regulations` | Векторный поиск (`gemini-embedding-2` + text fallback) по регламентам, техкартам Heidelberg, скидкам, ПВТР в `corporate_document_chunks`; обзор всех документов при `query="*"` | **[RequireConfirmation(false)]** строгий RBAC-фильтр (`mcp-knowledge-base`) |
| **База знаний (RAG)** | `index_corporate_document` | Индексация и векторизация нового корпоративного регламента (через `/tools/index_corporate_document`) | **[RequireConfirmation(true)] HITL**, RBAC `rag:search_all` (`mcp-knowledge-base`) |

### 2. Работа с созвонами Google Meet, аудио и документами

- **Обработка аудио и голоса (Telegram + Web UI):** поддержка голосовых сообщений (`voice`/`audio`) в Telegram и загрузки аудио в Web UI через `/api/audio/transcribe` (Whisper/Gemini + полиграфический словарь `config/vocabulary.yaml`).
- **Аудиопротоколирование совещаний (`POST /api/meetings/process-audio`):** расшифровка аудио → MoM-анализ без галлюцинаций (`MeetingTranscriptAnalyzer`, явная пометка `"требует уточнения"`, если не назван срок или ответственный) → генерация `.docx` → отправка в Telegram с inline-кнопками `[✅ В Битрикс]`.
- **Автоматический пайплайн Google Meet (`07_google_meet_post_processing`):**
  1. Приём вебхука об окончании созвона или опрос календаря по расписанию (cron каждые 10 мин).
  2. Скачивание записи → транскрибация в текст (`Whisper` через `/api/audio/transcribe`).
  3. Суммаризация и извлечение решений/поручений через `nvidia/nemotron-3-ultra-550b-a55b` (`/api/llm/generate`).
  4. Сохранение в БД (`meetings`, `meeting_protocols`, `action_items`) и векторная индексация в `pgvector` (`gemini-embedding-2`) через `/api/meetings/save-and-index`.
- **RAG-воспоминания по созвонам и выгрузка полной стенограммы (`08_meeting_recall_rag_and_export`):**
  1. Пользователь задаёт вопрос по прошлому созвону; по `telegram_id` / `MasterId` определяется его профиль в `mcp-employee-reader`.
  2. Через `/api/meetings/rag-search` векторный поиск `gemini-embedding-2` находит релевантный созвон **только среди тех, где пользователь указан организатором/участником (`master:{MasterId}`), либо для роли Директора (`c-level` / `Director`)**.
  3. Возвращает саммари и принятые решения; при запросе полной расшифровки отдаёт готовый файл (`.docx` или HTML/PDF) через `/api/meetings/{id}/export-full?master_id=...&format=docx|pdf` (посторонним возвращается `403 Forbidden`).

### 3. Пользовательский опыт и безопасность

- **Два канала работы:** **Telegram-бот** (через `cloudflared` HTTPS Quick Tunnel, с индикатором `typing`, дедупликацией `update_id`, кнопками оценки `[👍]/[👎]` и `[✅ В Битрикс]`) и **Web Copilot UI** (переключение ролей Директор / Производство / Продажи / Бухгалтерия / Гость, голосовой ввод).
- **Контроль смены темы (`ContextSwitchDetector`):** при смене темы диалога старая ветка архивируется/замораживается, открывается новый `SessionId`, не смешивая контекст.
- **Ролевая модель доступа (RBAC), anti-IDOR и HITL:** сотрудник видит только документы своего отдела и `all` (`production`, `sales`, `accounting`), свои задачи в Битрикс24 и только те созвоны, где он организатор/участник; Директор (`c-level`) имеет доступ ко всем документам, задачам и созвонам. Прямые внешние вызовы `/api/rag/*` и `/api/agent/execute` проверяются через DB-RBAC или `X-Internal-Service-Key`, а мутирующие операции (`[RequireConfirmation(true)]` / `/tools/call`) требуют `X-User-Confirmed: true`. Неавторизованные пользователи (`Guest`) блокируются на этапе `01_main_router_and_auth`.

---

## 🏗 Архитектура системы

Платформа построена на микросервисной контейнеризированной архитектуре с защищенным внутренним периметром MCP-серверов и автоматическим туннелированием вебхуков Telegram через **Cloudflare Quick Tunnel (`cloudflared`)**:

```text
             ┌──────────────────────────────┐     ┌──────────────────────────────┐
             │   Telegram Bot API (Cloud)   │     │   Web Copilot UI (Browser)   │
             └──────────────┬───────────────┘     └──────────────┬───────────────┘
                            │ (HTTPS Quick Tunnel)               │ (HTTP :80)
                            ▼                                    │
             ┌──────────────────────────────┐                    │
             │    fotoprint-cloudflared     │                    │
             └──────────────┬───────────────┘                    │
                            └─────────────────┬──────────────────┘
                                              ▼
    ┌────────────────────────────────────────────────────────────────────────────┐
    │               ЕДИНАЯ ТОЧКА ВХОДА: NGINX Ingress Load Balancer              │
    │  • Защита от сканеров портов и ботов (Anti-Scanner 444 Drop & Honeypots)   │
    │  • Блокировка внешних вызовов /tools, /mcp, /api/tools и MCP-путей (403)   │
    │  • Очистка anti-spoofing заголовков X-Internal-Service-Key / User-Confirmed│
    │  • Буферизация пиковых нагрузок: burst=200 (Telegram) / burst=150 (Web API)│
    └─────────────────────────────────────────┬──────────────────────────────────┘
                                              │ (Внутренняя сеть fotoprint-net)
                                              ▼
    ┌────────────────────────────────────────────────────────────────────────────┐
    │                     Fotoprint.AiGateway.Api (.NET 9, :8080)                │
    │  • TelegramWebhookProcessor (мгновенный 200 OK, голос/аудио, кнопки, STT)  │
    │  • Гибридный ContextSwitchDetector (FastPath < 1мс + LLM Fallback)         │
    │  • FotoprintAiAgent (история до 10 сообщений, RBAC-кэш 45с, ReAct + HITL)  │
    │  • GeminiEmbeddingClient (gemini-embedding-2, 1536d, фоновая доиндексация) │
    │  • LlmLoadBalancer (PriorityQueue + 40 RPM) & RollbackManager              │
    └──────────────┬──────────────────────────┬──────────────────────────┬───────┘
                   │                          │                          │
                   ▼                          ▼                          ▼
        ┌─────────────────────┐    ┌─────────────────────┐    ┌─────────────────────┐
        │   n8n Orchestrator  │    │   PostgreSQL 16     │    │    Langfuse v3      │
        │  (8 AI Workflows)   │    │   + pgvector (RAG)  │    │  + ClickHouse OLAP  │
        │      Порт 5678      │    │   127.0.0.1:5434    │    │      Порт 3000      │
        └──────────┬──────────┘    └─────────────────────┘    └─────────────────────┘
                   │
                   ▼
    ┌────────────────────────────────────────────────────────────────────────────┐
    │               ИЗОЛИРОВАННЫЙ ВНУТРЕННИЙ ПЕРИМЕТР MCP-СЕРВЕРОВ               │
    │  • Порты закрыты от внешнего мира (доступ только внутри fotoprint-net)     │
    │  • InternalServiceAuthMiddleware (проверка X-Internal-Service-Key)         │
    │  • SecurityInterceptorMiddleware (проверка RBAC-разрешений сотрудника)     │
    ├──────────────────┬─────────────────┬──────────────────┬──────────────────┬─────────────────────┤
    │ EmployeeReader   │ MCP-1C          │ MCP-Bitrix24     │ MCP-Calendar     │ MCP-KnowledgeBase   │
    │ (RBAC/Auth:8081) │ (1С:УНТ :8082)  │ (Задачи/CRM:8083)│ (Встречи+MeetRAG │ (RAG Регламентов    │
    │                  │                 │                  │  :8084)          │  :8086)             │
    └──────────────────┴─────────────────┴──────────────────┴──────────────────┴─────────────────────┘
```

### Воркфлоу `n8n` (`n8n/workflows/`)

| Файл | Назначение |
| :--- | :--- |
| `01_main_router_and_auth.json` | Главный маршрутизатор чата, авторизация в `EmployeeReader`, вызов агента |
| `02_rbac_rag_pipeline.json` | Индексация и поиск регламентов с учетом групп доступа |
| `03_pre_meeting_prep_cron.json` | Автоподготовка брифа перед предстоящей планеркой по расписанию |
| `04_live_meeting_copilot.json` | Живые заметки и подсказки в ходе совещания |
| `05_post_meeting_mom_and_tasks.json` | Пост-обработка заметок встречи: генерация MoM и черновиков поручений |
| `06_executive_summary.json` | Управленческая сводка для руководства |
| `07_google_meet_post_processing.json` | Окончание Google Meet (webhook/cron) → Whisper STT → LLM саммари → БД + `gemini-embedding-2` RAG |
| `08_meeting_recall_rag_and_export.json` | Вопрос по созвону → `MasterId` + `gemini-embedding-2` RAG (участники + Директор) → саммари или полный файл DOCX/PDF |

---

## 🔄 Диаграмма последовательности (Sequence Diagram)

Ниже представлена полная последовательность обработки запроса пользователя (от входа через Nginx до вызова оркестратора `n8n`, автономного выбора MCP-инструментов и векторного поиска `gemini-embedding-2`, работы с `LlmLoadBalancer` на 40 RPM и записи телеметрии в `Langfuse v3`):

```mermaid
sequenceDiagram
    autonumber
    actor User as Сотрудник (Web UI / Telegram)
    participant Nginx as Nginx Ingress (:80)<br/>[burst=200 / Anti-Scan]
    participant GW as Fotoprint.AiGateway.Api<br/>[ContextSwitch + Agent]
    participant Emp as MCP EmployeeReader (:8081)<br/>[RBAC Паспорт]
    participant DB as PostgreSQL 16 + pgvector<br/>[Сессии & База знаний 1536d]
    participant N8N as n8n Orchestrator (:5678)<br/>[01_main_router_and_auth]
    participant LB as LlmLoadBalancer & NvidiaClient<br/>[PriorityQueue / 40 RPM / 3 Retries]
    participant MCP as MCP Сервисы (:8082..8084, :8086)<br/>[1C / Bitrix24 / Calendar+MeetRAG / KnowledgeBase]
    participant LF as Langfuse v3 + ClickHouse<br/>[Traces / Spans / Costs / Scores]

    User->>Nginx: POST /api/agent/chat (или /api/telegram/webhook)
    Nginx->>Nginx: Проверка Anti-Scanner & Rate Limit (burst=150..200)
    Nginx->>GW: Проксирование запроса во внутреннюю сеть

    Note over GW,DB: Этап 1: Идентификация сотрудника и управление сессией (MasterId -> SessionId)
    GW->>DB: ResolveMasterId + GetOrCreateActiveSessionByMasterIdAsync()
    DB-->>GW: Активная сессия (UUID SessionId) + последние сообщения (до 10 шт.)
    GW->>GW: ContextSwitchDetector.DetectSwitchAsync()<br/>(Гибридный FastPath < 1мс или nemotron-3.5-bolt-e4b)
    alt Контекст сменился (IsSwitched == true)
        GW->>DB: SwitchToNewSessionAsync() (архивация старой, новый UUID SessionId)
    end
    GW->>DB: SaveMessageToSessionAsync(role: "user")
    GW--)LF: CreateTraceAsync(input, tags, sessionId)

    Note over GW,N8N: Этап 2: Маршрутизация и авторизация в оркестраторе n8n
    GW->>N8N: POST /webhook/fotoprint-chat (trace_id, session_id, branch, user_id)
    N8N->>Emp: GET /tools/get_employee_security_context (X-Internal-Service-Key)
    Emp->>DB: Загрузка ролей, разрешений и групп допуска RAG
    DB-->>Emp: EmployeeSecurityContext (Director / Production / Sales / Accounting)
    alt Сотрудник не найден (Guest / Не авторизован)
        Emp-->>N8N: 404 Not Found
        N8N-->>GW: 403 Forbidden ("Доступ запрещен")
        GW--)LF: LogScore(agent_success: 0) + Trace(status:forbidden)
        GW-->>User: 🚫 Доступ запрещен
    else Сотрудник авторизован (200 OK)
        Emp-->>N8N: 200 OK (Паспорт прав RBAC)
        N8N->>GW: POST /api/agent/execute (вызов автономного ReAct-агента)
    end

    Note over GW,MCP: Этап 3: Автономный цикл FotoprintAiAgent (ReAct + 10 сообщений истории + Кэш 45с + HITL)
    GW->>GW: Проверка RBAC Response Cache (TTL 45с по роли/отделу)
    alt Ответ найден в кэше (Cache Hit)
        GW--)LF: LogEvent("Agent:CacheHit", 0 вызовов LLM)
    else Кэш пуст (Cache Miss)
        GW->>DB: Загрузка истории сессии (.TakeLast(10) незамороженных сообщений)
        GW->>LB: Шаг 1 ReAct: ChatTurnAsync(tools, thinking=true, nemotron-3-ultra-550b-a55b)
        LB->>LB: Очередь PriorityQueue + контроль окна 40 RPM
        LB-->>GW: Выбранные tool_calls (напр. get_order_status, create_task)
        GW--)LF: CreateGeneration("Gen:Main_Agent", usageDetails, costDetails)

        alt Мутирующее действие без подтверждения (create_task / schedule_meeting)
            GW-->>User: 📋 План действий + запрос явного подтверждения (HITL)
        else Чтение или подтвержденное действие
            par Параллельный вызов выбранных инструментов (MCP / RAG)
                GW->>MCP: GET/POST /tools/... (X-Internal-Service-Key, X-Telegram-Id)
                MCP->>MCP: SecurityInterceptorMiddleware (проверка прав RBAC)
                MCP-->>GW: Чистый UTF-8 JSON с производственными данными
            and Векторный поиск в базе знаний (search_typography_regulations)
                GW->>DB: GeminiEmbeddingClient (gemini-embedding-2) + SearchSimilarChunksAsync(pgvector + RBAC)
                DB-->>GW: Релевантные чанки документов (с учетом allowed_access_groups)
            end
            GW--)LF: CreateSpan("Span:MCP-*", startTime, endTime, input, output)

            GW->>LB: Шаг 2 ReAct: ChatTurnNoThinkingAsync(toolResults UTF-8, thinking=false, nemotron-3.5-bolt-e4b)
            LB-->>GW: Сформированный структурированный ответ
            GW--)LF: CreateGeneration("Gen:Main_Agent", usageDetails, costDetails)
            GW->>GW: Сохранение read-only ответа в RBAC-кэш на 45 секунд
        end
    end

    N8N-->>GW: 200 OK (answer, tools_invoked, total_tokens, branch)
    GW->>DB: SaveMessageToSessionAsync(role: "assistant")
    GW--)LF: CreateSpan("Span:N8n_Orchestrator") + Upsert Trace(output, status:ok)
    GW--)LF: LogScore(agent_success, tools_invoked_count, context_switched, response_latency_sec)
    GW-->>Nginx: 200 OK (JSON / Telegram Async Reply)
    Nginx-->>User: Готовый ответ ассистента
```

---

## 🚀 Быстрый старт и управление проектом

### Предварительные требования
- **Docker Desktop** (версия 24+) с поддержкой **Docker Compose v2**.
- Выделенная оперативная память для Docker: рекомендуется **не менее 4–6 ГБ RAM** (PostgreSQL 16, ClickHouse, Redis, MinIO, n8n, Langfuse, .NET микросервисы).
- Для локального запуска тестов вне Docker: **.NET 9 SDK**.

### 1. Настройка `.env`
Скопируйте шаблон переменных окружения и укажите:
- `NVIDIA_API_KEY` (ключ NVIDIA NIM для LLM-моделей `nemotron`);
- `GEMINI_API_KEY` и `GEMINI_EMBEDDING_MODEL=gemini-embedding-2` (для векторного поиска RAG и fallback-транскрибации аудио);
- при необходимости токен Telegram-бота (`TELEGRAM_BOT_TOKEN`).

```powershell
# PowerShell (Windows):
Copy-Item .env.example .env
```

```bash
# Bash (Linux / macOS):
cp .env.example .env
```

> 💡 **Поддержка пула API-ключей (Key Rotation):** В переменной `NVIDIA_API_KEY` (или `NVIDIA_API_KEYS`) можно указать несколько ключей через запятую (`nvapi-key1,nvapi-key2,nvapi-key3`). `NvidiaClient` автоматически распределяет запросы между ними по алгоритму `Round-Robin`, кратно увеличивая лимит RPM (`2 ключа = 80 RPM`, `3 ключа = 120 RPM`).

### 2. Запуск всего стека
```bash
docker compose up -d --build
```

Проверка статуса всех контейнеров:
```bash
docker compose ps
```

### 3. Пересборка только AI Gateway после изменений в коде .NET
```bash
docker compose up -d --build --no-deps ai-gateway
docker restart fotoprint-nginx
```

### 4. Остановка проекта
```bash
# Штатная остановка с сохранением всех данных:
docker compose stop

# Остановка и удаление контейнеров (тома БД сохраняются):
docker compose down

# Полный сброс вместе с томами БД (Fresh Install):
docker compose down -v
```

---

## 🌐 Веб-интерфейсы и ключевые точки входа

| Сервис / Эндпоинт | URL | Назначение | Учетные данные (Dev) |
| :--- | :--- | :--- | :--- |
| **Web Copilot UI (через Nginx)** | [http://localhost](http://localhost) (`:80`) | Веб-чат сотрудника с переключением ролей (Директор, Производство, Продажи, Бухгалтерия, Гость), голосовой ввод (STT) и быстрые сценарии | — |
| **Langfuse v3 UI** | [http://localhost:3000](http://localhost:3000) | Дашборд трейсов, стоимости моделей (`$`), задержек (`Spans`) и оценок (`Scores`) | `admin@fotoprint.ru` / `FotoprintAdmin2026!` |
| **n8n Orchestrator** | [http://localhost:5678](http://localhost:5678) | Визуальные 8 воркфлоу маршрутизации, авторизации, RAG и обработки созвонов | Задаются при входе |
| **MinIO Console** | [http://localhost:9091](http://localhost:9091) | Объектное S3-хранилище событий и батчей Langfuse | `minio` / `minio_secret_password` |
| **Telegram Tunnel Status** | [http://localhost/api/telegram/info](http://localhost/api/telegram/info) | Текущий публичный HTTPS URL Cloudflare Quick Tunnel и статус вебхука Telegram | — |
| **Статистика балансировщика и RPM** | [http://localhost/api/system/stats](http://localhost/api/system/stats) | Счетчик вызовов за 60 секунд, остаток квоты `40 RPM`, состояние очереди и роллбэков | — |
| **RAG API (`/api/rag/*`)** | `/api/rag/index-document`, `/api/rag/search`, `/api/rag/reindex-embeddings` | Индексация документов, векторный поиск и переиндексация чанков без эмбеддингов | RBAC: сотрудники / Директор |
| **Meetings & Google Meet API** | `/api/meetings/process-audio`, `/api/meetings/save-and-index`, `/api/meetings/rag-search`, `/api/meetings/{id}/export-full`, `/api/meetings/{id}/export-docx` | Обработка аудио, сохранение и индексация Google Meet, RAG-поиск и экспорт полного файла (DOCX/PDF) | RBAC: участники + Директор |
| **MCP Серверы (`:8081..8084, :8086`)** | *Внутренняя сеть `fotoprint-net`* | Закрыты от внешнего доступа. Принимают запросы только с заголовком `X-Internal-Service-Key` | — |

---

## ⚡ Оптимизация под лимит `40 RPM` и пиковые нагрузки (40+ человек в минуту)

Чтобы корпоративный бот стабильно обслуживал **40–150+ обращений в минуту** без ошибок `HTTP 429 Too Many Requests` и без потерь сообщений, в системе работают 5 механизмов:

1. **Расширенный буфер Nginx (`infra/nginx/nginx.conf`):**
   - Для `/api/telegram/webhook` настроен `rate=50r/s` с буфером всплеска `burst=200 delay=50`.
   - Для `/api/*` настроен `rate=30r/s` с буфером `burst=150 nodelay`.
   - Всплеск из 45+ одновременных запросов проходит в шлюз со 100% успешностью (`200 OK`), не отбрасывая пользователей на периметре.
2. **Асинхронная очередь `LlmLoadBalancer` с приоритетами руководства (`PriorityQueue`):**
   - Ограничитель параллелизма (`max_concurrent_requests: 5`) и минутного окна (`rpm: 40`, `min_interval_between_requests_ms: 250`) установлен **непосредственно перед HTTP-вызовом LLM** в `NvidiaClient`, не блокируя ожидание `n8n` и параллельные запросы к MCP/БД.
   - Если в минуту приходит 41-й и последующие запросы к LLM, они **не падают с ошибкой**, а встают в очередь ожидания до точного момента освобождения соответствующего слота в 60-секундном окне. Запросы руководства (`priority > 0`) обслуживаются первыми.
3. **Гибридный `ContextSwitchDetector` (`0` вызовов LLM в 90%+ случаев):**
   - Классификация доменной ветки (`executive_report`, `task_management`, `meeting_prep`, `meeting_summary`, `doc_search`, `general_chat`) и распознавание фраз-продолжений выполняются детерминированно за `< 1 мс` без расхода квоты RPM. Вызов быстрой LLM (`nvidia/nemotron-3.5-bolt-e4b`) выполняется только для нестандартных фраз.
4. **Краткосрочный RBAC-кэш ответов (`45 секунд`):**
   - Повторные идентичные read-only запросы от сотрудников с теми же правами доступа возвращаются из кэша за **`~200 мс`** и тратят **`0` вызовов LLM** (мутирующие запросы вроде создания задач и встреч не кэшируются).
5. **Чистая UTF-8 передача данных инструментов + гибридный режим `Thinking`:**
   - Результаты всех MCP-сервисов передаются в LLM в чистом UTF-8 без `\u04XX`-экранирования кириллицы, что сокращает объем промпта в 3–4 раза и исключает галлюцинации.
   - На шаге 1 (выбор MCP-инструмента) используется `nvidia/nemotron-3-ultra-550b-a55b` с `enable_thinking: true` (при истории до 10 сообщений), а на шаге 2 (синтез ответа по данным MCP) — быстрая `nvidia/nemotron-3.5-bolt-e4b` с `enable_thinking: false`.

---

## 📊 Расход вызовов LLM на одно сообщение пользователя

| Сценарий | Вызовов LLM | Время отклика | Как обрабатывается |
| :--- | :---: | :---: | :--- |
| **Кнопки в Telegram (`[👍]/[👎]`, `[✅ В Битрикс]`)** | **0** | `~20–80 мс` | Прямая запись оценки в Langfuse или постановка задачи в `mcp-bitrix24` с роллбэком на 3 повтора |
| **Повторный типовой запрос в течение 45 сек (RBAC Cache Hit)** | **0** | `~180–250 мс` | `ContextSwitchDetector` (FastPath, 0 LLM) ➔ ответ из RBAC-кэша `FotoprintAiAgent` |
| **Прямой диалоговый вопрос без вызова MCP (Приветствие / Уточнение)** | **1** | `~2.5–3.5 сек` | `ContextSwitchDetector` (FastPath, 0 LLM) ➔ 1 вызов `Gen:Main_Agent` |
| **Запрос данных из 1С / Битрикс24 / Календаря / RAG** | **2** | `~6.0–8.2 сек` | `ContextSwitchDetector` (FastPath, 0 LLM) ➔ Шаг 1 (выбор MCP/RAG-тулов) ➔ параллельный вызов ➔ Шаг 2 (синтез отчета) |

---

## 📈 Телеметрия и метрики в дашборде Langfuse v3

В системе настроен полный сбор метрик в **Langfuse v3** ([http://localhost:3000](http://localhost:3000)):

1. **Таблица Traces (`default.traces`):**
   - Для каждого запроса в веб-чате (`web-agent-chat`) и Telegram (`telegram-chat-message`) записываются полный вопрос пользователя (`Input`), финальный ответ агента (`Output`), `SessionId`, `UserId`, метаданные (`tools_invoked_count`, `total_tokens`, `latency_sec`, `branch`) и теги статуса (`status:ok`, `status:error`, `status:forbidden`).
2. **Стоимость токенов и FinOps (`Model Costs` / `default.observations`):**
   - Каждая генерация (`Gen:Main_Agent`, `Gen:ContextSwitchDetector`, `Gen:Embedding_RAG`, `Gen:Protocol_And_Executive_Summarizer`, `Gen:Audio_MoM_Protocol`) передает `usageDetails` (`input`, `output`, `total`) и `costDetails` (`input`, `output`, `total` в USD) по тарифам моделей, зарегистрированным в таблицах `models` и `prices` Langfuse.
   - В метаданных `Gen:Main_Agent` фиксируется `history_messages_count` (число сообщений истории сессии, реально переданных в контекст — от `0` до `10`).
3. **Детализация задержек по узлам (`Latency Spans`):**
   - `Span:N8n_Orchestrator`, `Span:Audio_Transcribe`, `Span:MCP-EmployeeReader`, `Span:MCP-get_order_status`, `Span:MCP-get_production_kpis`, `Span:MCP-get_user_tasks`, `Span:MCP-get_crm_deal_info`, `Span:MCP-create_task`, `Span:MCP-get_upcoming_meetings`, `Span:MCP-get_meeting_history`, `Span:MCP-search_meeting_rag`, `Span:MCP-schedule_meeting`, `Span:MCP-search_typography_regulations`.
4. **Операционные и качественные оценки (`Scores` / `default.scores`):**
   - На каждый запрос автоматически записываются 4 детерминированные метрики (не расходующие квоту LLM):
     - `agent_success` (`1` — успех, `0` — сбой или отказ в доступе);
     - `tools_invoked_count` (количество вызванных MCP/RAG инструментов);
     - `context_switched` (`1` — смена темы и ротация `SessionId`, `0` — продолжение сессии);
     - `response_latency_sec` (полное время ответа в секундах для построения перцентилей p50/p95/p99).
   - Также поддерживаются оценки пользователей `user_feedback` (`👍`/`👎`), `task_extraction_accuracy` и фоновые проверки `ILlmJudgeService` (включаются флагом `telemetry.evaluations.enable_llm_as_judge: true` в `config.yaml`).

---

## 🧪 Тестирование

В проекте **110 автоматических тестов (xUnit)**, покрывающих RBAC-безопасность, anti-IDOR, защиту периметра, автономного агента, пакетный вызов инструментов, HITL-подтверждение мутирующих действий, устойчивость при отключении БД/MCP, балансировщик нагрузки, роллбэк на 3 повтора, эмбеддинги `gemini-embedding-2`, генерацию протоколов (DOCX/HTML с полной стенограммой), детектор смены контекста и Telegram/Cloudflare-интеграцию:

```bash
dotnet test
```
