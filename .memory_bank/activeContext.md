# Active Context

- **Current Epics**:
  - **Stage 10**: Smart Tokenized Employee / Client Search by FIO, Position & Department (`Tasks 018A, 018B, 018C, QA-018`).
  - **Stage 11**: Calendar Views (`Day`, `Month`, `Year`) + `#cal-view-select` Fix (`Tasks 019A, 019B, QA-019`).
- **Completed Baseline**:
  - Standalone 3-column TickTick Calendar Window (`#calendar-window-view`, Week View) + Drag-to-Create selection + Context-Anchored Popover (`#cal-drag-create-popover`) implemented and visually verified across `Obsidian`, `Liquid Glass`, and `Business Amber`.
- **Stage 11 Plan (`019A`, `019B`, `QA-019`) — Aligned to TickTick Reference Screenshots**:
  - **Contract**: `.memory_bank/contracts/calendar_view_switcher_contract.md`
  - **Task `019A`**: Replace `3days` with `day` / `week` / `month` / `year` in `#cal-view-select`; fix 1-column `Day` view (`--cal-grid-cols: 1`), add 7×6 `Month` view (`#cal-month-grid` with `W44–W49` week numbers, compact event pills, `+N` overflow), and add 4×3 `Year` heatmap view (`#cal-year-grid` with left week-number column `1–52`, event-density shading, and click-to-drill-down).
  - **Task `019B`**: Theme tokens and styling for `Day`, `Month`, and `Year` heatmap views across `theme-obsidian.css`, `theme-liquid-glass.css`, and `theme-amber.css`.
  - **Task `QA-019`**: Verification of `Day`, `Week`, `Month`, `Year` views, navigation steps, drill-down, drag-to-create, and all 3 themes.
- **Next Step**:
  - Dispatch `019A` + `019B` (and/or `018A`–`018C`) to `coder-worker` subagents upon user confirmation.
