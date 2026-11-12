# Active Context

- **Current Epics**:
  - **Stage 10**: Smart Tokenized Employee / Client Search by FIO, Position & Department (`Tasks 018A, 018B, 018C, QA-018`).
  - **Stage 11**: Calendar View Switcher Fix (`Week`, `3 Days`, `Day`) & Year View (`Tasks 019A, 019B, QA-019`).
- **Completed Baseline**:
  - Standalone 3-column TickTick Calendar Window (`#calendar-window-view`) + Drag-to-Create selection + Context-Anchored Popover (`#cal-drag-create-popover`) implemented and visually verified across `Obsidian`, `Liquid Glass`, and `Business Amber`.
- **Stage 11 Plan (`019A`, `019B`, `QA-019`)**:
  - **Contract**: `.memory_bank/contracts/calendar_view_switcher_contract.md`
  - **Task `019A`**: Fix `renderWeekViewGrid()` and `changeWeek()` to honor `week` (7 cols), `3days` (3 cols), `day` (1 col) via `--cal-grid-cols`, plus add `year` (`#cal-year-grid` 12-month overview with event dots and click-to-drill-down).
  - **Task `019B`**: Theme tokens and Year View / dropdown styling in `theme-obsidian.css`, `theme-liquid-glass.css`, and `theme-amber.css`.
  - **Task `QA-019`**: Verification of all 4 view modes, navigation steps, drill-down, drag-to-create, and all 3 themes.
- **Next Step**:
  - Dispatch `019A` + `019B` (and/or `018A`–`018C`) to `coder-worker` subagents upon user confirmation.
