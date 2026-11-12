# Task 019B: Multi-Theme Styling for Calendar View Switcher & Year View (3 Themes)

- **Scope & Assigned Worktree**: Branch `feature/ticktick-calendar-ui` (UI Theme & Styling Specialist)
- **File Boundaries**:
  - `infra/nginx/html/themes/theme-obsidian.css`
  - `infra/nginx/html/themes/theme-liquid-glass.css`
  - `infra/nginx/html/themes/theme-amber.css`
- **Frozen Contract**: `.memory_bank/contracts/calendar_view_switcher_contract.md`

---

## Implementation Guidelines

1. **Theme Tokens & Year View Styling across All 3 Themes**:
   Define `--cal-year-card-bg`, `--cal-year-card-border`, `--cal-year-card-hover`, `--cal-year-event-dot`, `--cal-year-today-bg`, and polish `.cal-view-dropdown`, `.cal-year-month-card`, `.cal-year-month-title`, `.cal-year-day-cell`, `.cal-year-day-cell.is-today`, `.cal-year-day-cell.is-selected`, and `.cal-year-event-dot` in each theme:
   - **`theme-obsidian.css` (Linear Obsidian Pro)**:
     - Matte dark `#141414` / `#191919` cards, hairline `rgba(255, 255, 255, 0.08)` borders, indigo `#6366f1` today/selected/focus accents, crisp subtle hover states.
   - **`theme-liquid-glass.css` (Apple Liquid Glass)**:
     - Frosted translucent glass cards with `backdrop-filter: blur(18px) saturate(160%)`, specular `rgba(255, 255, 255, 0.14)` top borders, sky-blue `#38bdf8` today/event-dot accents, smooth hover glow.
   - **`theme-amber.css` (Business Noir & Electric Amber)**:
     - Deep noir `#0f1117` cards, gold-tinted `rgba(245, 158, 11, 0.18)` borders, electric amber `#f59e0b` today/selected/event-dot highlights, sharp executive contrast.
2. **Dropdown & Contrast**:
   - Ensure `#cal-view-select.cal-view-dropdown` and its `<option>` elements have high-contrast readable text and hover/focus rings in all 3 themes.
   - Ensure zero horizontal overflow in Year View (`#cal-year-grid`) at both `1440x900` and `1024x768`.

---

## Acceptance Criteria

- All 3 themes (`obsidian`, `liquid-glass`, `amber`) define the Year View CSS tokens and distinct visual styling for `.cal-year-month-card` and `.cal-view-dropdown`.
- Text, today badges, selected dates, and event dots are clearly legible in all 3 themes.
