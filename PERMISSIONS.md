# Case Management permissions (OmniConnect Host)

The Host's role editor grants capabilities; Case Management enforces them in its API on every request. Hiding a
button is a courtesy only. One table — `HostIntegration/HostPermissionCatalog.cs` — feeds both `GET /permissions`
(what the Host offers) and the translation of a token's `perms` claim into the internal permissions the controllers check.

Host format: `remote.casemanagement.<module>:<Capability>`. After changing the catalog, press **Resync Permissions** in
Setup → Applications.

| Feature | Host capability | Internal permission | Backend enforcement | Frontend |
|---|---|---|---|---|
| View cases, reports (incl. CSV export), search | `cases:View` | `cases.read` | `CasesController`, `DashboardController`, `SearchController` | sidebar rows (Host nav) |
| See every team's cases | `cases:ViewAll` | `cases.viewall` | `ICaseAccessScope` on every list; `[CaseAccess]` 404 on single cases | — |
| Create cases | `cases:Create` | `cases.create` | `POST /api/cases` | New Case button, Quick Actions |
| Work cases (status, notes, escalate, co-workers, links, attachments, assign within own teams) | `cases:Edit` | `cases.write` | `CasesController` writes | Assign, Escalate, Link, status menu |
| Assign / reassign any case | `cases:Assign` | `cases.assign` | assignment service (403 + reason) | Assign dialog |
| Transfer to another team | `cases:Transfer` | `cases.transfer` | `PUT /api/cases/{id}/transfer` | Transfer button |
| Resolve, close, cancel, reopen | `cases:Resolve` | `cases.resolve` | resolve / reopen; status → Closed/Cancelled | Resolve, Reopen, Closed/Cancelled in status menu |
| Look up customers (Customer 360, local) | `customers:View` | `customers.read` | `Customer360Controller`, `CustomersController` | Existing Customer |
| Add a new (local) customer | `customers:Edit` | `customers.write` | `POST /api/customers` | + New Customer |
| Manage teams and members | `teams:Manage` | `teams.manage` | `TeamsController` writes, `HostUsersController`, `SkillsController` | Teams page actions |
| Team Monitoring | `monitoring:View` / `monitoring:Nudge` | `monitoring.view` / `monitoring.nudge` | `TeamMonitoringController` | page / Nudge button |
| Case audit trail | `audit:View` | `audit.view` | `AuditController`, case audit | page |
| Case configuration (forms, lists, SLA, routing, assignment) | `settings:Manage` | `config.manage` | configuration controllers | settings pages |
| Unmasked customer data | `pii:Unmask` | `pii.unmask` | `PiiMaskingService` (by name only) | — |

Rules worth knowing:

- **No capability, no access.** An endpoint that names no permission still needs the caller to hold *some* Case
  Management permission. Only `GET /permissions` and `GET /api/users/me` are open (pinned by
  `PermissionEnforcementTests`), so someone without access is told so instead of seeing errors — plus
  `POST /api/customer360/handoff/redeem`, which Customer 360 calls for "Open Customer 360" and which requires the
  Host's `remote.customer360.profile:View` (or `remote.customer360:View`) instead, for the same person who asked, once.
- **Open Customer 360** (`POST /api/cases/{id}/customer360/handoff`, `POST /api/customer360/handoff`) is offered and
  allowed only to people holding that same Customer 360 permission.
- **Case Listing in Customer 360** reads `POST /api/cases/for-customer` with the person's own token: `cases:View`
  plus their team access decide which cases appear.
- **Host administrators** (`administrator: "true"`) get everything except `pii.unmask`, which must be granted by name.
- **Team membership grants no permission.** It only decides which cases a permitted person can see
  (`HostIntegration/CaseAccess.cs`).
- Other external dependencies of Phase 3–4 (no AuthService change needed): listing Host users for teams needs the
  person to hold `host.settings.users:View` in the Host.
