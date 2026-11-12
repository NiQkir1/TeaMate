# Frozen Contract: Calendar View Switcher (`Week`, `3 Days`, `Day`, `Year`)

- **Version**: 1.0
- **Status**: FROZEN
- **Scope**: `infra/nginx/html/index.html`, `infra/nginx/html/themes/theme-core.css`, `infra/nginx/html/themes/theme-obsidian.css`, `infra/nginx/html/themes/theme-liquid-glass.css`, `infra/nginx/html/themes/theme-amber.css`

---

## 1. Supported View Modes & Selector (`#cal-view-select`)

The `<select id="cal-view-select" class="cal-view-dropdown">` element in `.cal-toolbar-right` MUST support exactly 4 options:

| Option `value` | Russian Label | Columns / Layout | Navigation Step (`#cal-prev-btn` / `#cal-next-btn`) |
| :--- | :--- | :--- | :--- |
| `week` | `Неделя` | 7-column time-grid | `±7` days |
| `3days` | `3 дня` | 3-column time-grid | `±3` days |
| `day` | `День` | 1-column time-grid | `±1` day |
| `year` | `Год` | 12-month overview grid (`#cal-year-grid`) | `±1` year |

- State variable: `let currentCalendarViewMode = 'week';`
- `changeCalendarView(viewMode)` validates `['week', '3days', 'day', 'year']`, updates `currentCalendarViewMode`, syncs `#cal-view-select.value`, closes any active drag-create popover (`closeDragCreatePopover()`), and triggers rendering.
- Changing view mode MUST also trigger `refreshCalendarEvents()` when entering or navigating in `year` mode so events for the full year (`YYYY-01-01` .. `YYYY-12-31`) are loaded.

---

## 2. Dynamic Time-Grid Columns (`week`, `3days`, `day`)

- CSS variable `--cal-grid-cols` MUST be set on `#cal-workspace-body` (default `7`; set to `7` for `week`, `3` for `3days`, `1` for `day`).
- In `theme-core.css`:
  - `.cal-week-columns-header`, `.cal-allday-cells-container`, and `.cal-grid-columns` MUST use:
    ```css
    grid-template-columns: repeat(var(--cal-grid-cols, 7), minmax(0, 1fr));
    ```
- Anchor date calculation in `renderWeekViewGrid()`:
  - `week`: `numCols = 7`, starts at `currentWeekStart`.
  - `3days`: `numCols = 3`, starts at `selectedCalendarDate` (or `currentWeekStart` synced on navigation).
  - `day`: `numCols = 1`, shows `[selectedCalendarDate]`.
- Current-time red dot (`#cal-now-dot`) horizontal position:
  - Must be computed dynamically using `numCols`:
    ```js
    nowDot.style.left = `calc(56px + ${todayColIdx} * ((100% - 56px) / ${numCols}))`;
    ```
- All-day strip, timed events, collision layout (`layoutDayTimedEvents`), drag-to-create (`setupDayColDrag`), and event popover (`openEventPopover`) MUST work identically in `week`, `3days`, and `day` modes.

---

## 3. Year View Layout & Drill-Down (`viewMode === 'year'`)

- Inside `#cal-workspace-body`, add `<div id="cal-year-grid" class="cal-year-grid" style="display: none;"></div>`.
- When `currentCalendarViewMode === 'year'`:
  - `.cal-week-header`, `.cal-allday-strip`, and `.cal-timegrid-scroll` MUST be hidden (`display: none`).
  - `#cal-year-grid` MUST be visible (`display: grid`).
  - `#current-month-label` MUST display `${year} г.` (e.g., `2026 г.`).
  - `#cal-year-grid` renders 12 `.cal-year-month-card` elements (`data-month="0"` through `data-month="11"`).
  - Each `.cal-year-month-card` contains:
    - `.cal-year-month-title` (clickable month name, e.g. `Ноябрь`) — clicking switches `selectedCalendarDate` / `currentWeekStart` to the 1st of that month (or 12th if Nov 2026) and switches view to `week`.
    - `.cal-year-weekdays` (`Пн Вт Ср Чт Пт Сб Вс`).
    - `.cal-year-days-grid` (42 compact day cells `.cal-year-day-cell` with `.other-month`, `.is-today`, `.is-selected`, `.has-events` + `.cal-year-event-dot` when `cachedCalendarEvents` has events on that `YYYY-MM-DD`).
  - Clicking any `.cal-year-day-cell` updates `selectedCalendarDate`, `currentCalendarDate`, `currentWeekStart`, and switches to `day` view (`changeCalendarView('day')`) for that date.
- When `currentCalendarViewMode !== 'year'`:
  - `#cal-year-grid` MUST be hidden (`display: none`).
  - `.cal-week-header`, `.cal-allday-strip`, and `.cal-timegrid-scroll` MUST be visible (`display: flex`).

---

## 4. Theme CSS Tokens (All 3 Themes)

`theme-obsidian.css`, `theme-liquid-glass.css`, and `theme-amber.css` MUST define and style:
- `--cal-year-card-bg`
- `--cal-year-card-border`
- `--cal-year-card-hover`
- `--cal-year-event-dot`
- `--cal-year-today-bg`
- `.cal-view-dropdown` hover/focus states matching each theme.
