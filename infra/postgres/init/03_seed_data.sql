-- 03_seed_data.sql
-- Тестовые и демонстрационные данные для Fotoprint AI Copilot

\c fotoprint;

-- ============================================================================
-- 1. РОЛИ И ПРАВА ДОСТУПА (RBAC)
-- ============================================================================

INSERT INTO roles (id, name, description) VALUES
(1, 'Director', 'Генеральный директор и топ-менеджмент (C-Level). Полный доступ ко всем модулям и отчетам.'),
(2, 'Production', 'Производственный департамент (печать, постпресс, склад материалов).'),
(3, 'Sales', 'Отдел продаж и работы с корпоративными клиентами.'),
(4, 'Accounting', 'Бухгалтерия и финансовый отдел.'),
(5, 'Employee', 'Базовая роль рядового сотрудника.')
ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description;

INSERT INTO permissions (id, code, description, category) VALUES
(1, 'onec:read_orders', 'Просмотр статуса заказов в 1С', '1C'),
(2, 'onec:read_production_kpis', 'Просмотр KPI цехов, объемов брака и загрузки оборудования в 1С', '1C'),
(3, 'bitrix24:read_tasks', 'Просмотр задач и поручений сотрудников в Битрикс24', 'Bitrix24'),
(4, 'bitrix24:create_tasks', 'Создание задач и поручений в Битрикс24', 'Bitrix24'),
(5, 'calendar:read_meetings', 'Просмотр расписания и истории встреч', 'Calendar'),
(6, 'rag:search_all', 'Доступ ко всей корпоративной базе знаний без ограничений', 'RAG'),
(7, 'rag:search_department', 'Доступ к документам своего отдела и общим регламентам', 'RAG'),
(8, 'executive:view_summary', 'Формирование и просмотр управленческого отчета Executive Summary', 'Analytics'),
(9, 'calendar:create_meetings', 'Заведение встреч в корпоративном календаре (доступно всем сотрудникам)', 'Calendar'),
(10, 'support:escalate', 'Эскалация диалога на дежурного оператора поддержки', 'Support')
ON CONFLICT (id) DO UPDATE SET code = EXCLUDED.code, description = EXCLUDED.description;

-- Привязка прав к ролям
-- Director (Все права)
INSERT INTO role_permissions (role_id, permission_id) VALUES
(1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 7), (1, 8),
-- Production (1С заказы и KPI цеха, Битрикс задачи, Календарь, документы отдела)
(2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (2, 7),
-- Sales (1С заказы, Битрикс задачи, Календарь, документы отдела)
(3, 1), (3, 3), (3, 4), (3, 5), (3, 7),
-- Accounting (1С заказы, Битрикс задачи, Календарь, документы отдела)
(4, 1), (4, 3), (4, 5), (4, 7),
-- Employee (Базовые задачи и календарь)
(5, 3), (5, 5), (5, 7),
-- Заведение встреч доступно всем ролям
(1, 9), (2, 9), (3, 9), (4, 9), (5, 9),
-- Эскалация на оператора доступна всем сотрудникам
(1, 10), (2, 10), (3, 10), (4, 10), (5, 10)
ON CONFLICT DO NOTHING;

-- Сброс последовательностей
SELECT setval('roles_id_seq', (SELECT MAX(id) FROM roles));
SELECT setval('permissions_id_seq', (SELECT MAX(id) FROM permissions));

-- ============================================================================
-- 2. СОТРУДНИКИ И ИХ ТЕЛЕГРАМ-ПРОФИЛИ
-- ============================================================================

INSERT INTO employees (id, master_id, first_name, last_name, middle_name, email, telegram_id, telegram_username, department, position, access_groups) VALUES
(1, 100001, 'Алексей', 'Смирнов', 'Сергеевич', 'a.smirnov@fotoprint.ru', 2135203645, 'ceo_fotoprint', 'Руководство', 'Генеральный директор', '["all", "c-level", "production", "sales", "accounting"]'::jsonb),
(2, 100002, 'Иван', 'Кузнецов', 'Петрович', 'i.kuznetsov@fotoprint.ru', 100002, 'prod_head', 'Производство', 'Начальник производства', '["all", "production"]'::jsonb),
(3, 100003, 'Елена', 'Морозова', 'Алексеевна', 'e.morozova@fotoprint.ru', 100003, 'sales_lead', 'Продажи', 'Руководитель отдела продаж', '["all", "sales"]'::jsonb),
(4, 100004, 'Ольга', 'Соколова', 'Викторовна', 'o.sokolova@fotoprint.ru', 100004, 'acc_chief', 'Бухгалтерия', 'Главный бухгалтер', '["all", "accounting"]'::jsonb)
ON CONFLICT (id) DO UPDATE SET 
    master_id = EXCLUDED.master_id,
    first_name = EXCLUDED.first_name,
    last_name = EXCLUDED.last_name,
    telegram_id = EXCLUDED.telegram_id,
    access_groups = EXCLUDED.access_groups;

INSERT INTO employee_roles (employee_id, role_id) VALUES
(1, 1), -- Алексей -> Director
(2, 2), -- Иван -> Production
(3, 3), -- Елена -> Sales
(4, 4)  -- Ольга -> Accounting
ON CONFLICT DO NOTHING;

SELECT setval('employees_id_seq', (SELECT MAX(id) FROM employees));

-- ============================================================================
-- 3. КОРПОРАТИВНЫЕ ДОКУМЕНТЫ ДЛЯ RAG И ПОИСКА ПО ЗНАНИЯМ
-- ============================================================================

INSERT INTO corporate_documents (id, title, source_type, file_path, department, allowed_access_groups, metadata) VALUES
('11111111-1111-1111-1111-111111111111', 'Технологический регламент офсетной печати и нормы брака 2026', 'docx', '/storage/docs/production_rules_2026.docx', 'Производство', '["production", "c-level"]'::jsonb, '{"author": "Кузнецов И.П.", "version": "2.1"}'::jsonb),
('22222222-2222-2222-2222-222222222222', 'Коммерческая политика, система скидок и условия отсрочки платежей', 'pdf', '/storage/docs/sales_policy_2026.pdf', 'Продажи', '["sales", "c-level"]'::jsonb, '{"author": "Морозова Е.А.", "version": "1.4"}'::jsonb),
('33333333-3333-3333-3333-333333333333', 'Положение об оплате сверхурочных часов и премировании цехов', 'xlsx', '/storage/docs/bonus_policy_2026.xlsx', 'Бухгалтерия', '["accounting", "c-level"]'::jsonb, '{"author": "Соколова О.В.", "version": "3.0"}'::jsonb),
('44444444-4444-4444-4444-444444444444', 'Правила внутреннего трудового распорядка типографии Fotoprint', 'pdf', '/storage/docs/company_general_rules.pdf', 'Общий', '["all"]'::jsonb, '{"author": "HR", "version": "1.0"}'::jsonb)
ON CONFLICT (id) DO NOTHING;

-- Чанки документов (для векторного поиска, с нулевым dummy-вектором, который при необходимости обновится через Embeddings API)
INSERT INTO corporate_document_chunks (id, document_id, chunk_index, chunk_text, embedding, allowed_access_groups) VALUES
(
    'aaaaaaa1-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    '11111111-1111-1111-1111-111111111111',
    1,
    'Технологический регламент: Допустимый процент технологического брака на листовой офсетной машине Heidelberg Speedmaster не должен превышать 2.5% от общего объема тиража при приладке и 0.8% в процессе основного тиража. Все инциденты перерасхода краски и бумаги свыше нормы оформляются актом смены.',
    NULL,
    '["production", "c-level"]'::jsonb
),
(
    'aaaaaaa2-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    '22222222-2222-2222-2222-222222222222',
    1,
    'Регламент скидок: Скидка клиентам категории А (объем заказов от 1 000 000 руб/мес) составляет до 15%. Отсрочка платежа согласовывается индивидуально только для контрагентов без просроченной дебиторской задолженности за последние 6 месяцев.',
    NULL,
    '["sales", "c-level"]'::jsonb
),
(
    'aaaaaaa3-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    '33333333-3333-3333-3333-333333333333',
    1,
    'Оплата ночных смен и сверхурочных в печатном цехе рассчитывается с коэффициентом 1.5 за первые 2 часа и 2.0 за последующие часы. Табель передается в бухгалтерию не позднее 25 числа каждого месяца.',
    NULL,
    '["accounting", "c-level"]'::jsonb
),
(
    'aaaaaaa4-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    '44444444-4444-4444-4444-444444444444',
    1,
    'Общие правила типографии Fotoprint: Рабочий день первой смены начинается в 08:00, второй смены — в 20:00. Проход на территорию производства осуществляется строго по бесконтактным картам-пропускам. Курение разрешено только в специально отведенной зоне.',
    NULL,
    '["all"]'::jsonb
)
ON CONFLICT (id) DO NOTHING;

-- ============================================================================
-- 4. ВСТРЕЧИ, ЗАМЕТКИ, ПРОТОКОЛЫ И ACTION ITEMS
-- ============================================================================

-- Встреча 1: Предстоящая планерка через 20 минут (для сценария 5.1 Pre-Meeting Prep)
INSERT INTO meetings (id, title, description, start_time, end_time, status, organizer_id, participant_ids, calendar_event_id) VALUES
(
    'bbbbbbb1-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'Оперативка производства: Срыв сроков по тиражу каталогов №44102',
    'Обсуждение причин задержки поставки меловой бумаги, текущего процента брака на Heidelberg и перераспределения смен.',
    NOW() + INTERVAL '20 minutes',
    NOW() + INTERVAL '50 minutes',
    'scheduled',
    1, -- Алексей Смирнов (CEO)
    '[1, 2, 3]'::jsonb, -- Алексей, Иван (Производство), Елена (Продажи)
    'cal-event-prod-44102'
)
ON CONFLICT (id) DO NOTHING;

-- Встреча 2: Прошедшая планерка руководителей (для сценария 5.3 Post-Meeting & 5.4 Executive Summary)
INSERT INTO meetings (id, title, description, start_time, end_time, status, organizer_id, participant_ids, calendar_event_id) VALUES
(
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'Еженедельная координация руководителей направлений',
    'Анализ выработки цехов, дебиторской задолженности и готовности оборудования к высокому сезону.',
    NOW() - INTERVAL '2 days',
    NOW() - INTERVAL '2 days' + INTERVAL '1 hour',
    'completed',
    1,
    '[1, 2, 3, 4]'::jsonb,
    'cal-event-weekly-coord'
)
ON CONFLICT (id) DO NOTHING;

-- Заметки к прошедшей встрече (для демонстрации режима секретаря и сборки протокола)
INSERT INTO meeting_notes (id, meeting_id, author_id, note_text, category) VALUES
(
    'ccccccc1-cccc-cccc-cccc-cccccccccccc',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    2,
    'Иван Кузнецов: По офсетной машине Heidelberg требуется замена красочных валов до конца месяца, иначе вырастет разнотон.',
    'discussion'
),
(
    'ccccccc2-cccc-cccc-cccc-cccccccccccc',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    3,
    'Елена Морозова: Клиент "Альфа-Маркет" требует тираж 50 000 каталогов строго к 15 числу, согласны на предоплату 100%.',
    'discussion'
),
(
    'ccccccc3-cccc-cccc-cccc-cccccccccccc',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    1,
    'Алексей Смирнов: Принято решение заказать запчасти для Heidelberg немедленно. Бухгалтерии согласовать аванс поставщику.',
    'decision'
)
ON CONFLICT (id) DO NOTHING;

-- Протокол прошедшей встречи
INSERT INTO meeting_protocols (id, meeting_id, title, summary, decisions, raw_transcript, file_docx_path, file_pdf_path) VALUES
(
    'ddddddd1-dddd-dddd-dddd-dddddddddddd',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'Протокол еженедельной координации руководителей',
    'Рассмотрены производственные риски по оборудованию, согласована отгрузка для ключевого клиента "Альфа-Маркет", утвержден график оплаты счетов на запчасти.',
    '["Согласовать покупку красочных валов для Heidelberg", "Перевести тираж Альфа-Маркет в высокий приоритет", "Оплатить счет поставщику валов до пятницы"]'::jsonb,
    'Полный транскрипт обсуждения доступен в архиве заметок.',
    '/storage/protocols/mom_weekly_coord.docx',
    '/storage/protocols/mom_weekly_coord.pdf'
)
ON CONFLICT (id) DO NOTHING;

-- Поручения (Action Items), готовые к экспорту или выгруженные в Битрикс24
INSERT INTO action_items (id, meeting_id, protocol_id, title, description, responsible_id, deadline, status, bitrix24_task_id) VALUES
(
    'eeeeeee1-eeee-eeee-eeee-eeeeeeeeeeee',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'ddddddd1-dddd-dddd-dddd-dddddddddddd',
    'Сформировать и отправить поставщику спецификацию на валы Heidelberg',
    'Связаться с сервисной службой Heidelberg, запросить счет и артикулы оригинальных валов.',
    2, -- Иван Кузнецов
    NOW() + INTERVAL '2 days',
    'exported_to_bitrix',
    1045
),
(
    'eeeeeee2-eeee-eeee-eeee-eeeeeeeeeeee',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'ddddddd1-dddd-dddd-dddd-dddddddddddd',
    'Оплатить счет на запчасти Heidelberg',
    'Провести оплату аванса 50% поставщику запчастей после получения счета от производства.',
    4, -- Ольга Соколова
    NOW() + INTERVAL '3 days',
    'pending',
    NULL
)
ON CONFLICT (id) DO NOTHING;

-- ============================================================================
-- 5. ДЕМОНСТРАЦИОННОЕ РАСПИСАНИЕ РЕФЕРЕНСНОЙ НЕДЕЛИ (12–18 НОЯБРЯ 2026)
-- ============================================================================

INSERT INTO meetings (id, title, description, start_time, end_time, status, organizer_id, participant_ids, calendar_event_id, metadata) VALUES
-- Ежедневные сводки и рутина (All-Day Bars)
('b0000001-1112-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-12 00:00:00+03', '2026-11-12 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-12', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1113-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-13 00:00:00+03', '2026-11-13 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-13', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1114-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-14 00:00:00+03', '2026-11-14 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-14', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1115-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-15 00:00:00+03', '2026-11-15 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-15', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1116-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-16 00:00:00+03', '2026-11-16 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-16', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1117-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-17 00:00:00+03', '2026-11-17 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-17', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),
('b0000001-1118-2026-0000-000000000001', 'Утренняя сводка выработки цеха', 'Ежедневная утренняя пятиминутка начальника производства и директора, фиксация приоритетов дня', '2026-11-18 00:00:00+03', '2026-11-18 23:59:59+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-routine-18', '{"item_type": "routine", "category": "routine", "is_all_day": true, "is_recurring": true, "tags": ["рутина", "цех", "приоритеты"]}'::jsonb),

-- Четверг 12 ноября 2026
('b0000112-1200-2026-0000-000000000001', 'Планерка ОМО / Снабжение бумаги', 'Сверка поставок мелованной бумаги 150г/м2, резервирование партий под тираж 50 000 экз.', '2026-11-12 12:00:00+03', '2026-11-12 13:25:00+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-omo-supply', '{"item_type": "meeting", "category": "production", "is_all_day": false, "is_recurring": false, "tags": ["снабжение", "бумага", "омо", "производство"]}'::jsonb),
('b0000112-1335-2026-0000-000000000001', 'Техконтроль печатного цеха Heidelberg (ПЗ ФизК)', 'Инструментальная проверка состояния красочных аппаратов и биения валов печатного цеха', '2026-11-12 13:35:00+03', '2026-11-12 15:00:00+03', 'scheduled', 2, '[2]'::jsonb, 'cal-tech-heidelberg', '{"item_type": "meeting", "category": "production", "is_all_day": false, "is_recurring": false, "tags": ["техконтроль", "heidelberg", "цех", "производство"]}'::jsonb),
('b0000112-1530-2026-0000-000000000001', 'Согласование коммерческих условий Альфа-Маркет (ЛК АМД)', 'Переговоры с коммерческим директором Альфа-Маркет по предоплате 100% и скидке 15%', '2026-11-12 15:30:00+03', '2026-11-12 16:55:00+03', 'scheduled', 1, '[1, 3]'::jsonb, 'cal-sales-alfamarket', '{"item_type": "meeting", "category": "sales", "is_all_day": false, "is_recurring": false, "tags": ["альфа-маркет", "клиенты", "скидки", "продажи"]}'::jsonb),

-- Пятница 13 ноября 2026
('b0000113-1900-2026-0000-000000000001', 'Итоги недели по отгрузкам (ЛК МПДиС)', 'Сводка выполненных отгрузок за неделю, контроль дебиторской задолженности и закрытие актов', '2026-11-13 19:00:00+03', '2026-11-13 20:25:00+03', 'scheduled', 1, '[1, 3]'::jsonb, 'cal-weekly-shipments', '{"item_type": "meeting", "category": "sales", "is_all_day": false, "is_recurring": false, "tags": ["отгрузки", "продажи", "итоги-недели"]}'::jsonb),

-- Суббота 14 ноября 2026
('b0000114-1200-2026-0000-000000000001', 'Аудит профилактики валов (ЛР АМД)', 'Плановое техническое обслуживание и смывка красочных валиков офсетных секций', '2026-11-14 12:00:00+03', '2026-11-14 13:25:00+03', 'scheduled', 2, '[2]'::jsonb, 'cal-roller-audit', '{"item_type": "meeting", "category": "production", "is_all_day": false, "is_recurring": false, "tags": ["аудит", "валы", "heidelberg", "производство"]}'::jsonb),
('b0000114-1335-2026-0000-000000000001', 'Сверка складских остатков и БД (ЛР СУБД)', 'Сверка остатков бумаги, красок и лаков в 1С с фактическими инвентаризационными описями', '2026-11-14 13:35:00+03', '2026-11-14 15:00:00+03', 'scheduled', 4, '[4]'::jsonb, 'cal-inv-audit', '{"item_type": "meeting", "category": "accounting", "is_all_day": false, "is_recurring": false, "tags": ["1с", "инвентаризация", "склад", "бухгалтерия"]}'::jsonb),

-- Понедельник 16 ноября 2026
('b0000116-1705-2026-0000-000000000001', 'Финансовый комитет и закрытие счетов (ЛР СУБД)', 'Утверждение реестра оплат недели: аванс поставщику валов и оплата сырья', '2026-11-16 17:05:00+03', '2026-11-16 18:30:00+03', 'scheduled', 1, '[1, 4]'::jsonb, 'cal-fin-committee', '{"item_type": "meeting", "category": "accounting", "is_all_day": false, "is_recurring": false, "tags": ["финансы", "платежи", "бухгалтерия", "счета"]}'::jsonb),
('b0000116-1900-2026-0000-000000000001', 'План продаж корпоративным клиентам (ЛК ОМО)', 'Анализ воронки ключевых корпоративных клиентов, новогодний каталог и загрузка цехов', '2026-11-16 19:00:00+03', '2026-11-16 20:25:00+03', 'scheduled', 1, '[1, 3]'::jsonb, 'cal-sales-plan', '{"item_type": "meeting", "category": "sales", "is_all_day": false, "is_recurring": false, "tags": ["продажи", "b2b", "планирование", "клиенты"]}'::jsonb),

-- Вторник 17 ноября 2026
('b0000117-1530-2026-0000-000000000001', 'Калибровка цветопробы цеха (ПЗ ФизК)', 'Проверка спектрофотометра и линеаризация выводного устройства CTP', '2026-11-17 15:30:00+03', '2026-11-17 16:55:00+03', 'scheduled', 2, '[2]'::jsonb, 'cal-color-calib', '{"item_type": "meeting", "category": "production", "is_all_day": false, "is_recurring": false, "tags": ["цветопроба", "калибровка", "цех", "производство"]}'::jsonb),
('b0000117-1705-2026-0000-000000000001', 'Разбор аналитики Langfuse и SLA (ЛК САиИО)', 'Оценка времени ответа Copilot, анализ логов трассировок и удовлетворенности пользователей', '2026-11-17 17:05:00+03', '2026-11-17 18:30:00+03', 'scheduled', 1, '[1, 2]'::jsonb, 'cal-langfuse-sla', '{"item_type": "meeting", "category": "management", "is_all_day": false, "is_recurring": false, "tags": ["langfuse", "sla", "copilot", "руководство"]}'::jsonb),
('b0000117-1900-2026-0000-000000000001', 'Синхронизация задач CRM (ПЗ ТиМАУ)', 'Разбор стадий сделок в CRM, проверка заполнения контактных лиц и задач менеджеров', '2026-11-17 19:00:00+03', '2026-11-17 20:25:00+03', 'scheduled', 3, '[3]'::jsonb, 'cal-crm-sync', '{"item_type": "meeting", "category": "sales", "is_all_day": false, "is_recurring": false, "tags": ["битрикс24", "crm", "лиды", "продажи"]}'::jsonb),

-- Среда 18 ноября 2026
('b0000118-1530-2026-0000-000000000001', 'Совещание по автоматизации n8n (ЛК САиИО)', 'Тестирование вебхука смены статуса заказов 1С и триггера авто-создания встреч', '2026-11-18 15:30:00+03', '2026-11-18 16:55:00+03', 'scheduled', 1, '[1, 2, 3]'::jsonb, 'cal-n8n-auto', '{"item_type": "meeting", "category": "management", "is_all_day": false, "is_recurring": false, "tags": ["n8n", "автоматизация", "вебхуки", "руководство"]}'::jsonb),
('b0000118-1705-2026-0000-000000000001', 'Ревизия себестоимости тиражей (ЛК СУБД)', 'Анализ плановой и фактической маржинальности ноябрьских заказов с учетом ночных смен', '2026-11-18 17:05:00+03', '2026-11-18 18:30:00+03', 'scheduled', 1, '[1, 4]'::jsonb, 'cal-cost-review', '{"item_type": "meeting", "category": "accounting", "is_all_day": false, "is_recurring": false, "tags": ["себестоимость", "тираж-44102", "бухгалтерия"]}'::jsonb),
('b0000118-1900-2026-0000-000000000001', 'Контроль дебиторской задолженности (ПЗ СУБД)', 'Сверка актов сверок и статус погашения задолженности за предыдущий отчетный месяц', '2026-11-18 19:00:00+03', '2026-11-18 20:25:00+03', 'scheduled', 4, '[4]'::jsonb, 'cal-debts-control', '{"item_type": "meeting", "category": "accounting", "is_all_day": false, "is_recurring": false, "tags": ["дебиторка", "платежи", "отчетность", "бухгалтерия"]}'::jsonb)
ON CONFLICT (id) DO NOTHING;

-- Задачи / Action Items для календаря
INSERT INTO action_items (id, meeting_id, protocol_id, title, description, responsible_id, deadline, status, bitrix24_task_id) VALUES
(
    'e0000112-1200-2026-0000-000000000001',
    'bbbbbbb2-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    'ddddddd1-dddd-dddd-dddd-dddddddddddd',
    'Отправить спецификацию валов в Битрикс24',
    'Сформировать и отправить поставщику спецификацию на валы Heidelberg в Битрикс24 (задача #1045)',
    2, -- Иван Кузнецов
    '2026-11-12 12:30:00+03',
    'pending',
    1045
),
(
    'e0000112-1900-2026-0000-000000000001',
    'bbbbbbb1-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    NULL,
    'Вечерний чек-ин смены',
    'Контрольная сдача смены печатников и передача ночного задания на тираж №44102',
    1, -- Алексей Смирнов
    '2026-11-12 19:30:00+03',
    'pending',
    1046
)
ON CONFLICT (id) DO NOTHING;

