namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public sealed record TypeChangeResult(bool Ok, string? Message, int Mismatched, string[]? AllowedTypes);

public interface IFieldTypeChangeChecker
{
    /// <summary>Whether <paramref name="current"/> may become the field described by <paramref name="proposed"/>, given what is stored today.</summary>
    Task<TypeChangeResult> CheckAsync(FieldConfiguration current, FieldConfiguration proposed, CancellationToken ct = default);
}

/// <summary>
/// Changing a field's type is allowed even for built-in fields, but only when (1) the storage behind the field can hold the new
/// type and (2) every value already stored still satisfies the new type and its rules. Values are streamed and checked with the
/// same engine that validates new input, so "compatible" means exactly "would be accepted today".
/// </summary>
public class FieldTypeChangeChecker : IFieldTypeChangeChecker
{
    public const int MismatchLimit = 1000;   // stop counting once it is clearly incompatible

    private readonly AppDbContext _db;
    private readonly IFieldValidationEngine _engine;

    public FieldTypeChangeChecker(AppDbContext db, IFieldValidationEngine engine) { _db = db; _engine = engine; }

    public async Task<TypeChangeResult> CheckAsync(FieldConfiguration current, FieldConfiguration proposed, CancellationToken ct = default)
    {
        var allowed = BuiltInFieldStorage.AllowedTypes(current);
        var records = BuiltInFieldStorage.RecordsName(current);
        if (string.Equals(current.FieldType, proposed.FieldType, StringComparison.OrdinalIgnoreCase))
            return new TypeChangeResult(true, null, 0, allowed);

        if (allowed != null && !allowed.Contains(proposed.FieldType, StringComparer.OrdinalIgnoreCase))
            return new TypeChangeResult(false,
                $"'{current.DisplayLabel}' cannot become {proposed.FieldType}: its value is stored in a way that only supports {string.Join(", ", allowed)}.", 0, allowed);

        var values = BuiltInFieldStorage.StoredValues(_db, current);
        var mismatched = 0;
        if (values != null)
        {
            // Dropdown membership is judged against every option of the list (an inactive option can still be stored on old records).
            await foreach (var value in values.Where(v => v != null && v != "").Distinct().AsAsyncEnumerable().WithCancellation(ct))
            {
                if (await _engine.CheckValueAsync(proposed, value!.Trim(), includeInactiveOptions: true) != null && ++mismatched >= MismatchLimit) break;
            }
        }

        if (mismatched > 0)
            return new TypeChangeResult(false,
                $"Cannot change field type because existing {records} data does not match the selected type. ({(mismatched >= MismatchLimit ? $"{MismatchLimit}+" : mismatched.ToString())} stored value(s) would be rejected.)",
                mismatched, allowed);

        return new TypeChangeResult(true, null, 0, allowed);
    }
}
