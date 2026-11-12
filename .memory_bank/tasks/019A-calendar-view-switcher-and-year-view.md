# Task 019A: Fix Calendar View Switcher (`Week`, `3 Days`, `Day`) & Implement Year View

- **Scope & Assigned Worktree**: Branch `feature/ticktick-calendar-ui` (Frontend Calendar Logic & Core Layout)
- **File Boundaries**:
  - `infra/nginx/html/index.html`
  - `infra/nginx/html/themes/theme-core.css`
- **Frozen Contract**: `.memory_bank/contracts/calendar_view_switcher_contract.md`

---

## Implementation Guidelines

1. **HTML Updates (`infra/nginx/html/index.html`)**:
   - In `#cal-view-select`, add `<option value="year">Год</option>` alongside `week` (`Неделя`), `3days` (`3 дня`), and `day` (`День`).
   - Inside `#cal-workspace-body`, add `<div id="cal-year-grid" class="cal-year-grid" style="display: none;"></div>`.

2. **Core CSS Updates (`infra/nginx/html/themes/theme-core.css`)**:
   - Replace hardcoded `grid-template-columns: repeat(7, 1fr);` on `.cal-week-columns-header`, `.cal-allday-cells-container`, and `.cal-grid-columns` with:
     ```css
     grid-template-columns: repeat(var(--cal-grid-cols, 7), minmax(0, 1fr));
     ```
   - Add responsive structural styles for `.cal-year-grid` (4 columns on desktop `repeat(4, minmax(0, 1fr))`, 3 columns on `<= 1200px`, 2 columns on `<= 820px`, scrollable `overflow-y: auto`), `.cal-year-month-card`, `.cal-year-month-title`, `.cal-year-weekdays`, `.cal-year-days-grid`, `.cal-year-day-cell`, `.cal-year-event-dot`, using CSS variables `--cal-year-card-bg`, `--cal-year-card-border`, `--cal-year-card-hover`, `--cal-year-event-dot`, `--cal-year-today-bg` with sensible fallbacks.

3. **JavaScript Engine (`infra/nginx/html/index.html`)**:
   - Add `let currentCalendarViewMode = 'week';`.
   - Update `changeCalendarView(viewMode)`:
     - Validate against `['week', '3days', 'day', 'year']`.
     - Update `currentCalendarViewMode` and `#cal-view-select.value`.
     - Call `closeDragCreatePopover()`.
     - When switching to `3days` or `day`, ensure `selectedCalendarDate` is valid and used as the view anchor; when switching to `year`, call `refreshCalendarEvents()` so full-year events are fetched.
     - Re-render the active view (`renderWeekViewGrid()` or `renderYearViewGrid()`).
   - Update `renderWeekViewGrid()`:
     - When `currentCalendarViewMode === 'year'`, hide `.cal-week-header`, `.cal-allday-strip`, `.cal-timegrid-scroll`, show `#cal-year-grid`, and call `renderYearViewGrid()`.
     - Otherwise show `.cal-week-header`, `.cal-allday-strip`, `.cal-timegrid-scroll`, hide `#cal-year-grid`.
     - Compute `numCols` (`7` for `week`, `3` for `3days`, `1` for `day`) and set `workspaceBody.style.setProperty('--cal-grid-cols', String(numCols))`.
     - Set anchor start date: `currentWeekStart` for `week`, `selectedCalendarDate` for `3days` and `day`.
     - Loop `for (let i = 0; i < numCols; i++)` to render day headers, all-day cells, and 24h day columns (preserving `setupDayColDrag(colDiv, isoDate)` and `layoutDayTimedEvents`).
     - Update `#cal-now-dot` horizontal offset using `numCols`: `calc(56px + ${todayColIdx} * ((100% - 56px) / ${numCols}))`.
     - Update `#current-month-label` to `${MONTH_NAMES[anchor.getMonth()]} ${anchor.getFullYear()}`.
   - Implement `renderYearViewGrid()`:
     - Render 12 `.cal-year-month-card` elements for `currentCalendarDate.getFullYear()`.
     - Update `#current-month-label` to `${year} г.`.
     - Highlight `todayStr` (`2026-11-12` or real today if matched) and `selectedCalendarDate`.
     - Mark days with visible events (`isEventVisible(ev)`) using `.has-events` and `.cal-year-event-dot` + `title` showing event count.
     - Clicking a `.cal-year-day-cell` sets `selectedCalendarDate`, `currentCalendarDate`, `currentWeekStart` to that date and calls `changeCalendarView('day')`.
     - Clicking `.cal-year-month-title` sets `selectedCalendarDate`, `currentCalendarDate`, `currentWeekStart` to the 1st of that month and calls `changeCalendarView('week')`.
   - Update `changeWeek(delta)` (keep function name for `onclick="changeWeek(-1/1)"` compatibility):
     - `week`: shift `currentWeekStart` by `delta * 7` days.
     - `3days`: shift `selectedCalendarDate` and `currentWeekStart` by `delta * 3` days.
     - `day`: shift `selectedCalendarDate` and `currentWeekStart` by `delta * 1` day.
     - `year`: shift `currentCalendarDate` and `selectedCalendarDate` by `delta * 1` year.
     - Re-render mini-month, active main view, and refresh events if month/year changed.
   - Update `refreshCalendarEvents()`:
     - When `currentCalendarViewMode === 'year'`, request `start_date=${year}-01-01` and `end_date=${year}-12-31`.
   - Update `goToToday()`:
     - Preserve or reset dates to `2026-11-12`, re-render whichever view mode is active (`year` or time-grid).

---

## Acceptance Criteria

- Selecting `Неделя`, `3 дня`, `День`, or `Год` in `#cal-view-select` immediately switches the main calendar workspace to 7 columns, 3 columns, 1 column, or 12-month Year View respectively.
- `Prev` (`#cal-prev-btn`) and `Next` (`#cal-next-btn`) step by 7 days, 3 days, 1 day, or 1 year according to the active mode.
- Drag-to-create and event inspector popover continue to work in `week`, `3days`, and `day` modes.
- Clicking any day in Year View drills down to `day` view on that exact date; clicking a month title drills down to `week` view.
