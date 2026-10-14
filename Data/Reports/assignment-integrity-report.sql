-- Assignment integrity report (READ ONLY). Phase 1 of the Case Management transformation.
--
-- Lists existing data that the assignment rules now enforced by the API would not allow, so it can be reconciled
-- deliberately (Phase 7) instead of being "fixed" silently. It changes nothing: everything runs in a READ ONLY
-- transaction that is rolled back.
--
--   psql "<connection>" -f backend/Data/Reports/assignment-integrity-report.sql

BEGIN TRANSACTION READ ONLY;

-- "Open" = not Resolved / Closed / Cancelled (same definition as the API). Capacity in force per team = the team's own active
-- settings row, else the global one. Both are written inline (CTEs) because a READ ONLY transaction cannot create views.

\echo '1. Open cases whose holder is neither an active member nor the lead of the case''s team'
WITH open_cases AS (SELECT * FROM "Cases" WHERE "Status" NOT IN ('Resolved', 'Closed', 'Cancelled')),
     team_capacity AS (
       SELECT d."Id" AS "DepartmentId",
              COALESCE((SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" = d."Id" AND a."IsActive"),
                       (SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" IS NULL AND a."IsActive")) AS "Capacity"
       FROM "Departments" d)
SELECT d."Code" AS team, o."Name" AS holder,
       (SELECT string_agg(td."Code", ', ' ORDER BY td."Code") FROM "TeamMembers" m JOIN "Departments" td ON td."Id" = m."DepartmentId"
         WHERE m."UserId" = o."Id" AND m."IsActive") AS holder_teams,
       count(*) AS open_cases
FROM open_cases c
JOIN "Departments" d ON d."Id" = c."DepartmentId"
JOIN "Users" o ON o."Id" = c."OwnerId"
WHERE d."OwnerId" IS DISTINCT FROM c."OwnerId"
  AND NOT EXISTS (SELECT 1 FROM "TeamMembers" m WHERE m."UserId" = c."OwnerId" AND m."DepartmentId" = c."DepartmentId" AND m."IsActive")
GROUP BY d."Code", o."Id", o."Name"
ORDER BY open_cases DESC, team;

\echo '2. Open cases held by deactivated users'
WITH open_cases AS (SELECT * FROM "Cases" WHERE "Status" NOT IN ('Resolved', 'Closed', 'Cancelled')),
     team_capacity AS (
       SELECT d."Id" AS "DepartmentId",
              COALESCE((SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" = d."Id" AND a."IsActive"),
                       (SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" IS NULL AND a."IsActive")) AS "Capacity"
       FROM "Departments" d)
SELECT d."Code" AS team, o."Name" AS holder, count(*) AS open_cases
FROM open_cases c JOIN "Users" o ON o."Id" = c."OwnerId" JOIN "Departments" d ON d."Id" = c."DepartmentId"
WHERE NOT o."IsActive"
GROUP BY 1, 2 ORDER BY 3 DESC;

\echo '3. Open cases in inactive teams'
WITH open_cases AS (SELECT * FROM "Cases" WHERE "Status" NOT IN ('Resolved', 'Closed', 'Cancelled')),
     team_capacity AS (
       SELECT d."Id" AS "DepartmentId",
              COALESCE((SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" = d."Id" AND a."IsActive"),
                       (SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" IS NULL AND a."IsActive")) AS "Capacity"
       FROM "Departments" d)
SELECT d."Code" AS team, d."Name", count(*) AS open_cases
FROM open_cases c JOIN "Departments" d ON d."Id" = c."DepartmentId"
WHERE NOT d."IsActive"
GROUP BY 1, 2 ORDER BY 3 DESC;

\echo '4. People holding more open cases than the capacity of a team they hold cases in'
WITH open_cases AS (SELECT * FROM "Cases" WHERE "Status" NOT IN ('Resolved', 'Closed', 'Cancelled')),
     team_capacity AS (
       SELECT d."Id" AS "DepartmentId",
              COALESCE((SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" = d."Id" AND a."IsActive"),
                       (SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" IS NULL AND a."IsActive")) AS "Capacity"
       FROM "Departments" d)
SELECT d."Code" AS team, o."Name" AS holder, count(*) AS open_in_team, tc."Capacity",
       (SELECT count(*) FROM open_cases x WHERE x."OwnerId" = o."Id") AS open_total
FROM open_cases c JOIN "Users" o ON o."Id" = c."OwnerId" JOIN "Departments" d ON d."Id" = c."DepartmentId"
JOIN team_capacity tc ON tc."DepartmentId" = d."Id"
GROUP BY d."Code", o."Id", o."Name", tc."Capacity"
HAVING (SELECT count(*) FROM open_cases x WHERE x."OwnerId" = o."Id") >= tc."Capacity"
ORDER BY open_total DESC, team;

\echo '5. Active teams: lead, members who could be chosen manually / automatically right now'
WITH open_cases AS (SELECT * FROM "Cases" WHERE "Status" NOT IN ('Resolved', 'Closed', 'Cancelled')),
     team_capacity AS (
       SELECT d."Id" AS "DepartmentId",
              COALESCE((SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" = d."Id" AND a."IsActive"),
                       (SELECT a."MaxConcurrentCapacity" FROM "AssignmentConfigurations" a WHERE a."DepartmentId" IS NULL AND a."IsActive")) AS "Capacity"
       FROM "Departments" d)
SELECT d."Code" AS team, d."Name",
       (SELECT u."Name" || CASE WHEN u."IsActive" THEN '' ELSE ' (deactivated)' END FROM "Users" u WHERE u."Id" = d."OwnerId") AS lead,
       (SELECT count(*) FROM "TeamMembers" m JOIN "Users" u ON u."Id" = m."UserId"
         WHERE m."DepartmentId" = d."Id" AND m."IsActive" AND u."IsActive" AND u."Status" IN ('Available', 'Busy')) AS online_members,
       (SELECT count(*) FROM "TeamMembers" m JOIN "Users" u ON u."Id" = m."UserId"
         WHERE m."DepartmentId" = d."Id" AND m."IsActive" AND m."IsAssignable" AND u."IsActive" AND u."Status" IN ('Available', 'Busy')) AS online_auto_assignable,
       tc."Capacity"
FROM "Departments" d JOIN team_capacity tc ON tc."DepartmentId" = d."Id"
WHERE d."IsActive"
ORDER BY d."Code";

\echo '6. Users whose stored status is not one of Available / Busy / Away / Offline'
SELECT "Status" AS stored_status, count(*) AS users
FROM "Users" WHERE "Status" NOT IN ('Available', 'Busy', 'Away', 'Offline')
GROUP BY 1;

\echo '7. Legacy free-text Users.Team / Users.Queue that match none of the user''s team memberships'
SELECT count(*) AS users_with_stale_team_label
FROM "Users" u
WHERE u."Team" IS NOT NULL AND u."Team" <> ''
  AND NOT EXISTS (SELECT 1 FROM "TeamMembers" m JOIN "Departments" d ON d."Id" = m."DepartmentId"
                  WHERE m."UserId" = u."Id" AND m."IsActive" AND lower(d."Name") = lower(u."Team"));

ROLLBACK;
