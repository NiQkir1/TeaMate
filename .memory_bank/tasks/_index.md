# Task Registry & DAG — Fotoprint AI Copilot

| Task ID | Name | Assigned Role | Worktree / Branch | Paired QA Agent | Status |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `001`–`016` | SOLID Audio v2.0, Calendar MCP, TickTick 3-Column Calendar & 3 Themes | Core / API / UI Teams | `feature/ticktick-calendar-ui` | `QA-001`–`QA-015` | `[Completed - Verified]` |
| `017A` | TickTick Drag-to-Create Selection & Dynamic Left/Right Anchored Popover | Frontend Calendar Interactions Developer | `feature/ticktick-calendar-ui` | `QA-017` | `[Completed - Verified]` |
| `017B` | Multi-Theme Styling for Drag Ghost & Anchored Popover | UI Theme & Styling Specialist | `feature/ticktick-calendar-ui` | `QA-017` | `[Completed - Verified]` |
| `QA-017` | Playwright & Multi-Theme QA Verification of Drag-to-Create | QA Verifier | `feature/ticktick-calendar-ui` | Audits `017A–017B` | `[Completed - PASS]` |
| `018A` | Backend Scalable Employee Search Endpoint (`GET /api/employees/search`) | Backend API Developer (`coder-worker`) | `feature/ticktick-calendar-ui` | `QA-018` | `[To Do]` |
| `018B` | Frontend Smart Tokenized FIO/Role Search, Clear `✕` & Autocomplete UI (Popover, Modal, Sidebar) | Frontend Calendar & Search Developer (`coder-worker`) | `feature/ticktick-calendar-ui` | `QA-018` | `[To Do — Parallel with 018A, 018C]` |
| `018C` | Multi-Theme Styling for Employee Search, Chips, Dropdown & Theme-Colored `<mark>` Highlights | UI Theme & Styling Specialist (`coder-worker`) | `feature/ticktick-calendar-ui` | `QA-018` | `[To Do — Parallel with 018A, 018B]` |
| `QA-018` | Comprehensive QA & 1,000-User Scale Test of Smart Employee Search Across All 3 Themes | QA Verifier (`qa-worker`) | `feature/ticktick-calendar-ui` | Audits `018A–018C` | `[To Do — Depends on 018A, 018B, 018C]` |
| `019A` | Fix Calendar View Switcher (`Week`, `3 Days`, `Day`) & Implement Year View | Frontend Calendar Developer (`coder-worker`) | `feature/ticktick-calendar-ui` | `QA-019` | `[To Do — Parallel with 019B]` |
| `019B` | Multi-Theme Styling for View Dropdown & Year View (`Obsidian`, `Liquid Glass`, `Amber`) | UI Theme & Styling Specialist (`coder-worker`) | `feature/ticktick-calendar-ui` | `QA-019` | `[To Do — Parallel with 019A]` |
| `QA-019` | Playwright & Multi-Theme QA Verification of View Switcher & Year View | QA Verifier (`qa-worker`) | `feature/ticktick-calendar-ui` | Audits `019A–019B` | `[To Do — Depends on 019A, 019B]` |

---

## Execution DAG for Stage 10 (Smart Employee Search) & Stage 11 (Calendar View Switcher)

```text
Stage 10:
   [018A] + [018B] + [018C] ──▶ [QA-018]

Stage 11:
   [019A: View Switcher & Year Grid] + [019B: 3-Theme Year Styling] ──▶ [QA-019]
```
