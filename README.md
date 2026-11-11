# 🖨️ Fotoprint AI Copilot

**Fotoprint AI Copilot** — интеллектуальная мультиагентная платформа и корпоративный ассистент типографии. Система автоматизирует подготовку к ежедневным планеркам, протоколирование совещаний (Minutes of Meeting), контроль производственных поручений, семантический поиск по регламентам с разграничением прав доступа (RBAC RAG), а также интеграцию с производственной системой 1С и CRM Битрикс24.

---

## 🏗 Архитектура системы

Платформа построена на микросервисной контейнеризированной архитектуре:

```
                          ┌──────────────────────────┐
                          │   Telegram / Web Client  │
                          └─────────────┬────────────┘
                                        │ (Входной порт 80 / 443)
                                        ▼
    ┌────────────────────────────────────────────────────────────────────────┐
    │          ЕДИНАЯ ТОЧКА ВХОДА: NGINX Ingress Load Balancer               │
    │  • Защита от поиска портов (Anti-Port-Scanning & Scanner-Bot 444 Drop) │
    │  • Honeypot-ловушки (блокировка зондирования /.env, wp-, phpmyadmin)   │
    │  • Сетевой rate limiting (limit_req burst=40)                          │
    └───────────────────────────────────┬────────────────────────────────────┘
                                        │ (Внутренняя сеть fotoprint-net)
                                        ▼
    ┌────────────────────────────────────────────────────────────────────────┐
    │                       Fotoprint.AiGateway.Api                          │
    │                   (Внутренний шлюз агента, 8080)                       │
    └──────────────┬────────────────────┬────────────────────┬───────────────┘
                   │                    │                    │
                   ▼                    ▼                    ▼
        ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
        │   n8n Workflows  │  │   PostgreSQL 16  │  │   Langfuse v3    │
        │   (Оркестрация)  │  │   + pgvector     │  │  (Телеметрия &   │
        │    Порт 5678     │  │   127.0.0.1:5432 │  │     LLM Traces)  │
        └─────────┬────────┘  └──────────────────┘  │    Порт 3000     │
                  │                                 └──────────────────┘
                  ▼
   ┌─────────────────────────────────────────────────────────────────────────┐
   │            ИЗОЛИРОВАННЫЙ ВНУТРЕННИЙ ПЕРИМЕТР MCP-СЕРВЕРОВ               │
   │  • Порты закрыты от внешнего мира (НЕТ публичного маппинга портов)     │
   │  • PerimeterDefenseMiddleware: мгновенная отсечка сканеров портов       │
   │  • InternalServiceAuthMiddleware: вызовы разрешены ТОЛЬКО из n8n/Gateway│
   │    через защищенный заголовок X-Internal-Service-Key                    │
   │  • SecurityInterceptor: валидация контекста прав сотрудника (RBAC)      │
   ├───────────────────┬────────────────────┬────────────────────────────────┤
   │ Fotoprint.Mcp.    │ Fotoprint.Mcp.     │ Fotoprint.Mcp.                 │
   │ EmployeeReader    │ OneC               │ Bitrix24 & Calendar            │
   │ (RBAC, :8081)     │ (Заказы/KPI, :8082)│ (CRM/Задачи :8083, План :8084) │
   └───────────────────┴────────────────────┴────────────────────────────────┘
```

### Основные компоненты

1. **`Fotoprint.AiGateway.Api` (.NET 9 / C# 13)** — центральный входной API-шлюз с 3-этапной обработкой:
   - **Этап 1 (Периметр и резолвинг MasterId):** Прием вебхуков Telegram, защита периметра (`PerimeterDefenseMiddleware`), запрос в `Fotoprint.Mcp.EmployeeReader` для получения уникального числового `MasterId` пользователя, сглаживание пиков (`LlmLoadBalancer`) и роллбэк (`RollbackManager`).
   - **Этап 2 (БД диалогов и LLM ContextSwitchDetector):** По `MasterId` находится активный диалог с UUID `SessionId`. В `ContextSwitchDetector` передается история текущей сессии (`GetMessagesBySessionIdAsync`) и новое сообщение. Если Gemini определяет смену контекста (`is_context_switched = true`) — **активный `SessionId` закрывается и генерируется новый UUID `SessionId`**, а старые сообщения замораживаются.
   - **Этап 3 (Оркестрация MCP в n8n):** В n8n передается payload с `master_id`, актуальным `session_id`, флагом `is_context_switched` и веткой `branch`, где вызываются изолированные MCP-микросервисы (1C, Bitrix24, Calendar, RAG).
2. **`Fotoprint.Core`** — общая библиотека ядра:
   - Доменные модели (`Entities`).
   - Репозиторий Dapper + Npgsql (`FotoprintRepository`) с поддержкой векторного поиска через pgvector.
   - Парсер документов DOCX, XLSX, PDF (`DocumentParser`).
   - Генератор протоколов встреч с таймкодами и задачами (`MeetingProtocolGenerator`).
   - RBAC & Security Middleware (`SecurityInterceptorMiddleware`, `EmployeeSecurityContext`).
   - Клиент аудита и трейсинга Langfuse v3 (`LangfuseClient`).
3. **MCP-серверы (Model Context Protocol)**:
   - `Fotoprint.Mcp.EmployeeReader` (порт 8081) — паспорт сотрудника, аутентификация по Telegram ID, роли и права доступа.
   - `Fotoprint.Mcp.OneC` (порт 8082) — данные заказов 1С, статусы тиражей, загрузка печатных цехов, процент брака.
   - `Fotoprint.Mcp.Bitrix24` (порт 8083) — сделки CRM, лиды, постановка задач и контроль исполнителей.
   - `Fotoprint.Mcp.Calendar` (порт 8084) — корпоративный календарь, встречи, повестки планерок.
   - `bitrix24-ready-mcp` (порт 8085) — готовый Docker-коннектор к REST API Битрикс24.
4. **Оркестратор сценариев n8n (порт 5678)** — исполнение 6 ключевых цепочек:
   - `01_main_router_and_auth` — входной роутер и авторизация.
   - `02_rbac_rag_pipeline` — семантический поиск по регламентам с RBAC-фильтром.
   - `03_pre_meeting_prep_cron` — CRON-сбор повестки, заказов и KPI перед утренней планеркой.
   - `04_live_meeting_copilot` — быстрый AI-ассистент во время совещания.
   - `05_post_meeting_mom_and_tasks` — разбор транскрипта, формирование протокола (MoM) и постановка задач.
   - `06_executive_summary` — аналитическая сводка для руководства (C-level).
5. **База данных PostgreSQL 16 + pgvector (порт 5432)** — реляционные данные, документы, векторные эмбеддинги (размерность 1536), журнал доступа.
6. **Стек мониторинга Langfuse v3 (порт 3000)** — ClickHouse, Redis, MinIO и Web UI для аудита LLM-запросов, токенов, стоимости и алертов безопасности.
7. **Манифест зависимостей (`libraries.yaml`) и `library-downloader`** — автоматическая предзагрузка и кэширование NuGet и npm пакетов.

---

## 📋 Предварительные требования

Для запуска необходимы:
- **Docker Desktop** (версия 24+) с поддержкой **Docker Compose v2** (включен WSL2 на Windows или Docker Engine на Linux).
- Выделенная память для Docker: рекомендуется **не менее 4–6 ГБ RAM** (так как запускаются ClickHouse, PostgreSQL, n8n и Langfuse).
- Для локальной разработки и запуска тестов (без Docker): **.NET 9 SDK**.

---

## ⚙️ Настройка окружения

Перед первым запуском создайте файл `.env` в корне проекта на основе образца:

```powershell
# В PowerShell:
Copy-Item .env.example .env
```

```bash
# В Bash / Linux:
cp .env.example .env
```

Отредактируйте `.env` при необходимости (задайте токен вашего Telegram-бота `TELEGRAM_BOT_TOKEN`, API ключ LLM `OPENAI_API_KEY` и секретные ключи).

---

## 🚀 Как запускать проект

### Вариант 1. Запуск всего стека через Docker Compose (Рекомендуемый)

Сборка образов и фоновый запуск всех контейнеров:

```bash
docker compose up -d --build
```

> **Примечание:** При первом запуске сервис `library-downloader` запустит скрипт `bootstrap.sh`, восстановит NuGet-пакеты и установит npm-модули для n8n. База данных PostgreSQL автоматически инициализирует схему и тестовые данные из `infra/postgres/init/`.

#### Проверка статуса запущенных контейнеров:

```bash
docker compose ps
```

#### Просмотр логов в реальном времени:

```bash
# Логи всех сервисов
docker compose logs -f

# Логи конкретного сервиса, например API Gateway или n8n:
docker compose logs -f ai-gateway
docker compose logs -f n8n
docker compose logs -f mcp-1c
```

---

### Вариант 2. Запуск только инфраструктуры (для локальной разработки .NET)

Если вы разрабатываете .NET сервисы локально в IDE (Visual Studio, Rider, VS Code):

1. Запустите инфраструктурные сервисы (БД, Langfuse, n8n):
   ```bash
   docker compose up -d postgres n8n clickhouse langfuse-redis langfuse-minio langfuse-worker langfuse-web
   ```

2. Запустите тесты и убедитесь в работоспособности кода:
   ```bash
   dotnet test
   ```

3. Запустите нужный микросервис локально, например AI Gateway:
   ```bash
   dotnet run --project src/Fotoprint.AiGateway.Api
   ```

---

## 🛑 Как выключать проект

### 1. Обычная штатная остановка контейнеров (данные сохраняются):

```bash
docker compose stop
```

Контейнеры останавливаются, все данные в томах (volumes: `pgdata`, `n8n_data`, `clickhouse_data`, `minio_data`, `nuget_cache`) остаются нетронутыми.

Для повторного запуска после `stop`:
```bash
docker compose start
```

---

### 2. Полная остановка и удаление контейнеров (данные сохраняются):

```bash
docker compose down
```

Останавливает и удаляет контейнеры и общую сеть `fotoprint-net`. Данные в томах БД сохраняются.

---

### 3. Полный сброс с очисткой всех баз данных и томов (Fresh Install):

> ⚠️ **Внимание:** Эта команда удалит все сохраненные данные баз данных, сессии n8n и трейсы Langfuse!

```bash
docker compose down -v
```

После этой команды следующий `docker compose up -d --build` поднимет чистую систему с повторным выполнением `infra/postgres/init/01_init_databases.sql`, `02_schema.sql` и `03_seed_data.sql`.

---

### 4. Перезапуск конкретного сервиса:

Если вы изменили код одного из микросервисов:

```bash
# Пересобрать и перезапустить только AI Gateway:
docker compose up -d --build ai-gateway

# Перезапустить n8n:
docker compose restart n8n
```

---

## 🌐 Доступ к веб-интерфейсам и API

После успешного старта доступны следующие адреса:

| Сервис | URL | Назначение | Учетные данные по умолчанию |
|---|---|---|---|
| **Nginx (Единая точка входа)** | [http://localhost](http://localhost) (или `:80`) | Защищенный входной шлюз, anti-scanner, rate limiting | — |
| **n8n Orchestrator** | [http://localhost:5678](http://localhost:5678) | Визуальные воркфлоу, AI-агенты, RAG | Задаются при первом входе |
| **Langfuse UI** | [http://localhost:3000](http://localhost:3000) | Трейсинг LLM, аудит безопасности, метрики | `admin@fotoprint.ru` / `FotoprintAdmin2026!` |
| **MinIO Console** | [http://localhost:9091](http://localhost:9091) | Хранилище объектов для Langfuse | `minio` / `minio_secret_password` |
| **Fotoprint AI Gateway** | `127.0.0.1:8080` (или через Nginx) | Основной REST API (привязан к localhost) | — |
| **MCP Серверы (1C, Bitrix, Calendar, EmployeeReader)** | *Внутренняя сеть Docker (порты 8081-8085 закрыты)* | Изолированный периметр. Доступен ТОЛЬКО n8n и Gateway через `X-Internal-Service-Key` | — |
| **PostgreSQL 16** | `127.0.0.1:5432` | БД `fotoprint`, `n8n`, `langfuse` (только localhost) | `fotoprint` / `fotoprint_secret` |

---

## 🚦 Балансировщик нагрузки и Роллбэк-система (3 повтора)

В проекте реализована двухуровневая система защиты от перегрузок и отказоустойчивости:

```
[ Telegram / Пользователи ]
           │
           ▼
┌────────────────────────────────────────────────────────┐
│  УРОВЕНЬ 1: Внешний балансировщик NGINX (Reverse Proxy) │
│  • Порт 80 (infra/nginx/nginx.conf)                    │
│  • Сетевой rate limiting (limit_req с буфером burst=40)│
│  • Защита от сетевого флуда и равномерное проксирование│
└──────────────────────────┬─────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────┐
│  УРОВЕНЬ 2: Внутренний Application-балансировщик       │
│  • Класс LlmLoadBalancer (Token Bucket + Concurrency)  │
│  • Сглаживание пиков сообщений (задержка между вызовами│
│    к LLM, защита от HTTP 429 Too Many Requests)       │
│  • Очередь задач без потери входящих вебхуков          │
└──────────────────────────┬─────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────┐
│  СИСТЕМА РОЛЛБЭКА НА 3 ПОВТОРА (RollbackManager)       │
│  • 3 повтора с экспоненциальным backoff при сбое узла  │
│  • Автоматический откат (Compensating action) при крахе│
│    (возврат статуса задачи, фиксация инцидента в БД)   │
│  • Аудит и алерты в Langfuse (Node:RollbackTriggered)  │
└────────────────────────────────────────────────────────┘
```

### Проверка метрик балансировщика и роллбэка

Состояние очередей, активных запросов и откатов доступно в реальном времени:

```bash
curl http://localhost:8080/api/system/stats
```

Пример ответа:
```json
{
  "service": "Fotoprint.AiGateway.Api",
  "load_balancer": {
    "totalProcessed": 42,
    "totalThrottled": 5,
    "queueLength": 0,
    "activeExecutions": 1,
    "currentRpmEstimate": 14
  },
  "rollback_system": {
    "totalExecutions": 42,
    "totalRecoveredOnRetry": 3,
    "totalFailedAndRolledBack": 0
  },
  "timestamp": "2026-11-09T19:20:00Z"
}
```

---

## 🧪 Запуск автоматических тестов

В репозитории подготовлены модульные и интеграционные тесты для проверки контекста безопасности (`SecurityInterceptor`), парсинга документов и генератора протоколов:

```bash
dotnet test
```

Для вывода подробного лога тестирования:
```bash
dotnet test --logger "console;verbosity=detailed"
```

---

## 📦 Управление зависимостями (`libraries.yaml`)

Все внешние пакеты и библиотеки зафиксированы в едином манифесте [libraries.yaml](file:///c:/Users/jotry/Photoprint/libraries.yaml):
- Пакеты .NET 9 (Npgsql, Pgvector, Dapper, OpenXml, PdfPig, YamlDotNet).
- npm-модули для n8n (axios, lodash, form-data, pdf-parse, docx).
- MCP коннекторы и Python-библиотеки.

При добавлении новых пакетов обязательно синхронизируйте манифест `libraries.yaml`.

---

## ⚙️ Единый конфигурационный файл (`config.yaml`)

Все динамические параметры системы вынесены в [config.yaml](file:///c:/Users/jotry/Photoprint/config.yaml) (Single Source of Truth), а [FotoprintConfig.cs](file:///c:/Users/jotry/Photoprint/src/Fotoprint.Core/Configuration/FotoprintConfig.cs) служит строго типизированным загрузчиком для C#:
- **LLM & Proxy**: выбор провайдера (`gemini`, `openai`, `proxy`), API ключ Google AI Studio (`GEMINI_API_KEY`), модель (`gemini-1.5-flash` / `gemini-3.5`).
- **Ограничения Rate Limits**: `rpm` (по умолчанию 60 RPM, гибко настраивается под тариф), `tpm` (1000000), `max_concurrent_requests` (5).
- **Балансировщик**: интервал сглаживания `min_interval_between_requests_ms` (250мс), размер очереди `queue_capacity` (1000).
- **Детекция смены контекста**: `context_switch` (`enabled: true`, `max_history_messages: 5`, `temperature: 0.0`, `auto_freeze_on_switch: true`).
- **Отказоустойчивость**: `max_retries` (3 повтора), `initial_delay_ms`, параметры Circuit Breaker.
- **RAG & Векторный поиск**: `embedding_dimensions` (1536), `top_k` (5), `chunk_size` (800), `chunk_overlap` (150).
- **Планерки и протоколы**: `default_deadline_days` (3 дня), `pre_meeting_window_minutes` (30 мин).

---

## 🤖 Интеграция с Google Gemini (AI Studio) и контроль Rate Limits

В системе реализован клиент [GeminiClient.cs](file:///c:/Users/jotry/Photoprint/src/Fotoprint.Core/Llm/GeminiClient.cs), работающий по спецификации Google AI Studio:
- **Аутентификация**: переменная `GEMINI_API_KEY` в файле `.env`, передаваемая в HTTP-заголовке `x-goog-api-key`.
- **Эндпоинты генерации и чата**:
  - `POST /api/gemini/generate` — прямой вызов Gemini.
  - `POST /api/context/detect-switch` — вызов детектора смены контекста (2-й этап).
  - `POST /api/agent/chat` — диалог с ReAct-агентом (автономный Tool Calling к 1С, CRM, Календарю).
- **Трекинг скользящего окна RPM**: учет вызовов за 60 секунд (`CallsInLast60Seconds`, `RemainingQuotaInCurrentMinute`).
- При пиковой нагрузке балансировщик ставит последующие запросы на микропаузу до освобождения слота, исключая ошибку `429 Too Many Requests`.

---

## 📊 Статистика вызовов LLM на один цикл «вопрос ➔ ответ»

| Режим | Количество вызовов к LLM | Сценарий |
| :--- | :---: | :--- |
| **Минимум (Min)** | **0 – 1** | • **0:** Кнопки подтверждения `[👍]/[👎]`, `[✅ В Битрикс]` (чистый SQL/REST).<br>• **1:** Первое сообщение в сессии или общий вопрос без вызова MCP. |
| **Среднее (Average)** | **2** | Стандартный диалог с отчетом или запросом в 1С/CRM (~80% случаев):<br>1. `ContextSwitchDetector` (проверка смены темы).<br>2. Генерация понятного отчета на основе данных из MCP. |
| **Максимум (Max)** | **3 – 4** | Составной аналитический запрос со сменой диалога и вызовом нескольких MCP (детекция ➔ вызов MCP 1C ➔ вызов RAG ➔ сводный отчет). |

---

## 📈 Телеметрия Langfuse v3, FinOps и LLM-as-a-Judge (Эпик 6)

Система интегрирована с **Langfuse v3** для обеспечения полной наблюдаемости (Observability), учета расходов (FinOps) и контроля качества ответов в реальном времени:

### 1. Сквозной распределенный трейсинг (Distributed Tracing)
- **Иерархия связывания**:
  `SessionId` (ветка контекста диалога) $\rightarrow$ `TraceId` (конкретное сообщение / вебхук) $\rightarrow$ `Span / Generation` (вызовы MCP и LLM).
- **Сквозные заголовки**: `X-Trace-Id`, `X-Session-Id`, `X-Master-Id`, `X-Telegram-Id`, `X-Internal-Service-Key` передаются между .NET Gateway, n8n и MCP.
- **Обогащение трейса**: Каждый трейс маркируется тегами сотрудника: `dept:<department>`, `role:<roles>`, `branch:<branch>`, `context_switched:true/false`.

### 2. FinOps: Раздельный учет токенов по 4 ключевым текстовым узлам
Учет Input / Output / Total и Cached Tokens с расчетом стоимости в долларах США по тарифам из `config.yaml`:
1. `Gen:ContextSwitchDetector` — классификация смены контекста и ветки диалога.
2. `Gen:Embedding_RAG` — векторизация поисковых запросов и фрагментов регламентов.
3. `Gen:Main_Agent` — автономный ReAct агент, выбор инструментов (Tool Calling) и формулирование ответа.
4. `Gen:Protocol_And_Executive_Summarizer` — суммаризация стенограмм, генерация MoM и управленческих отчетов.

### 3. Технические и Security-метрики (Latency & Alerts)
- **Замеры задержек (Latency Spans)**: `Span:MCP-EmployeeReader`, `Span:MCP-1C`, `Span:MCP-Bitrix24`, `Span:MCP-Calendar`, `Span:PgVector_Search`.
- **Логирование нарушений безопасности**: При попытке обращения к данным без прав (`403 Forbidden`) фиксируется событие `Event:Security_Violation`.

### 4. Оценка качества (Evaluations & LLM-as-a-Judge)
- **Пользовательский скоринг**:
  - `user_feedback`: сбор реакций `[👍]` (score 1.0) и `[👎]` (score 0.0) под ответами.
  - `task_extraction_accuracy`: 1.0 при клике `[✅ В Битрикс]` без ручных корректировок.
- **Автоматические фоновые проверки (LLM Judges)**:
  - **Faithfulness**: сверка фактов и числовых значений из ответа бота с сырыми данными MCP 1C и чанками RAG (проверка отсутствия галлюцинаций).
  - **Context Switch Accuracy**: валидация решения детектора контекста (склеил или разделил сессии корректно).
  - **Formatting & Policy Check**: проверка соответствия корпоративному шаблону и отсутствия утечки системных промптов, токенов и ключей.

---

## 🎙️ Модуль локальной транскрибации речи (SOLID Audio Architecture v2.0) и автопротоколирования (MoM)

Для распознавания голосовых сообщений сотрудников и аудиозаписей планерок реализован гибридный отказоустойчивый модуль `Fotoprint.Core.Audio` по принципам **SOLID**, объединяющий встроенные в репозиторий C++ бинарники инференса (Apple Silicon Metal GPU / Windows Vulkan GPU / CPU), внешнюю базу предметного словаря и облачный мультимодальный резерв.

### 🎙️ Настройка модуля распознавания речи

1. **Бинарники движка (встроены в репозиторий)**:
   - В папке `tools/engine/` уже находятся скомпилированные C++ ядра распознавания (`tsukiko-recognizer` для macOS Metal GPU, `tsukiko-recognizer-vulkan.exe` и `tsukiko-recognizer-cpu.exe` для Windows, а также конвертер `ffmpeg.exe`).
   - Установка сторонних GUI-приложений или Flutter-оболочек **НЕ ТРЕБУЕТСЯ**.

2. **Загрузка весов модели нейросети (только .bin веса Whisper)**:
   - macOS / Linux: `./scripts/download-model.sh` (проверяет наличие `models/ggml-large-v3-turbo.bin` локально и в системном кэше; если модель уже есть — моментально завершается без скачивания).
   - Windows: `powershell -File scripts/download-model.ps1`
   - Для скачивания облегченной модели используйте флаг `--light` / `-LightModel` (`ggml-base.bin` ~142 МБ).

3. **Облачный резерв Zero-Install (Gemini Cloud Audio API)**:
   - Если модель или локальный бинарник отсутствуют, система прозрачно переключается на облачный `gemini-1.5-flash` с передачей того же специализированного словаря. Достаточно указать `GEMINI_API_KEY` в файле `.env`.

### 1. Архитектура компонентов (SOLID v2.0)
```
                           [ Входящее аудио ]
                                   │
                                   ▼
                       [ CompositeAudioTranscriber ]
                                   │
                    ┌──────────────┴──────────────┐
                    ▼ (Приоритет: Офлайн)          ▼ (Резерв: Облако)
       [ LocalEngineAudioTranscriber ]     [ CloudGeminiAudioTranscriber ]
                    │                                     │
                    ▼                                     ▼
        [ IAudioEngineResolver ]              [ IVocabularyProvider ]
        (Metal на Mac / Vulkan на Win)        (Читает словарь из config/vocabulary.yaml)
                    │                                     │
                    └──────────────┬──────────────────────┘
                                   │
                                   ▼
                       [ IVocabularyPostProcessor ]
                       (Автозамена омофонов по правилам из конфига)
                                   │
                                   ▼
                            [ Чистый текст ]
```

- **`IVocabularyProvider` (`YamlVocabularyProvider`)**: Читает термины предметной области и правила автозамены омофонов из `config/vocabulary.yaml`. Позволяет адаптировать систему под любое другое предприятие (банк, клинику, завод) без правки исходного кода C#.
- **`IVocabularyPostProcessor` (`VocabularyPostProcessor`)**: Автоматически исправляет искаженные нейросетью отраслевые термины («один с» $\rightarrow$ «1С», «бетрикс» $\rightarrow$ «Битрикс24», «гейдельберг» $\rightarrow$ «Heidelberg»).
- **`IAudioEngineResolver` (`AudioEngineResolver`)**: Автоматически определяет операционную систему, находит подходящий C++ бинарник (`local-metal` / `local-vulkan` / `local-cpu`), аудиоконвертер (`afconvert` / `ffmpeg.exe`) и путь к модели.
- **`LocalEngineAudioTranscriber`**: Выполняет локальный инференс через прямой запуск C++ бинарника с передачей глоссария в аргументе `--prompt`, конвертацией в 16 кГц PCM WAV и возвратом посегментных таймкодов.
- **`CloudGeminiAudioTranscriber`**: Мультимодальный резервный транскрибатор на базе Gemini 1.5 Flash с передачей глоссария в `system_instruction`.
- **`CompositeAudioTranscriber`**: Фасад с автопереключением (Primary Local Engine $\rightarrow$ Fallback Cloud Gemini).

### 2. Внешний файл словаря (`config/vocabulary.yaml`)
```yaml
version: "1.0"
domain: "typography_fotoprint"

prompt_terms:
  - "Фотопринт"
  - "Битрикс24"
  - "1С:Управление нашей типографией"
  - "1С"
  - "Heidelberg Speedmaster XL 75"
  - "Heidelberg"
  - "приладка"
  - "выработка"
  - "флексомашина"
  - "разнотон"
  - "постпресс"
  - "лакирование"
  - "мелованная бумага"

replacements:
  "один эс": "1С"
  "один с": "1С"
  "битрикс 24": "Битрикс24"
  "бетрикс": "Битрикс24"
  "фитрикс": "Битрикс24"
  "китрекс": "Битрикс24"
  "гейдельберг": "Heidelberg"
  "хайдельберг": "Heidelberg"
  "фото принт": "Фотопринт"
```

### 3. Конфигурация в `config.yaml`
```yaml
audio:
  enabled: true
  provider: "auto" # "auto" | "local" | "gemini"
  vocabulary_path: "config/vocabulary.yaml"
  engine:
    timeout_seconds: 300
  gemini:
    model: "gemini-1.5-flash"
  max_file_size_mb: 100
```

### 3. Поддерживаемые аудио- и видеоформаты
`.ogg`, `.oga`, `.opus`, `.m4a`, `.mp3`, `.wav`, `.aac`, `.flac`, `.mp4`, `.mov`.

### 4. Примеры вызова API (`curl`)

#### 4.1 Прямая транскрибация аудиофайла (`POST /api/audio/transcribe`)
```bash
curl -X POST "http://localhost:8080/api/audio/transcribe?language=ru" \
  -F "file=@/path/to/voice_message.m4a"
```

Пример ответа:
```json
{
  "success": true,
  "text": "Коллеги, начинаем планерку офсетного цеха. Кузнецову Ивану подготовить отчет по валам к пятнице.",
  "segments": [
    {
      "startSeconds": 0.0,
      "endSeconds": 4.2,
      "text": "Коллеги, начинаем планерку офсетного цеха.",
      "speaker": null
    },
    {
      "startSeconds": 4.2,
      "endSeconds": 9.8,
      "text": "Кузнецову Ивану подготовить отчет по валам к пятнице.",
      "speaker": null
    }
  ],
  "duration_ms": 840,
  "provider": "local-metal",
  "error": null
}
```

#### 4.2 End-to-End обработка аудиозаписи совещания с генерацией протокола `.docx` и поручений (`POST /api/meetings/process-audio`)
Эндпоинт выполняет полный цикл **Audio $\rightarrow$ Transcript $\rightarrow$ MoM Analysis (`MeetingTranscriptAnalyzer`) $\rightarrow$ PostgreSQL $\rightarrow$ DOCX (`MeetingProtocolGenerator`) $\rightarrow$ Action Items + Telegram Inline Keyboard**:
- Соблюдается строгий инвариант **No Hallucinations**: если в стенограмме совещания не озвучен ответственный или дедлайн по задаче, система автоматически проставляет статус `"требует уточнения"`.

```bash
curl -X POST "http://localhost:8080/api/meetings/process-audio?language=ru" \
  -F "file=@/path/to/meeting_recording.m4a" \
  -F "title=Оперативка производства по тиражу №44102" \
  -F "organizer_id=1" \
  -F "chat_id=100001"
```

#### 4.3 Обработка голосовых сообщений в Telegram (`POST /api/telegram/webhook`)
Шлюз автоматически перехватывает объекты `message.voice` и `message.audio` из Telegram, скачивает голосовой файл через Telegram Bot API (или читает локальный файл при тестировании), расшифровывает речь через `IAudioTranscriber` и передает распознанный текст в стандартный пайплайн `MasterId` $\rightarrow$ `SessionId` $\rightarrow$ `ContextSwitchDetector` $\rightarrow$ `FotoprintAiAgent` / `n8n`:

```bash
curl -X POST "http://localhost:8080/api/telegram/webhook" \
  -H "Content-Type: application/json" \
  -d '{
    "update_id": 900101,
    "message": {
      "message_id": 55,
      "from": { "id": 100001, "username": "smirnov_ceo" },
      "chat": { "id": 100001, "type": "private" },
      "voice": {
        "file_id": "AwACAgIAAxkBAAIB...",
        "duration": 6,
        "mime_type": "audio/ogg",
        "local_file_path": "/path/to/sample_voice.m4a"
      }
    }
  }'
```



