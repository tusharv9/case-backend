namespace CaseManagement.Api.Data;

/// <summary>
/// Field-metadata defaults that EF's model cannot express, kept in ONE place and applied both by the
/// FieldValidationMetadata migration (existing databases) and by the bootstrap seed (new databases).
/// Idempotent: applying it twice changes nothing the second time.
/// </summary>
public static class FieldMetadataDefaults
{
    /// <summary>
    /// The fields the application cannot function without: a case needs a customer, a department, a type (for its
    /// number prefix), a title and a sub-category (which drives its priority); a customer needs a name and an ID.
    /// Their "Required" setting is locked on. Every OTHER field's Required flag is whatever the administrator chose.
    /// </summary>
    public const string ApplySql = @"
UPDATE ""FieldConfigurations""
   SET ""IsSystemRequired"" = TRUE, ""IsRequired"" = TRUE, ""IsVisible"" = TRUE
 WHERE (""ModuleKey"", ""SectionKey"", ""ApiField"") IN (
        ('CaseManagement', 'CreateCase', 'selectCustomer'),
        ('CaseManagement', 'CreateCase', 'departmentId'),
        ('CaseManagement', 'CreateCase', 'caseType'),
        ('CaseManagement', 'CreateCase', 'title'),
        ('CaseManagement', 'CreateCase', 'subCategory'),
        ('Customer360', 'AddNewCustomer', 'fullName'),
        ('Customer360', 'AddNewCustomer', 'idType'),
        ('Customer360', 'AddNewCustomer', 'idValue'));

-- (The Malaysian phone rule that used to be seeded here now lives in the Countries table.)
";
}
