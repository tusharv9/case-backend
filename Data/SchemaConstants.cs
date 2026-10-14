namespace CaseManagement.Api.Data;

/// <summary>
/// Names of database objects that are created by the Baseline migration (see
/// <see cref="SchemaExtras"/>) and referenced from repository code. Kept in one place so the
/// bootstrap DDL and the queries that depend on it can never drift apart.
/// </summary>
public static class SchemaConstants
{
    /// <summary>
    /// PostgreSQL sequence backing the numeric part of <c>Cases.CaseNumber</c>.
    /// Created by the Baseline migration; when an existing database is adopted it is moved
    /// forward to the highest number already present, so there is no hardcoded starting value.
    /// </summary>
    public const string CaseNumberSequence = "case_number_seq";
}
