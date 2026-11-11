-- 02_schema.sql
-- Инициализация схемы БД Fotoprint AI Copilot

\c fotoprint;

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;

-- ============================================================================
-- 1. БЕЗОПАСНОСТЬ И RBAC (Employees, Roles, Permissions)
-- ============================================================================

CREATE TABLE IF NOT EXISTS roles (
    id SERIAL PRIMARY KEY,
    name VARCHAR(50) UNIQUE NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS permissions (
    id SERIAL PRIMARY KEY,
    code VARCHAR(100) UNIQUE NOT NULL,
    description TEXT,
    category VARCHAR(50) DEFAULT 'general',
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id INT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_id INT NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE IF NOT EXISTS employees (
    id SERIAL PRIMARY KEY,
    master_id BIGINT UNIQUE NOT NULL, -- Уникальный цифровой MasterId сотрудника (только цифры)
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    middle_name VARCHAR(100),
    email VARCHAR(150) UNIQUE,
    telegram_id BIGINT UNIQUE,
    telegram_username VARCHAR(100),
    whatsapp_id VARCHAR(50) UNIQUE,
    department VARCHAR(100) NOT NULL, -- 'Производство', 'Продажи', 'Бухгалтерия', 'Руководство'
    position VARCHAR(100) NOT NULL,
    access_groups JSONB NOT NULL DEFAULT '["all"]'::jsonb, -- ['all', 'production', 'sales', 'accounting', 'c-level']
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS employee_roles (
    employee_id INT NOT NULL REFERENCES employees(id) ON DELETE CASCADE,
    role_id INT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    assigned_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (employee_id, role_id)
);

CREATE INDEX IF NOT EXISTS idx_employees_master_id ON employees(master_id);
CREATE INDEX IF NOT EXISTS idx_employees_telegram_id ON employees(telegram_id);
CREATE INDEX IF NOT EXISTS idx_employees_access_groups ON employees USING GIN(access_groups);

-- ============================================================================
-- 2. ЕДИНАЯ БАЗА ДИАЛОГОВ ВСЕХ ПОЛЬЗОВАТЕЛЕЙ (Sessions, Messages, MasterId)
-- ============================================================================

CREATE TABLE IF NOT EXISTS chat_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(), -- Уникальный UUID SessionId текущего диалога
    master_id BIGINT NOT NULL REFERENCES employees(master_id) ON DELETE CASCADE, -- Цифровой MasterId пользователя
    employee_id INT REFERENCES employees(id) ON DELETE SET NULL,
    messenger_type VARCHAR(50) NOT NULL DEFAULT 'telegram', -- 'telegram', 'whatsapp', 'web', 'max'
    messenger_chat_id BIGINT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE, -- Активна ли сессия (при смене контекста переходит в FALSE)
    title VARCHAR(255) DEFAULT 'Новый диалог',
    current_branch VARCHAR(50) DEFAULT 'general', -- 'meeting_prep', 'live_meeting_notes', 'meeting_summary', 'doc_search', 'task_management', 'executive_report'
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    closed_at TIMESTAMPTZ NULL, -- Время завершения сессии при смене диалога
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE, -- Привязка к SessionId
    master_id BIGINT NOT NULL,                                              -- Цифровой MasterId автора
    context_id VARCHAR(100) NOT NULL DEFAULT 'default',                   -- Группировка в рамках контекста
    role VARCHAR(20) NOT NULL,                                             -- 'user', 'assistant', 'system'
    content TEXT NOT NULL,
    is_frozen BOOLEAN DEFAULT FALSE,                                       -- Признак изоляции при смене темы
    metadata JSONB DEFAULT '{}'::jsonb,                                    -- trace_id, tokens, branch
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS context_batches (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
    summary TEXT,
    message_count INT NOT NULL DEFAULT 0,
    is_context_switched BOOLEAN DEFAULT FALSE,
    branch VARCHAR(50),
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_chat_sessions_master_active ON chat_sessions(master_id, is_active);
CREATE INDEX IF NOT EXISTS idx_messages_session_id ON messages(session_id);
CREATE INDEX IF NOT EXISTS idx_messages_master_id ON messages(master_id);
CREATE INDEX IF NOT EXISTS idx_messages_session_created ON messages(session_id, created_at);

-- ============================================================================
-- 3. ВЕКТОРНАЯ БАЗА (RAG: Corporate Documents & Document Chunks)
-- ============================================================================

CREATE TABLE IF NOT EXISTS corporate_documents (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title VARCHAR(255) NOT NULL,
    source_type VARCHAR(50) NOT NULL, -- 'pdf', 'docx', 'xlsx', 'txt', 'manual'
    file_path TEXT,
    department VARCHAR(100),
    allowed_access_groups JSONB NOT NULL DEFAULT '["all"]'::jsonb,
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS corporate_document_chunks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES corporate_documents(id) ON DELETE CASCADE,
    chunk_index INT NOT NULL,
    chunk_text TEXT NOT NULL,
    embedding vector(1536), -- стандарт OpenAI text-embedding-3-small / text-embedding-ada-002
    allowed_access_groups JSONB NOT NULL DEFAULT '["all"]'::jsonb,
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_chunks_allowed_access_groups ON corporate_document_chunks USING GIN(allowed_access_groups);
CREATE INDEX IF NOT EXISTS idx_documents_allowed_access_groups ON corporate_documents USING GIN(allowed_access_groups);
-- Векторный индекс для косинусного сходства (HNSW)
CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON corporate_document_chunks 
USING hnsw (embedding vector_cosine_ops);

-- ============================================================================
-- 4. РЕЕСТР ВСТРЕЧ И ПРОТОКОЛЫ (Meetings, Notes, Protocols, Action Items)
-- ============================================================================

CREATE TABLE IF NOT EXISTS meetings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title VARCHAR(255) NOT NULL,
    description TEXT,
    start_time TIMESTAMPTZ NOT NULL,
    end_time TIMESTAMPTZ,
    status VARCHAR(50) DEFAULT 'scheduled', -- 'scheduled', 'in_progress', 'completed', 'cancelled'
    organizer_id INT REFERENCES employees(id) ON DELETE SET NULL,
    participant_ids JSONB DEFAULT '[]'::jsonb, -- список employee_id: [1, 2, 3]
    calendar_event_id VARCHAR(100),
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS meeting_notes (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    meeting_id UUID NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
    author_id INT REFERENCES employees(id) ON DELETE SET NULL,
    note_text TEXT NOT NULL,
    category VARCHAR(50) DEFAULT 'general', -- 'discussion', 'decision', 'question', 'action_point'
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS meeting_protocols (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    meeting_id UUID NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
    title VARCHAR(255) NOT NULL,
    summary TEXT NOT NULL,
    decisions JSONB DEFAULT '[]'::jsonb,
    raw_transcript TEXT,
    file_docx_path TEXT,
    file_pdf_path TEXT,
    metadata JSONB DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Изолированное векторное хранилище чанков созвонов (отдельно от корпоративных регламентов)
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
CREATE INDEX IF NOT EXISTS idx_meeting_chunks_embedding ON meeting_chunks
USING hnsw (embedding vector_cosine_ops);

CREATE TABLE IF NOT EXISTS action_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    meeting_id UUID NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
    protocol_id UUID REFERENCES meeting_protocols(id) ON DELETE SET NULL,
    title VARCHAR(255) NOT NULL,
    description TEXT,
    responsible_id INT REFERENCES employees(id) ON DELETE SET NULL,
    deadline TIMESTAMPTZ,
    status VARCHAR(50) DEFAULT 'pending', -- 'pending', 'confirmed', 'exported_to_bitrix', 'cancelled'
    bitrix24_task_id INT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_meetings_start_time ON meetings(start_time);
CREATE INDEX IF NOT EXISTS idx_meetings_status ON meetings(status);
CREATE INDEX IF NOT EXISTS idx_meeting_notes_meeting_id ON meeting_notes(meeting_id);
CREATE INDEX IF NOT EXISTS idx_action_items_meeting_id ON action_items(meeting_id);
CREATE INDEX IF NOT EXISTS idx_action_items_responsible ON action_items(responsible_id);

