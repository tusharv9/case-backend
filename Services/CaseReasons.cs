namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The reason a person gives for assigning, transferring or escalating a case. It must be an ACTIVE option of the action's
/// configured list (ASSIGNMENT_REASON, TRANSFER_REASON, ESCALATION_REASON); an option marked RequiresDetail (e.g. "Other")
/// also needs a typed explanation. The server checks this, so the lists in the UI are a convenience, not the rule.
/// </summary>
public static class CaseReasons
{
    public const string Assignment = "ASSIGNMENT_REASON";
    public const string Transfer = "TRANSFER_REASON";
    public const string Escalation = "ESCALATION_REASON";

    public const int MaxDetailLength = 500;

    /// <summary>The validated reason as it is recorded: the option's label, plus the explanation when one was given.</summary>
    public sealed record Resolved(string Value, string Label, string? Detail)
    {
        public string Text => string.IsNullOrWhiteSpace(Detail) ? Label : $"{Label} — {Detail}";
    }

    public static async Task<Resolved> ResolveAsync(AppDbContext context, string listCode, string? reason, string? detail, CancellationToken ct = default)
    {
        var options = await context.LookupValues.AsNoTracking()
            .Where(v => v.TypeCode == listCode && v.IsActive)
            .Select(v => new { v.Value, v.Label, v.RequiresDetail })
            .ToListAsync(ct);

        if (options.Count == 0)
            throw new InvalidOperationException($"No reasons are configured for this action ({listCode}). An administrator must add them in Configurable Settings.");

        var chosen = reason?.Trim();
        if (string.IsNullOrEmpty(chosen))
            throw new FieldValidationException(new[] { new FieldError("reason", "Choose a reason.") });

        var option = options.FirstOrDefault(o => string.Equals(o.Value, chosen, StringComparison.OrdinalIgnoreCase))
                     ?? options.FirstOrDefault(o => string.Equals(o.Label, chosen, StringComparison.OrdinalIgnoreCase))
                     ?? throw new FieldValidationException(new[] { new FieldError("reason", $"'{chosen}' is not one of the configured reasons. Reload and choose again.") });

        var text = detail?.Trim();
        if (option.RequiresDetail && string.IsNullOrEmpty(text))
            throw new FieldValidationException(new[] { new FieldError("reasonDetail", $"'{option.Label}' needs an explanation.") });
        if (text is { Length: > MaxDetailLength })
            throw new FieldValidationException(new[] { new FieldError("reasonDetail", $"The explanation can be at most {MaxDetailLength} characters.") });

        return new Resolved(option.Value, option.Label, string.IsNullOrEmpty(text) ? null : text);
    }
}
