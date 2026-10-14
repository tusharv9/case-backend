# Case Management transformation (CM-01 … CM-13) — handover

Delivered in 8 approved phases on 2026-10-13. Everything is **uncommitted** in the working tree, alongside earlier uncommitted
work that was already there (notably in `CaseService.cs`, `CaseDtos.cs`, `DbSeeder.cs` and several frontend files); review the diff
before committing.

**Verification at handover:** backend builds without compiler warnings · **363/363** backend tests pass on PostgreSQL ·
frontend builds · lint 75 warnings (78 before; none in new code) · end-to-end API smoke test **15/15** on a fresh database.

---

## 1. Shared database — applied 2026-10-13

Applied to the shared (Neon) database on 2026-10-13 (rehearsed first on a full copy; the live dry run matched it exactly):

| Item | Kind | What it does |
|---|---|---|
| `ReasonDetailRequirement` | migration | adds `LookupValues.RequiresDetail` |
| `ManualAssignmentSettings` | migration | adds the Assign & Reassign policy (one row = today's behaviour), its cross-team list, `TeamMembers.CanBeManuallyAssigned` (existing members: true) |
| `bootstrap.escalation-reasons-and-reason-detail.v1` | one-time step | creates `ESCALATION_REASON` from the values the Escalate dialog used to hard-code; marks Other/Others "needs explanation"; **deactivates** "Escalation" as an assignment reason |
| `bootstrap.case-list-filter-fields.v1` | one-time step | adds Priority and Channel rows to the Case Filters configuration (the list showed them before they were configurable) |

Then the data reconciliation (Phase 7), **applied 09:10 UTC with actor amina@bank.com** (one transaction):

```bash
dotnet CaseManagement.Api.dll --reconcile-data                                    # dry run: review the report
dotnet CaseManagement.Api.dll --reconcile-data --apply --actor <admin email>      # apply, one transaction, audited
```

Result: 12 user statuses stored as `"0"` → `Available`; **57** open cases held
outside their team → spread by each team's algorithm up to capacity, the lead holding only the overflow (3 cases); 6 sub-category
values need a person to recategorise (1–2 cases each, left untouched). Verified afterwards: a new dry run finds 0 statuses, 0
misplaced cases, 0 blank sub-categories; 57 audit events "by data reconciliation" on 57 cases. Pre-apply backups of user statuses
and open-case holders were taken (CSV, outside the repo). Already done on the shared database at your request: "General Inquiry" added
to Micro Finance. Inactive teams Campaign Studio and Marketing Team stay archived.

---

## 1b. Workbook reconciliation — applied 2026-10-14

The business workbook (`RMA_OmniCX_Case_Categories_Banking`) is now the case classification on the shared database:
- 11 active teams, one per category;
- 64 active sub-categories, each with its workbook ID, name, description and default priority;
- the workbook's SLA matrix.

Details, decisions and verification are in `WORKBOOK_RECONCILIATION_PLAN.md`. The 6 new teams (Cards, Deposits & Savings, Payments &
Transfers, Digital Banking, Branch & ATM Services, KYC & Compliance) have no members yet: staff them on the Teams page.

Known: automatic SLA escalation to a named person (e.g. Level 4, Head of Customer Experience) makes that person the owner even when
they are outside the case's team (case C-01226). Decide whether escalation should change ownership; `--reconcile-data` hands such
cases back to their team.

## 2. Requirement status

| ID | Requirement | Status | Where |
|---|---|---|---|
| CM-01 | Searchable dropdowns, placeholders | Done | shared `Select` (ids, ARIA, load-error + retry, Home/End/Tab); data-backed lists migrated; "Select a Department" convention; short fixed enumerations in dense tables stay native (documented rule) |
| CM-02 | Clean Description section | Done | header card + issue description only; Department and Sub-category moved to Details → Key Details (the old block read non-existent fields) |
| CM-03 | Department → Team → Agent → Case | Done | one eligibility service for routing, assign, reassign, transfer; transfer: receiving lead, else the team's algorithm, else refused |
| CM-04 | Assign / Reassign | Done | eligible-assignees API (real status, workload, reasons); server enforces eligibility (422 + code) and who may reassign (403, `cases.assign`) |
| CM-05 | Assign & Reassign settings | Done | Configurable Settings tab: scope / cross-team, leads, member selection, availability & capacity rules, permissions, default routing, reasons; versioned save (409 on stale) |
| CM-06 | Escalation reason and Other | Done | configured lists for assign / transfer / escalate; "needs explanation" flag enforced by the server; Escalation is no longer an assignment reason |
| CM-07 | Simplify settings | Done | Create Case Form · Case Filters · Assign & Reassign; Master Data tab removed after every card was rehomed |
| CM-08 | Dynamic Create Case form | Done | editable core options (case types, sub-categories) with rename cascade; option reordering; type changes (existing safety check); filters drive the case list |
| CM-09 | SLA priority integration | Done (workbook pending business) | priorities added only with all three targets (no hidden defaults); activate/deactivate/rename on the SLA matrix; workbook reconciled in `WORKBOOK_RECONCILIATION.md`, nothing imported |
| CM-10 | Masking end to end | Done | server-side, module-scoped rules; case custom fields, customer names on case screens, dashboard and audit masked; filters cannot mask; `pii.unmask` sees raw |
| CM-11 | Drawer sizing | Done | `--drawer-width-sm/md/lg` tokens + `SideDrawer size`; shell CSS owned by `SideDrawer`; duplicate shell CSS removed |
| CM-12 | Unsaved changes | Done | one `UnsavedChangesDialog` (Continue editing / Discard / Save, change summary) for drawers, page guards and the settings page |
| CM-13 | Department / team reconciliation | Done (applied to the shared database) | `--reconcile-data` (dry run / apply); 6 active departments = 6 teams by construction; 2 inactive, unreferenced teams kept archived |

## 3. Acceptance tests (brief §7)

| # | Scenario | Result | Evidence |
|---|---|---|---|
| 1 | Searchable dropdowns, meaningful placeholders | Pass | browser checks Phase 2/3; `Input.jsx` |
| 2 | Description shows only the intended content | Pass | browser check Phase 3 |
| 3 | No unrelated agents under the default policy | Pass | `EligibleAssignees_AreTheCasesTeamOnly…`, smoke |
| 4 | Team change revalidates eligibility | Pass | `Transfer_GoesToTheReceivingLead_AndEligibilityFollowsTheNewTeam` |
| 5 | Backend rejects unauthorised / ineligible assignment | Pass | `Assigning_ToSomeoneOutside…`, `OnlyTheOwnerTeamMembers…`, E2E 422/403 |
| 6 | Availability only from real data | Pass | status = `Users.Status`; unknown values read Offline |
| 7 | Assignment scope persists after reload | Pass | `TheMigrationKeepsTodaysBehaviour_AndSavedSettingsSurviveAReload`, smoke |
| 8 | Selecting members changes the eligible list | Pass | `SelectingMembers_AndExcludingLeads…` |
| 9 | Cross-team disabled prevents cross-team assignment | Pass | `CrossTeam_IsOffByDefault_ThenOnlyForTheChosenTeams…` |
| 10 | Round robin / skill / least occupancy use the eligibility model | Pass | `AgentPoolService` delegates to the eligibility service; routing tests |
| 11 | Assignment history kept | Pass | `Assigning_ToAnEligibleMember_Works_KeepsTheSlaClock_AndRecordsTheHistory`, smoke |
| 12 | Removing master-data UI keeps dropdown configuration working | Pass | Phase 5 browser checks; `SettingsConsolidationTests` |
| 13 | Field types changeable where supported | Pass | existing `FieldTypeChangeTests` |
| 14 | Unsafe conversions handled | Pass | existing check-type dry run |
| 15 | New options persist and appear on the form | Pass | Phase 5 browser check (case type added, reordered) |
| 16 | New priorities get SLA targets, no arbitrary defaults | Pass | `APriorityWithoutSlaTargets_IsRefused…`, `APriorityIsAddedOnTheSlaMatrix…` |
| 17 | Masking survives save and reload | Pass | E2E + smoke (masking saved through the API, then read) |
| 18 | No sensitive values through unauthorised APIs or logs | Pass | masking tests; request logs carry route/status only |
| 19 | Closing an edited configuration drawer warns correctly | Pass | Phase 2/5 browser checks (no false warnings) |
| 20 | Drawer dimensions consistent and responsive | Pass | tokens; phone-width check (full width) |
| 21 | Reconciliation keeps history and references | Pass | `DataReconciliationTests` (audited moves; inactive teams kept) |
| 22 | No hard-coded business records or sample seeding | Pass | lists moved from code to configuration; bootstrap steps add required configuration only |
| 23 | Builds healthy | Pass | see top |
| 24 | Existing functionality keeps working | Pass | 363 tests, smoke 15/15 |

## 4. API changes

| Endpoint | Change |
|---|---|
| `GET /api/cases/{id}/eligible-assignees` | new (`cases.read`) |
| `PUT /api/cases/{id}/assign` | 403 when the caller may not reassign; 422 `{code}` for an ineligible person; reason must be a configured option (+ `reasonDetail` when required) |
| `PUT /api/cases/{id}/transfer` | needs a different, active team; same reason rules; no "queue" transfers |
| `POST /api/cases/{id}/escalate` | reason from `ESCALATION_REASON` (+ `reasonDetail`) |
| `GET/PUT /api/assignment-settings` | new (read: signed-in; save: `config.manage`; 409 on stale version) |
| lookup values | `requiresDetail` returned; list save accepts it (omitted = unchanged) |
| field configuration save | masking on a Filters field → 400 |
| case DTOs | `ownerTeam` / `ownerQueue` removed; user `team` = real memberships; `queue` removed |

New permission **`cases.assign`** (Team Lead, Supervisor, Admin by default; mapped through the Host's roles). Add it to any Host role
that should reassign across teams.

## 5. Open items and decisions

1. ~~Apply the reconciliation~~ — done 2026-10-13 (section 1). Restart any backend still running the old build.
2. **Workbook** (`WORKBOOK_RECONCILIATION.md` §6): category model, ownership, SLA units/targets, two priority conflicts, KYC owner.
3. **6 remaining sub-category values** on single cases (report section 3b) — recategorise by hand.
4. **`Users.Team` / `Users.Queue` columns** are no longer read; dropping them is a destructive migration awaiting approval.
5. **No frontend test framework** — UI verified by browser walkthroughs; adding Vitest needs approval.
6. Cross-team candidates use the **case team's** capacity (documented choice); the manual-assignment policy is **global**, not per team.
7. The Dashboard settings tab still has its own priority rename/toggle card (outside Case Management scope).
8. Reason lists save one list at a time with the policy saved after them; a later failure leaves earlier lists saved (shown to the user).
9. `GET /api/assignment-settings` exposes team rosters to any signed-in user (same as the existing users list); restrict if needed.

## 6. Rollback

Code: revert the working tree changes (careful with files that also carry earlier uncommitted work). Schema:
`dotnet ef database update TypeChangeIdFormatPreferences` removes both new migrations. One-time steps: re-activate "Escalation" in
`ASSIGNMENT_REASON`, delete `ESCALATION_REASON` and the two added filter rows if wanted. Reconciliation (once applied): every moved
case has an audit event naming the previous holder.

## 7. Documentation updated

`DATABASE.md` (reasons, settings, consolidation, masking, reconciliation), `TEAMS_ROUTING.md` (manual vs automatic eligibility),
`HOST_INTEGRATION.md` (`cases.assign`), `WORKBOOK_RECONCILIATION.md` (new), `backend/Data/Reports/assignment-integrity-report.sql`.
