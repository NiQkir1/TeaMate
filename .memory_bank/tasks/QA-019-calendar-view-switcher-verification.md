# Task QA-019: Verification of Calendar View Switcher (`Week`, `3 Days`, `Day`, `Year`) & 3 Themes

- **Scope & Assigned Worktree**: Branch `feature/ticktick-calendar-ui` (QA Verifier)
- **Dependencies**: `019A`, `019B`
- **File Boundaries**: Read-only audit of application code; may run Playwright/Python/Node checks and capture verification screenshots in `docs/screenshots/`.
- **Frozen Contract**: `.memory_bank/contracts/calendar_view_switcher_contract.md`

---

## Verification Checklist

1. **View Mode Switching (`#cal-view-select`)**:
   - `week`: Verify `.cal-day-col` count === `7`, `.cal-day-header-col` count === `7`, `--cal-grid-cols` === `7`.
   - `3days`: Verify `.cal-day-col` count === `3`, `.cal-day-header-col` count === `3`, `--cal-grid-cols` === `3`.
   - `day`: Verify `.cal-day-col` count === `1`, `.cal-day-header-col` count === `1`, `--cal-grid-cols` === `1`.
   - `year`: Verify `#cal-year-grid` is visible, `.cal-timegrid-scroll` is hidden, and exactly `12` `.cal-year-month-card` elements are rendered.
2. **Navigation (`#cal-prev-btn`, `#cal-next-btn`, `#cal-today-btn`)**:
   - In `3days`: `Next` advances start date by 3 days; `Prev` steps back 3 days.
   - In `day`: `Next` advances by 1 day; `Prev` steps back 1 day.
   - In `year`: `Next` switches `2026 г.` -> `2027 г.`; `Prev` returns to `2026 г.`.
3. **Year View Drill-Down & Event Indicators**:
   - Verify Nov 12, 2026 has `.has-events` / `.cal-year-event-dot` and `.is-today`.
   - Clicking a day cell in `#cal-year-grid` switches `#cal-view-select` to `day` on that date.
   - Clicking a month title in `#cal-year-grid` switches `#cal-view-select` to `week`.
4. **Interactions & Current-Time Line**:
   - Verify `#cal-now-dot` stays aligned with Today's column in `week`, `3days`, and `day` views.
   - Verify drag-to-create opens `#cal-drag-create-popover` cleanly in `day` and `3days` views.
5. **Multi-Theme & Regression Check**:
   - Verify `year`, `day`, `3days`, and `week` views across `obsidian`, `liquid-glass`, and `amber` themes with zero console errors and no horizontal overflow.
   - Run existing tests in `tests/` to ensure no regressions.

---

## Output Format

Report `VERDICT: PASS` or `VERDICT: FAIL` with concrete counts, test results, and any discrepancies.
