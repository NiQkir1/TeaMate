# Контракт управленческих дашбордов

## GET `/api/system/stats`

Фронтенд передаёт выбранный экран и фильтры:

```http
GET /api/system/stats?dashboard=pulse&period=30&dateFrom=2026-10-13&dateTo=2026-11-11&department=all&product=all
X-Telegram-Id: 100001
```

Идентичность и права определяются сервером по `X-Telegram-Id` / существующему RBAC. Роль не берётся из тела запроса.

```json
{
  "service": "Fotoprint.AiGateway.Api",
  "employee": {"masterId": 100001, "telegramId": 100001, "name": "Алексей Смирнов", "role": "Director", "roleLabel": "Генеральный директор", "department": "Руководство", "email": "a.smirnov@fotoprint.by", "allowedDashboards": ["pulse", "sales", "production", "finance", "decisions"]},
  "dashboard": "pulse",
  "allowedDashboards": ["pulse", "sales", "production", "finance", "decisions"],
  "filters": {"period": 30, "dateFrom": "2026-10-13", "dateTo": "2026-11-11", "department": "all", "product": "all"},
  "timezone": "Europe/Minsk",
  "currency": "BYN",
  "title": "Пульс компании",
  "subtitle": "...",
  "chartTitle": "...",
  "kpis": [{"id":"turnover","name":"Оборот","base":1284600,"plan":1209000,"unit":"BYN","delta":"+6,2%","status":"good","source":"1С / ERP","formula":"...","updatedAt":"2026-11-11T09:42:00+03:00"}],
  "dailyPlans": [{"id":"dp_rev","title":"Выручка за смену","time":"на 14:30","current":42800,"plan":48500,"unit":"BYN","periodCurrent":"1,28 млн","periodPlan":"1,21 млн BYN","color":"#DD5757"}],
  "series": [68, 74, 71, 83, 89, 94, 102],
  "forecastSeries": [102, 108, 115],
  "trend": [{"date":"2026-11-11","fact":102,"plan":97.9,"forecast":102}],
  "systems": [{"id":"crm","name":"CRM / Битрикс24","dept":"sales","detail":"...","freshness":"2 мин назад","status":"ok","metric":"..."}],
  "alerts": [{"id":"alert_defect","title":"Брак / перепечатка: 8.1 %","reason":"Отклонение +3,1 п.п. от плана 5 % (ERP)","metricId":"defect","department":"Производство"}],
  "decisions": [{"id":"DEC-104","problem":"...","impact":"...","cause":"...","action":"...","owner":"...","dueDate":"2026-11-14","priority":"Высокий","status":"В работе","beforeKpi":"...","afterKpi":"...","department":"production","createdAt":"2026-11-11T09:15:00+03:00"}],
  "auditLog": [{"id":"AUD-03","timestamp":"2026-11-11T09:15:00+03:00","actor":"...","action":"...","details":"...","status":"OK"}],
  "quality": {"missing":0,"duplicates":0,"freshness":2,"financeReconciliation":"warning"},
  "forecast": [{"week":"47 неделя (25 ноя – 1 дек)","demand":"5 320 ч","capacity":"4 680 ч","balance":"−640 ч дефицит","deficit":true,"action":"..."}],
  "llm_config": {"provider":"...","chat_model":"...","fast_model":"...","temperature":0.2,"max_tokens":4096,"rpm_limit":40,"tpm_limit":100000,"proxy_enabled":false},
  "gemini_rpm_tracker": {},
  "load_balancer": {},
  "rollback_system": {},
  "updatedAt":"2026-11-11T09:42:00+03:00"
}
```

### Ошибки

- `401` - пользователь не авторизован;
- `403` - экран или данные недоступны для роли;
- `422` - неверные фильтры;
- `503` - источник временно недоступен.

Ответ ошибки: `{ "error": "Код ошибки", "message": "Понятное сообщение", "traceId": "..." }`.

### POST `/api/decisions`

Создание только после подтверждения пользователем. Заголовок `Idempotency-Key` обязателен. Повтор того же ключа не создаёт вторую карточку. Все изменения пишутся в audit log.

### Экраны

`pulse` - директорский агрегат; `sales` - CRM/клиент; `production` - ERP/качество; `finance` - 1С/люди; `decisions` - реестр решений. Роль и список доступных экранов возвращает сервер.
