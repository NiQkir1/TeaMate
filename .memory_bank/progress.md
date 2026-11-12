# Progress Tracker

- [x] Stage 1–8: SOLID Audio v2.0, Calendar MCP/Gateway, TickTick 3-Column Calendar Window & 3 Themes — Completed & Verified PASS.
- [x] Stage 9 (TickTick Drag-to-Create & Dynamic Anchored Popover — Tasks `017A`, `017B`, `QA-017`): Completed & Verified PASS (`fbba193`).
- [ ] Stage 10 (Smart Tokenized Employee / Client Search Across All Interfaces — Tasks `018A`, `018B`, `018C`, `QA-018`):
  - [x] Frozen contract created: `.memory_bank/contracts/employee_search_contract.md`.
  - [ ] `018A`: Backend `GET /api/employees/search` & repository tokenized ranking.
  - [ ] `018B`: Frontend pre-indexed tokenized + fuzzy FIO/Position search, clear `✕` button, chips & autocomplete dropdowns (`#cal-drag-create-popover`, `#cal-quick-modal`, `#cal-sidebar`).
  - [ ] `018C`: 3-theme CSS styling (`Obsidian`, `Liquid Glass`, `Business Amber`) with theme-colored `<mark class="cal-search-highlight">`.
  - [ ] `QA-018`: 1,000-user scale, word-order swap & 3-theme Playwright verification.
- [ ] Stage 11 (Calendar View Switcher Fix & Year View — Tasks `019A`, `019B`, `QA-019`):
  - [x] Frozen contract created: `.memory_bank/contracts/calendar_view_switcher_contract.md`.
  - [ ] `019A`: Dynamic `--cal-grid-cols` (`7`/`3`/`1`), adaptive `Prev`/`Next` navigation, `#cal-now-dot` positioning, and 12-month `#cal-year-grid` with drill-down.
  - [ ] `019B`: 3-theme CSS styling (`Obsidian`, `Liquid Glass`, `Business Amber`) for Year View cards and `#cal-view-select`.
  - [ ] `QA-019`: Playwright & multi-theme verification of `week`, `3days`, `day`, and `year` views.
