# Реестр и аудит HTTP-ручек (`src/`)

Дата аудита: `2026-11-12` (обновлено после чистки)
Микросервисов: **6** (`Fotoprint.AiGateway.Api` + 5 `Fotoprint.Mcp.*`)

---

## 1. Удалённые ручки (12)

Не вызывались ни в `n8n/workflows/*.json`, ни во фронтенде, ни в `FotoprintAiAgent`, тестов на них не было.

| № | Сервис | Метод и маршрут | Причина |
|---|---|---|---|
| 1 | AiGateway | `POST /api/gemini/generate` | Устаревший прямой вызов `IGeminiClient`, заменён `/api/llm/generate` |
| 2 | AiGateway | `POST /api/context/detect-switch` | Тестовая ручка детектора (вызывается через DI) |
| 3 | AiGateway | `POST /api/telemetry/evaluate-faithfulness` | Тестовая ручка LLM-Judge |
| 4 | AiGateway | `POST /api/telemetry/evaluate-formatting-policy` | Тестовая ручка LLM-Judge |
| 5 | AiGateway | `POST /api/calendar/slots` | Неиспользуемый прокси к `find_free_slots` |
| 6 | AiGateway | `GET /api/telegram/info` | Без авторизации, раскрывал URL Cloudflare-туннеля |
| 7 | Mcp.EmployeeReader | `GET /sse` | Заглушка SSE |
| 8 | Mcp.EmployeeReader | `POST /message` | Заглушка JSON-RPC |
| 9 | Mcp.EmployeeReader | `POST /tools/call` | Дублирующий JSON-RPC роутер |
| 10 | Mcp.OneC | `POST /tools/call` | Дубликат в обход `[RequirePermission]` |
| 11 | Mcp.Bitrix24 | `POST /tools/call` | Дубликат, `create_task` не сохранял в БД |
| 12 | Mcp.Calendar | `POST /tools/call` | Дубликат с неполной реализацией |

Также удалены осиротевшие DTO (`ContextSwitchRequest`, `ContextMessageDto`, `EvaluateFaithfulnessRequest`, `EvaluatePolicyRequest`) и хелпер `ExtractSafeWebhookInfo`.

---

## 2. Изменённые ручки

| Сервис | Маршрут | Изменение |
|---|---|---|
| AiGateway | `GET /api/employees/search` | Закрыта: нужен `X-Internal-Service-Key` или активный сотрудник (`X-Telegram-Id` / `X-Master-Id`), иначе `401`. Роли грузятся одним запросом (`GetRolesByEmployeeIdsAsync`) вместо N+1. `%`/`_` в запросе экранируются. |
| Mcp.Calendar | `POST /tools/schedule_meeting` | Был мок — теперь реально создаёт встречу в БД. Право `calendar:create_meetings` (выдано **всем** ролям), `[RequireConfirmation(true)]`. Организатор = вызывающий (защита от IDOR), участники по ФИО (умный поиск) или `participant_ids`, время без зоны = МСК, запрет встреч в прошлом. |

---

## 3. Актуальный список ручек

### 3.1. `Fotoprint.AiGateway.Api`

| Метод | Маршрут | Защита |
|---|---|---|
| `GET` | `/` | Публичный |
| `GET` | `/health` | Публичный |
| `GET` | `/api/system/stats` | Публичный |
| `POST` | `/api/audio/transcribe` | Лимит размера + санитизация |
| `POST` | `/api/llm/generate` | Закрыт в `nginx.conf` |
| `GET` | `/api/agent/users` | Публичный (демо-переключатель ролей) |
| `GET` | `/api/employees/search` | Внутренний ключ или активный сотрудник |
| `POST` | `/api/agent/execute` | Внутренний ключ или активный сотрудник |
| `POST` | `/api/agent/chat` | RBAC через n8n |
| `POST` | `/api/chat/ingress` | `InternalServiceAuthMiddleware` |
| `POST` | `/api/rag/index-document` | Ключ или `rag:search_all` |
| `POST` | `/api/rag/reindex-embeddings` | Ключ или `rag:search_all` |
| `POST` | `/api/rag/search` | Ключ или MasterId/TelegramId |
| `POST` | `/api/meetings/{id}/generate-protocol` | Проверка встречи |
| `GET` | `/api/meetings/{id}/export-docx` | Участники или Директор |
| `POST` | `/api/meetings/process-audio` | Лимит размера |
| `POST` | `/api/meetings/save-and-index` | Закрыт в `nginx.conf` |
| `POST` | `/api/meetings/rag-search` | RBAC по MasterId |
| `GET` | `/api/meetings/{id}/export-full` | RBAC по MasterId |
| `GET` | `/api/calendar/events` | Прокси в mcp-calendar с RBAC |
| `POST` | `/api/calendar/confirm` | Прокси в mcp-calendar, защита от IDOR |
| `POST` | `/api/telegram/webhook` | Секрет вебхука |
| `POST` | `/api/telegram/set-webhook` | Внутренний ключ |
| `POST` | `/api/notifications/send` | Внутренний ключ |
| `POST` | `/api/notifications/dispatch/{morning-digest,pre-meeting,task-deadlines}` | Внутренний ключ |
| `GET`/`PUT` | `/api/notifications/preferences/{employeeId}` | Внутренний ключ |

### 3.2. MCP-сервисы (все за `InternalServiceAuthMiddleware`)

| Сервис | Маршруты |
|---|---|
| EmployeeReader `8081` | `/health`, `/tools/list`, `/tools/get_employee_security_context`, `/tools/resolve_master_id` |
| OneC `8082` | `/health`, `/tools/list`, `/tools/get_order_status`, `/tools/get_production_kpis` |
| Bitrix24 `8083` | `/health`, `/tools/list`, `/tools/get_user_tasks`, `/tools/create_task`, `/tools/get_crm_deal_info` |
| Calendar `8084` | `/health`, `/tools/list`, `/tools/get_upcoming_meetings`, `/tools/get_meeting_history`, `/tools/find_free_slots`, `/tools/create_meeting`, `/tools/get_calendar_events`, `/tools/schedule_meeting`, `/tools/search_meeting_rag` |
| KnowledgeBase `8086` | `/health`, `/tools/list`, `/tools/search_typography_regulations`, `/tools/index_corporate_document` |
