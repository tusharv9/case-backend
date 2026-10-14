# Database: schema, migrations and seed data

## How the database is prepared
The API no longer creates or alters tables while it boots. `DatabaseInitializer` (backend/Data) does it
in the background, so the process answers `/health` immediately:

| Endpoint | Meaning |
|---|---|
| `GET /health` | Process is alive (always fast). |
| `GET /ready` | `200` once the database is migrated and seeded; `503` with `status`/`message` otherwise (`Initializing`, `MigrationRequired`, `Failed`). |
| any `/api/*` | `503` + `Retry-After: 5` until ready (instead of confusing errors from half-built tables). |

Steps: wait for the database (retries while a scale-to-zero database wakes) → adopt a legacy database
**or** apply EF migrations → run due seed steps → ready. The SLA worker waits for readiness.

## Configuration (`Database` section / `Database__*` env vars)
| Key | Default | Meaning |
|---|---|---|
| `MigrateOnStartup` | `true` in Development, `false` elsewhere | Apply migrations at start. When `false` and the schema is out of date the API stays *not ready* and says why. |
| `Seed` | `Development` in Development, `Bootstrap` elsewhere | `None` / `Bootstrap` (required configuration only) / `Development` (adds sample users, teams, customers, routing rules, skills). |
| `ConnectTimeoutSeconds` | `120` | How long to keep retrying an unreachable database. |

Explicit deployment step (exit code `0` on success, `1` on failure):
```
dotnet CaseManagement.Api.dll --migrate-only
```

## Changing the schema
Never edit DDL in `Program.cs` or `LegacySchemaUpgrade` (frozen). Use EF migrations:
```
cd backend
dotnet ef migrations add <Name>        # needs no database; uses DesignTimeDbContextFactory
dotnet ef database update              # optional, against ConnectionStrings__DefaultConnection
```
Things EF cannot model (sequences, extensions, expression/trigram indexes) live in `Data/SchemaExtras.cs`
and are applied by the Baseline migration; add new ones in a new migration with `migrationBuilder.Sql(...)`.

## Existing databases (created by the old startup code)
They have tables but no `__EFMigrationsHistory`. On first start with migration enabled the app **adopts**
them atomically (one transaction): additive upgrade script, convergence to the model (missing FK
indexes/FK; NULLs in columns the model requires are filled with the column default, then NOT NULL),
case-number sequence moved past the highest existing number, Baseline recorded. **No data is dropped
or re-seeded**, and the destructive statements the old bootstrap ran on every start (DROP TABLE/COLUMN,
nulling customer segments) are gone.

## Seed data
Seeding is a list of **one-time, versioned steps** (`DbSeeder.Steps`) recorded in `SeedHistory`.
A step never runs twice, so data an administrator deleted is not re-created on restart. To ship new
defaults add a new step key (e.g. `bootstrap.x.v2`). Dev/sample steps only run in `Development` mode.

## Real-PostgreSQL integration tests
```
TEST_POSTGRES_ADMIN_CONNECTION="Host=127.0.0.1;Port=5432;Username=postgres" dotnet test tests/CaseManagement.Tests
```
They create and drop their own `cmtest_*` databases (never use a real environment's server). Without the
variable they are skipped.

## SLA engine tables (Phase 5)

`BusinessCalendarSettings` (one row: the calendar's IANA time zone), `Notifications.EventKey` (unique per recipient when set), `Cases.SlaReminderSent`. See [SLA_ENGINE.md](SLA_ENGINE.md).

## Teams, routing & monitoring (Phase 6)

`TeamMembers` is the only team membership; `TeamMembers.IsAssignable`, `SkillRules`, one `AssignmentConfigurations` row per team (unique) plus the global row. Dropped: `Departments.Channels`, `TeamMembers.PrimaryChannel`, `RoutingRules.TargetQueueName`. See [TEAMS_ROUTING.md](TEAMS_ROUTING.md).

## Attachments (Phase 7)

`CaseAttachments.ContentHash` (SHA-256, nullable for earlier uploads). Files themselves are not in the database: see [NOTIFICATIONS_ATTACHMENTS.md](NOTIFICATIONS_ATTACHMENTS.md) for storage.

## Phase 8 additions

- `Cases.SlaOutcome` (text, nullable): persisted result of the SLA clock when a case stops (`Met` / `Breached`). Set by `SlaClock.Stop`; existing closed cases are back-filled once at startup by `SlaMonitorService.BackfillOutcomesAsync`. Lets dashboards count breaches in SQL instead of loading cases.
- Partial indexes over **open** cases only (`Status NOT IN ('Resolved','Closed','Cancelled')`): `(OwnerId, Status)` and `(DepartmentId, Status)`, plus an index on breached outcomes. Migrations: `SlaOutcomeAndPerformance`, `OpenCaseIndexes`. Back up the database before the first start on an existing deployment.

## Field configuration, lists and countries (migration `CountriesPhoneAndFieldMetadata`)

- **`FieldConfigurations`**: `IsEditable` and `IsSensitive` were removed. A `MaskingRule` other than `None` now masks on its own
  (the migration sets the rule to `None` where it never took effect because the field was not Sensitive, so nothing newly
  starts masking). `MinValue` / `MaxValue` hold the bounds of Number and Date fields. Which settings apply to which field type
  is defined once, in `FieldDefinitionRules`, and served to the UI by `GET /api/metadata/field-types`.
- **Display order** is unique within a form (`ModuleKey` + `SectionKey`). The API enforces it on every save (new fields and fields
  whose order changed are checked, so old duplicates never block an unrelated save). `UQ_FieldConfigurations_DisplayOrder`
  (DEFERRABLE) is the database backstop; the migration only creates it when no duplicates exist yet and otherwise prints a
  NOTICE — duplicates are never renumbered automatically. Fix them in Configurable Settings; a later migration can add the constraint.
- **`LookupTypes.AllowAdd`**: `false` for `ID_TYPE` (the customer record has a column per supported ID type). A lookup value's
  `Value` cannot be renamed (records store it); its label, order and active flag can.
- **`Countries`**: dial code and national-number length rules (plus an optional pattern) per country; phone validation is data-driven.
  `Customers.PhoneCountryIso2` records the country; `PhoneNumber` keeps the form `+<dial> <digits>`.

## Type changes, ID format rules, preferences (migration `TypeChangeIdFormatPreferences`)

- **Changing a field's type** is allowed for built-in fields too, within what their storage supports (`BuiltInFieldStorage`: e.g.
  Date of Birth is a date column, so only Date; Phone allows Phone or Text). The server also streams every stored value of the field
  through the same validation engine and refuses the change if any would be rejected (`FieldTypeChangeChecker`).
  `POST /api/ConfigurableSettings/fields/{id}/check-type` is the dry run the editor uses.
- **`LookupValues.FormatRule / FormatRegex / FormatMessage`** and **`LookupTypes.UsesFormatRules`**: an ID type option says what its ID
  values must look like (keys in `IdFormatRules`: MY_NRIC, PASSPORT, ACCOUNT_NUMBER, ALPHANUMERIC, REGEX, ANY). A null rule means the
  built-in default for the type's name, so nothing changes until an administrator edits it. The NRIC/date-of-birth cross-check applies only to MY_NRIC.
- **`UserPreferences`** (`UserId`, `Key`, `Value`): per-user UI preferences, e.g. `customer360.view-mode`. `GET/PUT /api/preferences/{key}` only ever touches the caller's own rows.
- Customer list: `sortBy` is limited to a fixed list of columns (`CustomerRepository.SortableColumns`), ties break on `Id`, and trigram indexes cover the searched columns.

## Reasons for assign / transfer / escalate (migration `ReasonDetailRequirement`, seed `bootstrap.escalation-reasons-and-reason-detail.v1`)

- **`LookupValues.RequiresDetail`** (bool, default false): choosing the option needs a typed explanation (e.g. "Other"). The API refuses the
  choice without one (`reasonDetail`, max 500 chars). The rule is data, not a hard-coded option name.
- The reason given for an assignment, transfer or escalation must be an **active** option of `ASSIGNMENT_REASON`, `TRANSFER_REASON` or
  `ESCALATION_REASON` (`Services/CaseReasons.cs`); it is recorded in the case event as "Label — explanation".
- One-time bootstrap step (runs once per database, recorded in `SeedHistory`): creates the `ESCALATION_REASON` list from the values the
  Escalate dialog used to hard-code, marks the existing "Other"/"Others" options RequiresDetail, and **deactivates** (does not delete)
  "Escalation" in `ASSIGNMENT_REASON` — escalating is its own action. Re-activate it in Configurable Settings if wanted.
- A transfer must name a different, active team (a team is the queue; the old free-text "queue" transfer is gone).

## Assign & Reassign settings (migration `ManualAssignmentSettings`)

- **`ManualAssignmentSettings`** — one row (unique index on a constant; `CHECK` on `CrossTeamMode`): `IncludeTeamLeads`, `RequireAvailable`,
  `EnforceCapacity`, `CrossTeamMode` (`Disabled` | `SelectedTeams` | `AllTeams`). The migration inserts it with the behaviour in force
  before it existed (team only, leads included, Available/Busy, below capacity), so applying it changes nothing.
- **`ManualAssignmentCrossTeams`** — the teams whose members may be given other teams' cases when the mode is `SelectedTeams`.
- **`TeamMembers.CanBeManuallyAssigned`** (default true; existing rows true) — whether a member may be CHOSEN for the team's cases.
  Separate from `IsAssignable` (automatic routing). Team edits update member rows in place, so the flag is kept.
- Edited under Configurable Settings → Case Management → Assign & Reassign (`GET/PUT /api/assignment-settings`, saving needs
  `config.manage`). The default routing strategy on that tab is the existing global `AssignmentConfigurations` row, not a copy.
  A save carries the version it was loaded at; a newer save in between returns 409 and nothing is changed.

## Settings consolidation (Phase 5; seed `bootstrap.case-list-filter-fields.v1`, no schema change)

- The Case Management "Master Data & Dropdowns" screen is gone. Its data is unchanged and is edited where it belongs:
  priorities with their SLA targets on Cases SLA & Routing → Priority SLA Matrix (add, activate/deactivate, rename);
  case types and each department's sub-categories in the Create Case field editor; departments on the Teams page;
  channel lists in the field editor (lookup lists); assignment/transfer/escalation reasons in Assign & Reassign.
- Renaming a case type or sub-category now follows the name into `Cases`, routing-rule conditions and exact-match skill rules
  (one transaction). A sub-category name shared by another department is left in rules, which still match that department.
- A priority can no longer be created without all three SLA targets (no hidden 22h/24h/240min defaults).
- The case list's filters are the "Case Filters" field configuration. The seed step adds Priority (`severity`) and Channel
  (`sourceChannel`) filter rows when missing, because the list showed them before they were configurable.

## Masking (Phase 6, no schema change)

- Masking is applied by the API (`PiiMaskingService`), never by the browser; callers with `pii.unmask` see raw values.
- Rules are scoped by module: **Customer 360** field settings govern customer data; **Case Management → Create Case** settings
  govern a case's own fields (custom attributes). Within a module the most restrictive rule across its form sections wins.
- **Filters never mask**: they are search inputs. The API refuses a masking rule on a Filters field and the settings screens no
  longer offer it. (Previously a rule anywhere applied everywhere by field name.)
- Case screens now mask too: case details (custom attributes, the customer's name and the nested customer, sub-cases), the
  case list/board, the audit trail and the dashboard's case lists repeat the customer's name masked.
- Request logs contain method, route, status and timing only (no query strings or bodies).

## Data reconciliation command (Phase 7)

```
dotnet CaseManagement.Api.dll --reconcile-data                                   # dry run: report only, nothing written
dotnet CaseManagement.Api.dll --reconcile-data --apply --actor admin@bank.com    # apply (one transaction; audit events name the actor)
                                                  [--report reconciliation.json]  # also write the report as JSON
```

* Refuses to run when migrations are pending (run `--migrate-only` first); it never changes the schema.
* Fixes automatically: user statuses stored as numbers or unknown text (numbers → their status name, unknown → Offline);
  open cases held by someone who is neither an active member nor the lead of the case's team → the team's active lead, with an
  audit event (SLA clock unchanged); blank sub-category text → no sub-category.
* Reports only (needs a person): cases in inactive teams or teams without an active lead; sub-category text that is not a
  sub-category of the case's team; how each team is used (inactive teams with no references are safe to keep archived);
  remaining legacy `Users.Team` / `Users.Queue` labels (no longer read: a user's teams come from team membership).
* Unrecognised stored statuses now read as Offline (never offered for work) instead of Available.
