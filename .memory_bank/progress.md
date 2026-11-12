# Progress Tracker

- [x] Stage 1–8: SOLID Audio v2.0, Calendar MCP/Gateway, TickTick 3-Column Calendar Window & 3 Themes — Completed & Verified PASS.
- [x] Stage 9 (TickTick Drag-to-Create & Dynamic Anchored Popover — Tasks `017A`, `017B`, `QA-017`): Completed & Verified PASS (`fbba193`).
- [ ] Stage 10 (Smart Tokenized Employee / Client Search Across All Interfaces — Tasks `018A`, `018B`, `018C`, `QA-018`):
  - [x] Frozen contract created: `.memory_bank/contracts/employee_search_contract.md`.
  - [ ] `018A`: Backend `GET /api/employees/search` & repository tokenized ranking.
  - [ ] `018B`: Frontend pre-indexed tokenized + fuzzy FIO/Position search, clear `✕` button, chips & autocomplete dropdowns (`#cal-drag-create-popover`, `#cal-quick-modal`, `#cal-sidebar`).
  - [ ] `018C`: 3-theme CSS styling (`Obsidian`, `Liquid Glass`, `Business Amber`) with theme-colored `<mark class="cal-search-highlight">`.
  - [ ] `QA-018`: 1,000-user scale, word-order swap & 3-theme Playwright verification.
- [ ] Stage 11 (Calendar Views: `Day`, `Month`, `Year` + `#cal-view-select` Fix — Tasks `019A`, `019B`, `QA-019`):
  - [x] Frozen contract created: `.memory_bank/contracts/calendar_view_switcher_contract.md` (v2.0, aligned to TickTick `1.png`, `2.png`, `3.png`).
  - [ ] `019A`: 1-col `Day` view, 7×6 `Month` grid (`W44–W49`, event pills, `+N` overflow), 4×3 `Year` heatmap grid (`1–52` week numbers, density shading, drill-down).
  - [ ] `019B`: 3-theme CSS styling (`Obsidian`, `Liquid Glass`, `Business Amber`) for `Day`, `Month`, and `Year` views.
  - [ ] `QA-019`: Playwright & multi-theme verification of `day`, `week`, `month`, and `year` views.
