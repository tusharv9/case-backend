namespace CaseManagement.Api.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.EntityFrameworkCore;

public class ConfigurableSettingsService : IConfigurableSettingsService
{
    private const int MaxSlaHours = 8760; // one year

    private readonly IConfigurableSettingsRepository _repository;
    private readonly AppDbContext _context;
    private readonly ILogger<ConfigurableSettingsService> _logger;
    private readonly INotificationService _notificationService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IFieldTypeChangeChecker _typeChecker;

    public ConfigurableSettingsService(IConfigurableSettingsRepository repository, AppDbContext context, INotificationService notificationService, IHttpContextAccessor httpContextAccessor, ILogger<ConfigurableSettingsService>? logger = null, IFieldTypeChangeChecker? typeChecker = null)
    {
        _typeChecker = typeChecker ?? new FieldTypeChangeChecker(context, new FieldValidationEngine(context, new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())), NullLogger<FieldValidationEngine>.Instance));
        _repository = repository;
        _logger = logger ?? NullLogger<ConfigurableSettingsService>.Instance;
        _context = context;
        _notificationService = notificationService;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>The user making the current request (set by UserAuthorizationMiddleware).</summary>
    private Guid? CurrentUserId =>
        _httpContextAccessor.HttpContext?.Items.TryGetValue("UserId", out var value) == true && value is Guid id ? id : null;

    private async Task RecordAuditLogAsync(string actionType, string entityName, string description, string? oldValue, string? newValue, CancellationToken ct)
    {
        try
        {
            // The audit row names the person who made the change.
            var userId = CurrentUserId;
            if (userId is null)
            {
                _logger.LogWarning("Configuration audit entry for {Entity} skipped: no acting user on the request.", entityName);
                return;
            }

            var audit = new CaseEvent
            {
                Id = Guid.NewGuid(),
                CaseId = null,
                EventType = EventType.Other,
                Message = description,
                CreatedAt = DateTime.UtcNow,
                UserId = userId.Value,
                Module = "Configurable Settings",
                EntityName = entityName,
                ActionType = actionType,
                OldValue = oldValue,
                NewValue = newValue
            };

            _context.CaseEvents.Add(audit);
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConfigAuditLog Error");
        }
    }

    /// <summary>Built-in field keys of the Create Case and Add Customer forms; a custom field may not reuse them.</summary>
    private static readonly HashSet<string> ReservedFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "caseType", "title", "description", "selectCustomer", "departmentId", "subCategory", "severity", "sourceChannel",
        "preferredLanguage", "preferredCommunicationChannel", "communicationChannel", "customerId",
        "fullName", "idType", "idValue", "nric", "passport", "accountNumber", "dateOfBirth", "phoneNumber", "email", "branch", "customerSegment"
    };

    private static FieldDefinitionRules.Definition DefinitionOf(string label, string fieldType, string? maskingRule, int visibleChars, int displayOrder,
        string? regex, int? minLength, int? maxLength, string? minValue, string? maxValue, string? lookup) =>
        new(label, fieldType, maskingRule ?? "None", visibleChars, displayOrder, regex, minLength, maxLength, minValue, maxValue, lookup);

    /// <summary>
    /// Checks a field definition at SAVE time, so a typo in a pattern, an unknown list or a settings combination the
    /// field type cannot honour is an immediate, clear error for the administrator instead of a silently ignored rule later.
    /// </summary>
    private async Task<List<FieldError>> CheckDefinitionAsync(FieldDefinitionRules.Definition d, CancellationToken ct, string? prefix = null)
    {
        var errors = FieldDefinitionRules.Check(d);
        var type = FieldDefinitionRules.CanonicalType(d.FieldType);
        if (type != null && FieldDefinitionRules.ByType[type].Lookup && !string.IsNullOrWhiteSpace(d.LookupTypeCode))
        {
            var known = await _context.LookupTypes.AsNoTracking().AnyAsync(t => t.Code == d.LookupTypeCode, ct);
            if (!known) errors.Add(new FieldError("lookupTypeCode", $"The list '{d.LookupTypeCode}' does not exist."));
        }
        return prefix == null ? errors : errors.Select(e => e with { Message = $"{prefix}: {e.Message}" }).ToList();
    }

    /// <summary>
    /// A field may not take a display order another field in the same form already has (a form needs a deterministic
    /// order). Only rows that are NEW or whose order CHANGED are checked, so old duplicates among untouched fields never
    /// block an unrelated save — they are reported the first time someone edits one of them.
    /// </summary>
    private static void CheckDisplayOrders(IReadOnlyList<(Guid Id, string Key, string Label, int Order, bool Touched)> final, List<FieldError> errors)
    {
        foreach (var row in final.Where(r => r.Touched))
        {
            if (final.Any(o => o.Id != row.Id && o.Order == row.Order))
                errors.Add(new FieldError(row.Key, string.Format(FieldDefinitionRules.DisplayOrderMessage, row.Order)));
        }
    }

    /// <summary>
    /// Filters are search inputs, not displayed data: a masking rule there would mean nothing (and used to leak into other screens'
    /// masking). Masking is configured on the forms that show the data (Create Case for case fields, Customer 360 for customers).
    /// </summary>
    private static string? MaskingOnFilter(string sectionKey, string? maskingRule) =>
        string.Equals(sectionKey, "Filters", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(maskingRule) && maskingRule != "None"
            ? "Masking does not apply to filters. Set it on the form that shows this data instead."
            : null;

    private static FieldConfiguration NewEntity(CreateCustomFieldDto dto, string moduleKey, string sectionKey)
    {
        var def = DefinitionOf(dto.DisplayLabel, dto.FieldType, dto.MaskingRule, dto.VisibleChars, dto.DisplayOrder,
            dto.ValidationRegex, dto.MinLength, dto.MaxLength, dto.MinValue, dto.MaxValue, dto.LookupTypeCode);
        var n = FieldDefinitionRules.Normalize(def, dto.ValidationMessage);
        return new FieldConfiguration
        {
            Id = Guid.NewGuid(),
            ModuleKey = moduleKey,
            SectionKey = sectionKey,
            ApiField = string.IsNullOrWhiteSpace(dto.ApiField) ? SanitizeApiField(dto.DisplayLabel) : dto.ApiField.Trim(),
            DisplayLabel = dto.DisplayLabel.Trim(),
            FieldType = FieldDefinitionRules.CanonicalType(dto.FieldType)!,
            IsVisible = dto.IsVisible,
            IsRequired = dto.IsRequired,
            MaskingRule = n.MaskingRule,
            VisibleChars = n.VisibleChars,
            DisplayOrder = dto.DisplayOrder,
            LookupTypeCode = n.Lookup,
            ValidationRegex = n.Regex,
            ValidationMessage = n.Message,
            MinLength = n.Min,
            MaxLength = n.Max,
            MinValue = n.MinValue,
            MaxValue = n.MaxValue,
            IsCustomField = true
        };
    }

    /// <summary>The key becomes a property name in API payloads and a column value key: keep it a plain identifier, and never let it shadow a built-in field.</summary>
    private static FieldError? CheckNewKey(FieldConfiguration entity)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entity.ApiField, @"^[A-Za-z][A-Za-z0-9_]{0,49}$"))
            return new FieldError("displayLabel", $"'{entity.ApiField}' is not a valid field key. Use letters, digits and underscores, starting with a letter (max 50).");
        if (ReservedFieldKeys.Contains(entity.ApiField))
            return new FieldError("displayLabel", $"'{entity.ApiField}' is a built-in field name and cannot be used for a custom field.");
        return null;
    }

    private static string Summarize(FieldConfiguration f) =>
        $"Label: {f.DisplayLabel}, Type: {f.FieldType}, Visible: {f.IsVisible}, Required: {f.IsRequired}, Order: {f.DisplayOrder}, Masking: {f.MaskingRule}" +
        (f.LookupTypeCode != null ? $", List: {f.LookupTypeCode}" : "") +
        (f.ValidationRegex != null ? $", Pattern: {f.ValidationRegex}" : "") +
        (f.MinLength != null || f.MaxLength != null ? $", Length: {f.MinLength}-{f.MaxLength}" : "") +
        (f.MinValue != null || f.MaxValue != null ? $", Range: {f.MinValue}..{f.MaxValue}" : "");

    public async Task<IEnumerable<FieldConfigurationDto>> GetFieldConfigurationsAsync(string moduleKey, string? sectionKey = null, CancellationToken ct = default)
    {
        var fields = await _repository.GetFieldConfigurationsAsync(moduleKey, sectionKey, ct);
        return fields.Select(MapToDto);
    }

    public async Task<IReadOnlyList<FieldConfigurationDto>> SaveFieldConfigurationsAsync(UpdateFieldConfigurationsRequest request, CancellationToken ct = default)
    {
        var moduleKey = request.ModuleKey;
        var sectionKey = request.SectionKey;
        var errors = new List<FieldError>();
        var existing = (await _repository.GetFieldConfigurationsAsync(moduleKey, sectionKey, ct)).ToList();

        var updates = new List<FieldConfiguration>();
        var final = existing.ToDictionary(e => e.Id, e => (Key: e.ApiField, Label: e.DisplayLabel, Order: e.DisplayOrder, Touched: false));

        foreach (var dto in request.Update)
        {
            var current = existing.FirstOrDefault(e => e.Id == dto.Id);
            var key = string.IsNullOrWhiteSpace(dto.ApiField) ? dto.DisplayLabel : dto.ApiField;
            if (current == null)
            {
                errors.Add(new FieldError(key, $"'{dto.DisplayLabel}' no longer exists in this form. Reload the page and try again."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(dto.DisplayLabel))
            {
                errors.Add(new FieldError(key, $"{current.ApiField}: a display label is required."));
                continue;
            }

            var fieldType = string.IsNullOrWhiteSpace(dto.FieldType) ? current.FieldType : dto.FieldType;
            var def = DefinitionOf(dto.DisplayLabel, fieldType, dto.MaskingRule, dto.VisibleChars, dto.DisplayOrder,
                dto.ValidationRegex, dto.MinLength, dto.MaxLength, dto.MinValue, dto.MaxValue, dto.LookupTypeCode);
            var defErrors = await CheckDefinitionAsync(def, ct, dto.DisplayLabel);
            if (defErrors.Count > 0) { errors.AddRange(defErrors.Select(e => e with { Field = key })); continue; }
            if (MaskingOnFilter(current.SectionKey, dto.MaskingRule) is { } filterMasking) { errors.Add(new FieldError(key, filterMasking)); continue; }

            var n = FieldDefinitionRules.Normalize(def, dto.ValidationMessage);

            // A different type is only allowed if the storage can hold it and every stored value still fits.
            if (!string.Equals(current.FieldType, fieldType, StringComparison.OrdinalIgnoreCase))
            {
                var verdict = await _typeChecker.CheckAsync(current, ProposedFrom(current, dto, fieldType, n), ct);
                if (!verdict.Ok) { errors.Add(new FieldError(key, verdict.Message!)); continue; }
            }

            updates.Add(new FieldConfiguration
            {
                Id = current.Id,
                DisplayLabel = dto.DisplayLabel.Trim(),
                FieldType = FieldDefinitionRules.CanonicalType(fieldType)!,
                IsVisible = dto.IsVisible,
                IsRequired = dto.IsRequired,
                MaskingRule = n.MaskingRule,
                VisibleChars = n.VisibleChars,
                DisplayOrder = dto.DisplayOrder,
                LookupTypeCode = n.Lookup,
                ValidationRegex = n.Regex,
                ValidationMessage = n.Message,
                MinLength = n.Min,
                MaxLength = n.Max,
                MinValue = n.MinValue,
                MaxValue = n.MaxValue
            });
            final[current.Id] = (current.ApiField, dto.DisplayLabel.Trim(), dto.DisplayOrder, dto.DisplayOrder != current.DisplayOrder);
        }

        var creates = new List<FieldConfiguration>();
        foreach (var dto in request.Create)
        {
            var label = dto.DisplayLabel?.Trim() ?? string.Empty;
            if (label.Length == 0) { errors.Add(new FieldError("displayLabel", "A new field needs a display label.")); continue; }
            dto.DisplayLabel = label;

            var def = DefinitionOf(label, dto.FieldType, dto.MaskingRule, dto.VisibleChars, dto.DisplayOrder,
                dto.ValidationRegex, dto.MinLength, dto.MaxLength, dto.MinValue, dto.MaxValue, dto.LookupTypeCode);
            var defErrors = await CheckDefinitionAsync(def, ct, label);
            if (defErrors.Count > 0) { errors.AddRange(defErrors.Select(e => e with { Field = label })); continue; }
            if (MaskingOnFilter(sectionKey, dto.MaskingRule) is { } filterMasking) { errors.Add(new FieldError(label, filterMasking)); continue; }

            var entity = NewEntity(dto, moduleKey, sectionKey);
            var keyError = CheckNewKey(entity);
            if (keyError != null) { errors.Add(keyError with { Field = label }); continue; }
            if (existing.Any(e => e.ApiField.Equals(entity.ApiField, StringComparison.OrdinalIgnoreCase)) ||
                creates.Any(c => c.ApiField.Equals(entity.ApiField, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new FieldError(label, $"A field with the key '{entity.ApiField}' already exists in this form. Use a different label."));
                continue;
            }
            creates.Add(entity);
            final[entity.Id] = (entity.ApiField, label, entity.DisplayOrder, true);
        }

        CheckDisplayOrders(final.Select(kv => (kv.Key, kv.Value.Key, kv.Value.Label, kv.Value.Order, kv.Value.Touched)).ToList(), errors);
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var before = existing.ToDictionary(e => e.Id, Summarize);
        await _repository.SaveFieldConfigurationsAsync(moduleKey, sectionKey, updates, creates, ct);

        var after = (await _repository.GetFieldConfigurationsAsync(moduleKey, sectionKey, ct)).ToList();
        foreach (var f in after)
        {
            if (!before.TryGetValue(f.Id, out var old))
                await RecordAuditLogAsync("CREATE", $"Field: {f.DisplayLabel}", $"Created custom configurable field '{f.DisplayLabel}' ({f.ApiField}) in section {sectionKey}", null, Summarize(f), ct);
            else if (old != Summarize(f))
                await RecordAuditLogAsync("UPDATE", $"Field: {f.DisplayLabel}", $"Updated field configuration '{f.DisplayLabel}' in section {sectionKey}", old, Summarize(f), ct);
        }
        return after.Select(MapToDto).ToList();
    }

    private static FieldConfiguration ProposedFrom(FieldConfiguration current, FieldConfigurationDto dto, string fieldType,
        (string? Regex, string? Message, int? Min, int? Max, string? MinValue, string? MaxValue, string? Lookup, string MaskingRule, int VisibleChars) n) => new()
    {
        Id = current.Id, ModuleKey = current.ModuleKey, SectionKey = current.SectionKey, ApiField = current.ApiField, IsCustomField = current.IsCustomField,
        DisplayLabel = dto.DisplayLabel.Trim(), FieldType = FieldDefinitionRules.CanonicalType(fieldType)!, IsRequired = dto.IsRequired, IsVisible = dto.IsVisible,
        ValidationRegex = n.Regex, ValidationMessage = n.Message, MinLength = n.Min, MaxLength = n.Max, MinValue = n.MinValue, MaxValue = n.MaxValue, LookupTypeCode = n.Lookup
    };

    /// <summary>Dry run for the editor: would this field be accepted with the proposed type and rules? (The save runs the same check.)</summary>
    public async Task<TypeChangeResult?> CheckTypeChangeAsync(Guid id, FieldConfigurationDto proposal, CancellationToken ct = default)
    {
        var current = await _repository.GetFieldConfigurationAsync(id, ct);
        if (current == null) return null;
        var type = string.IsNullOrWhiteSpace(proposal.FieldType) ? current.FieldType : proposal.FieldType;
        if (FieldDefinitionRules.CanonicalType(type) == null)
            return new TypeChangeResult(false, $"'{type}' is not a valid field type.", 0, BuiltInFieldStorage.AllowedTypes(current));
        proposal.DisplayLabel = string.IsNullOrWhiteSpace(proposal.DisplayLabel) ? current.DisplayLabel : proposal.DisplayLabel;
        var def = DefinitionOf(proposal.DisplayLabel, type, proposal.MaskingRule, proposal.VisibleChars, proposal.DisplayOrder,
            proposal.ValidationRegex, proposal.MinLength, proposal.MaxLength, proposal.MinValue, proposal.MaxValue, proposal.LookupTypeCode);
        return await _typeChecker.CheckAsync(current, ProposedFrom(current, proposal, type, FieldDefinitionRules.Normalize(def, proposal.ValidationMessage)), ct);
    }

    public async Task<FieldConfigurationDto> AddCustomFieldAsync(CreateCustomFieldDto dto, CancellationToken ct = default)
    {
        var module = string.IsNullOrWhiteSpace(dto.ModuleKey) ? "Customer360" : dto.ModuleKey;
        var section = string.IsNullOrWhiteSpace(dto.SectionKey) ? "AddNewCustomer" : dto.SectionKey;
        var saved = await SaveFieldConfigurationsAsync(new UpdateFieldConfigurationsRequest { ModuleKey = module, SectionKey = section, Create = new() { dto } }, ct);
        var key = string.IsNullOrWhiteSpace(dto.ApiField) ? SanitizeApiField(dto.DisplayLabel.Trim()) : dto.ApiField.Trim();
        return saved.First(f => f.ApiField.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FieldConfigurationDto?> UpdateFieldConfigurationAsync(Guid id, UpdateFieldConfigurationDto dto, CancellationToken ct = default)
    {
        var existing = await _repository.GetFieldConfigurationAsync(id, ct);
        if (existing == null) return null;

        var fieldType = string.IsNullOrWhiteSpace(dto.FieldType) ? existing.FieldType : dto.FieldType;
        var saved = await SaveFieldConfigurationsAsync(new UpdateFieldConfigurationsRequest
        {
            ModuleKey = existing.ModuleKey,
            SectionKey = existing.SectionKey,
            Update = new()
            {
                new FieldConfigurationDto
                {
                    Id = id, ApiField = existing.ApiField, DisplayLabel = dto.DisplayLabel, FieldType = fieldType,
                    IsVisible = dto.IsVisible, IsRequired = dto.IsRequired, MaskingRule = dto.MaskingRule, VisibleChars = dto.VisibleChars,
                    DisplayOrder = dto.DisplayOrder, LookupTypeCode = dto.LookupTypeCode, ValidationRegex = dto.ValidationRegex,
                    ValidationMessage = dto.ValidationMessage, MinLength = dto.MinLength, MaxLength = dto.MaxLength,
                    MinValue = dto.MinValue, MaxValue = dto.MaxValue
                }
            }
        }, ct);
        return saved.FirstOrDefault(f => f.Id == id);
    }

    public async Task<bool> DeleteFieldConfigurationAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _repository.GetFieldConfigurationAsync(id, ct);
        if (existing is { IsSystemRequired: true })
            throw new InvalidOperationException($"'{existing.DisplayLabel}' is a built-in field the system needs and cannot be deleted.");
        var oldVal = existing != null ? $"Label: {existing.DisplayLabel}, ApiField: {existing.ApiField}, Section: {existing.SectionKey}, Type: {existing.FieldType}" : $"ID: {id}";
        var fieldName = existing?.DisplayLabel ?? "Field";

        var success = await _repository.DeleteFieldConfigurationAsync(id, ct);
        if (success)
        {
            await RecordAuditLogAsync("DELETE", $"Field: {fieldName}", $"Deleted configurable field '{fieldName}'", oldVal, null, ct);
        }
        return success;
    }

    public async Task<IEnumerable<LookupTypeDto>> GetLookupTypesAsync(CancellationToken ct = default)
    {
        var types = await _context.LookupTypes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        return types.Select(t => new LookupTypeDto { Code = t.Code, Name = t.Name, Description = t.Description, AllowAdd = t.AllowAdd, UsesFormatRules = t.UsesFormatRules });
    }

    public async Task<IEnumerable<LookupValueDto>> GetLookupValuesAsync(string typeCode, bool activeOnly = true, CancellationToken ct = default)
    {
        var values = await _repository.GetLookupValuesAsync(typeCode, activeOnly, ct);
        return values.Select(MapLookupDto);
    }

    public async Task<LookupValueDto> AddLookupValueAsync(CreateLookupValueDto dto, CancellationToken ct = default)
    {
        var entity = new LookupValue
        {
            TypeCode = dto.TypeCode,
            Value = dto.Value.Trim(),
            Label = string.IsNullOrWhiteSpace(dto.Label) ? dto.Value.Trim() : dto.Label.Trim(),
            DisplayOrder = dto.DisplayOrder,
            IsActive = true
        };

        if (string.IsNullOrWhiteSpace(entity.Value))
            throw new InvalidOperationException("Value is required.");

        await EnsureCanAddAsync(entity.TypeCode, ct);

        if (await _repository.LookupValueExistsAsync(entity.TypeCode, entity.Value, null, ct))
            throw new InvalidOperationException($"'{entity.Value}' already exists in this list.");

        var created = await _repository.AddLookupValueAsync(entity, ct);
        await RecordAuditLogAsync("CREATE", $"Lookup: {created.Label} ({created.TypeCode})", $"Added lookup item '{created.Label}' for category '{created.TypeCode}'", null, $"Value: {created.Value}, TypeCode: {created.TypeCode}", ct);
        return MapLookupDto(created);
    }

    public async Task<LookupValueDto?> UpdateLookupValueAsync(Guid id, UpdateLookupValueDto dto, CancellationToken ct = default)
    {
        var entity = new LookupValue
        {
            Value = dto.Value.Trim(),
            Label = string.IsNullOrWhiteSpace(dto.Label) ? dto.Value.Trim() : dto.Label.Trim(),
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        var current = await _repository.GetLookupValueAsync(id, ct);
        if (current == null) return null;

        // Records store the VALUE itself (a customer keeps "English", not a reference to a row), so changing it would
        // strand every record that already uses it. The label, order and active flag are freely editable.
        if (!string.Equals(current.Value, entity.Value, StringComparison.Ordinal))
            throw new InvalidOperationException($"The value '{current.Value}' cannot be renamed because existing records store it. Edit its label instead, or disable it and add a new option.");

        var oldVal = $"Label: {current.Label}, Value: {current.Value}, Active: {current.IsActive}";

        if (await _repository.LookupValueExistsAsync(current.TypeCode, entity.Value, id, ct))
            throw new InvalidOperationException($"'{entity.Value}' already exists in this list.");

        var updated = await _repository.UpdateLookupValueAsync(id, entity, ct);
        if (updated != null)
        {
            var newVal = $"Label: {updated.Label}, Value: {updated.Value}, Active: {updated.IsActive}";
            await RecordAuditLogAsync("UPDATE", $"Lookup: {updated.Label} ({updated.TypeCode})", $"Updated lookup item '{updated.Label}' for category '{updated.TypeCode}'", oldVal, newVal, ct);
        }
        return updated == null ? null : MapLookupDto(updated);
    }

    private async Task EnsureCanDeleteAsync(string typeCode, CancellationToken ct)
    {
        var type = await _context.LookupTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Code == typeCode, ct);
        if (type is { AllowAdd: false })
            throw new InvalidOperationException($"Options of '{type.Name}' cannot be deleted: the system supports a fixed set. Switch the option off instead.");
    }

    private async Task EnsureCanAddAsync(string typeCode, CancellationToken ct)
    {
        var type = await _context.LookupTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Code == typeCode, ct);
        if (type is { AllowAdd: false })
            throw new InvalidOperationException($"New options cannot be added to '{type.Name}': the system supports a fixed set. You can switch the existing ones on or off.");
    }

    /// <summary>
    /// Saves every pending option change of one list together: all of it is applied, or none of it. This is what the
    /// field drawer's "manage options" panel calls, so a list can be edited from the field that uses it without a
    /// separate Master Lookup screen being the only place that knows how.
    /// </summary>
    public async Task<IReadOnlyList<LookupValueDto>> SaveLookupValuesAsync(string typeCode, SaveLookupValuesRequest request, CancellationToken ct = default)
    {
        string? createdListNote = null;
        var type = await _context.LookupTypes.FirstOrDefaultAsync(t => t.Code == typeCode, ct);
        if (type == null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new KeyNotFoundException($"The list '{typeCode}' does not exist.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(typeCode, @"^[A-Z][A-Z0-9_]{1,59}$"))
                throw new FieldValidationException(new[] { new FieldError("name", "A list's code must be 2-60 capital letters, digits or underscores, starting with a letter.") });
            type = new LookupType { Id = Guid.NewGuid(), Code = typeCode, Name = request.Name.Trim(), Description = $"Options of {request.Name.Trim()}", AllowAdd = true, CreatedAt = DateTime.UtcNow };
            _context.LookupTypes.Add(type);
            createdListNote = $"Created list '{type.Name}' ({type.Code})";
        }
        var current = await _context.LookupValues.Where(v => v.LookupTypeId == type.Id).ToListAsync(ct);
        var errors = new List<FieldError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<(string Action, string Text, string? Old, string? New)>();

        foreach (var draft in request.Values)
        {
            var value = draft.Value?.Trim() ?? string.Empty;
            var label = string.IsNullOrWhiteSpace(draft.Label) ? value : draft.Label.Trim();
            var existing = draft.Id.HasValue ? current.FirstOrDefault(v => v.Id == draft.Id.Value) : null;

            if (draft.Id.HasValue && existing == null) { errors.Add(new FieldError(label, $"'{label}' no longer exists. Reload and try again.")); continue; }
            if (existing != null) value = existing.Value;   // the stored value is immutable; see UpdateLookupValueAsync
            if (value.Length == 0) { errors.Add(new FieldError("value", "Every option needs a value.")); continue; }
            if (!seen.Add(value)) { errors.Add(new FieldError(value, $"'{value}' appears twice in this list.")); continue; }

            // Format rule (ID-type lists only): which kind of ID value this option accepts.
            string? formatRule = null, formatRegex = null, formatMessage = null;
            if (type.UsesFormatRules)
            {
                formatRule = string.IsNullOrWhiteSpace(draft.FormatRule) ? null : draft.FormatRule.Trim();
                formatRegex = formatRule == IdFormatRules.Regex && !string.IsNullOrWhiteSpace(draft.FormatRegex) ? draft.FormatRegex.Trim() : null;
                formatMessage = string.IsNullOrWhiteSpace(draft.FormatMessage) ? null : draft.FormatMessage.Trim();
                var ruleError = IdFormatRules.CheckDefinition(formatRule, formatRegex);
                if (ruleError != null) { errors.Add(new FieldError(value, $"{label}: {ruleError}")); continue; }
            }

            if (existing == null)
            {
                if (!type.AllowAdd) { errors.Add(new FieldError(value, $"New options cannot be added to '{type.Name}': the system supports a fixed set.")); continue; }
                var added = new LookupValue
                {
                    Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = type.Code, Value = value, Label = label,
                    DisplayOrder = draft.DisplayOrder, IsActive = draft.IsActive, CreatedAt = DateTime.UtcNow,
                    FormatRule = formatRule, FormatRegex = formatRegex, FormatMessage = formatMessage,
                    RequiresDetail = draft.RequiresDetail ?? false
                };
                _context.LookupValues.Add(added);
                changes.Add(("CREATE", $"Added option '{label}' to list '{type.Name}'", null, $"Value: {value}, Active: {draft.IsActive}"));
            }
            else if (existing.Label != label || existing.DisplayOrder != draft.DisplayOrder || existing.IsActive != draft.IsActive
                     || (draft.RequiresDetail.HasValue && existing.RequiresDetail != draft.RequiresDetail.Value)
                     || (type.UsesFormatRules && (existing.FormatRule != formatRule || existing.FormatRegex != formatRegex || existing.FormatMessage != formatMessage)))
            {
                changes.Add(("UPDATE", $"Updated option '{existing.Label}' of list '{type.Name}'",
                    $"Label: {existing.Label}, Order: {existing.DisplayOrder}, Active: {existing.IsActive}, Needs detail: {existing.RequiresDetail}",
                    $"Label: {label}, Order: {draft.DisplayOrder}, Active: {draft.IsActive}, Needs detail: {draft.RequiresDetail ?? existing.RequiresDetail}"));
                existing.Label = label;
                existing.DisplayOrder = draft.DisplayOrder;
                existing.IsActive = draft.IsActive;
                if (draft.RequiresDetail.HasValue) existing.RequiresDetail = draft.RequiresDetail.Value;
                if (type.UsesFormatRules) { existing.FormatRule = formatRule; existing.FormatRegex = formatRegex; existing.FormatMessage = formatMessage; }
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (errors.Count > 0) throw new FieldValidationException(errors);
        await _context.SaveChangesAsync(ct);
        if (createdListNote != null) await RecordAuditLogAsync("CREATE", $"Lookup list: {type.Name}", createdListNote, null, null, ct);
        foreach (var c in changes) await RecordAuditLogAsync(c.Action, $"Lookup: {type.Name}", c.Text, c.Old, c.New, ct);

        var all = await _repository.GetLookupValuesAsync(typeCode, activeOnly: false, ct);
        return all.Select(MapLookupDto).ToList();
    }

    public async Task<bool> DeleteLookupValueAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await _repository.GetLookupValueAsync(id, ct);
        if (existing != null) await EnsureCanDeleteAsync(existing.TypeCode, ct);
        var oldVal = existing != null ? $"Label: {existing.Label}, Value: {existing.Value}, TypeCode: {existing.TypeCode}" : $"ID: {id}";
        var label = existing?.Label ?? existing?.Value ?? "Lookup Item";

        var success = await _repository.DeleteLookupValueAsync(id, ct);
        if (success)
        {
            await RecordAuditLogAsync("DELETE", $"Lookup: {label}", $"Deleted lookup item '{label}' from configurable settings", oldVal, null, ct);
        }
        return success;
    }

    private static string SanitizeApiField(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "customField_" + Guid.NewGuid().ToString("N")[..8];
        var cleaned = new string(label.Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (string.IsNullOrEmpty(cleaned)) return "customField_" + Guid.NewGuid().ToString("N")[..8];
        return char.ToLowerInvariant(cleaned[0]) + cleaned[1..];
    }

    private static FieldConfigurationDto MapToDto(FieldConfiguration entity) => FieldConfigurationDto.From(entity);

    private static LookupValueDto MapLookupDto(LookupValue entity)
    {
        return new LookupValueDto
        {
            Id = entity.Id,
            LookupTypeId = entity.LookupTypeId,
            TypeCode = entity.TypeCode,
            Value = entity.Value,
            Label = entity.Label,
            DisplayOrder = entity.DisplayOrder,
            IsActive = entity.IsActive,
            FormatRule = entity.FormatRule,
            FormatRegex = entity.FormatRegex,
            FormatMessage = entity.FormatMessage,
            RequiresDetail = entity.RequiresDetail
        };
    }

    // Case Management Settings Implementation
    public async Task<IEnumerable<CaseTypeConfigDto>> GetCaseTypesAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var items = await _repository.GetCaseTypesAsync(activeOnly, ct);
        return items.Select(c => new CaseTypeConfigDto
        {
            Id = c.Id,
            Code = c.Code,
            Name = c.Name,
            Prefix = c.Prefix,
            DisplayOrder = c.DisplayOrder,
            IsActive = c.IsActive
        });
    }

    public async Task<CaseTypeConfigDto> AddCaseTypeAsync(CreateCaseTypeConfigDto dto, CancellationToken ct = default)
    {
        var entity = new CaseTypeConfig
        {
            Code = dto.Code.Trim(),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Code.Trim() : dto.Name.Trim(),
            Prefix = string.IsNullOrWhiteSpace(dto.Prefix) ? "C-" : dto.Prefix.Trim().ToUpper(),
            DisplayOrder = dto.DisplayOrder,
            IsActive = true
        };

        if (await _repository.CaseTypeExistsAsync(entity.Code, entity.Name, null, ct))
            throw new InvalidOperationException($"A case type with the code '{entity.Code}' or name '{entity.Name}' already exists.");

        var created = await _repository.AddCaseTypeAsync(entity, ct);
        await RecordAuditLogAsync("CREATE", $"Case Type: {created.Name}", $"Created case type '{created.Name}' ({created.Code})", null, $"Name: {created.Name}, Code: {created.Code}, Prefix: {created.Prefix}", ct);
        await _notificationService.CreateConfigChangedNotificationAsync("System Setting Updated", $"Case Type '{created.Name}' was created.", ct);
        return new CaseTypeConfigDto
        {
            Id = created.Id,
            Code = created.Code,
            Name = created.Name,
            Prefix = created.Prefix,
            DisplayOrder = created.DisplayOrder,
            IsActive = created.IsActive
        };
    }

    public async Task<CaseTypeConfigDto?> UpdateCaseTypeAsync(Guid id, UpdateCaseTypeConfigDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Code)) throw new InvalidOperationException("Code is required.");

        var existingList = await _repository.GetCaseTypesAsync(false, ct);
        var existing = existingList.FirstOrDefault(c => c.Id == id);
        var oldVal = existing != null ? $"Name: {existing.Name}, Code: {existing.Code}, Prefix: {existing.Prefix}, Active: {existing.IsActive}" : null;

        var name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Code.Trim() : dto.Name.Trim();
        if (await _repository.CaseTypeExistsAsync(dto.Code.Trim(), name, id, ct))
            throw new InvalidOperationException($"A case type with the code '{dto.Code.Trim()}' or name '{name}' already exists.");

        var entity = new CaseTypeConfig
        {
            Code = dto.Code.Trim(),
            Name = name,
            Prefix = string.IsNullOrWhiteSpace(dto.Prefix) ? "C-" : dto.Prefix.Trim().ToUpper(),
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        // A rename must follow the name everywhere that stores it as text, in the same transaction (cases, routing and skill rules).
        var updated = await InTransactionAsync(async () =>
        {
            var result = await _repository.UpdateCaseTypeAsync(id, entity, ct);
            if (result != null && existing != null && !string.Equals(existing.Name, result.Name, StringComparison.Ordinal))
            {
                await _context.Cases.Where(c => c.CaseType.ToLower() == existing.Name.ToLower())
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.CaseType, result.Name), ct);
                await RenameInRoutingRulesAsync(c => c.CaseType, (c, v) => c.CaseType = v, existing.Name, result.Name, ct);
                await RenameInSkillRulesAsync("CaseType", existing.Name, result.Name, ct);
                await _context.SaveChangesAsync(ct);
            }
            return result;
        }, ct);
        if (updated == null) return null;

        var newVal = $"Name: {updated.Name}, Code: {updated.Code}, Prefix: {updated.Prefix}, Active: {updated.IsActive}";
        await RecordAuditLogAsync("UPDATE", $"Case Type: {updated.Name}", $"Updated case type '{updated.Name}'", oldVal, newVal, ct);
        await _notificationService.CreateConfigChangedNotificationAsync("System Setting Updated", $"Case Type '{updated.Name}' configuration was updated.", ct);

        return new CaseTypeConfigDto
        {
            Id = updated.Id,
            Code = updated.Code,
            Name = updated.Name,
            Prefix = updated.Prefix,
            DisplayOrder = updated.DisplayOrder,
            IsActive = updated.IsActive
        };
    }

    public async Task<bool> DeleteCaseTypeAsync(Guid id, CancellationToken ct = default)
    {
        var existingList = await _repository.GetCaseTypesAsync(false, ct);
        var existing = existingList.FirstOrDefault(c => c.Id == id);
        var oldVal = existing != null ? $"Name: {existing.Name}, Code: {existing.Code}, Prefix: {existing.Prefix}" : $"ID: {id}";
        var caseTypeName = existing?.Name ?? "Case Type";

        if (existing != null)
        {
            var inUse = await _repository.CountCasesByCaseTypeAsync(existing.Code, existing.Name, ct);
            if (inUse > 0)
                throw new InvalidOperationException(
                    $"Case type '{existing.Name}' cannot be deleted because {inUse} case(s) use it. Deactivate it instead.");
        }

        var success = await _repository.DeleteCaseTypeAsync(id, ct);
        if (success)
        {
            await RecordAuditLogAsync("DELETE", $"Case Type: {caseTypeName}", $"Deleted case type '{caseTypeName}'", oldVal, null, ct);
            await _notificationService.CreateConfigChangedNotificationAsync("System Setting Updated", $"Case Type '{caseTypeName}' was deleted.", ct);
        }
        return success;
    }

    public async Task<IEnumerable<DepartmentSubCategoryDto>> GetSubCategoriesAsync(Guid? departmentId = null, bool activeOnly = true, CancellationToken ct = default)
    {
        var items = await _repository.GetSubCategoriesAsync(departmentId, activeOnly, ct);
        return items.Select(s => new DepartmentSubCategoryDto
        {
            Id = s.Id,
            DepartmentId = s.DepartmentId,
            DepartmentName = s.Department?.Name ?? string.Empty,
            Name = s.Name,
            Code = s.Code,
            Description = s.Description,
            DisplayOrder = s.DisplayOrder,
            IsActive = s.IsActive
        });
    }

    public async Task<DepartmentSubCategoryDto> AddSubCategoryAsync(CreateDepartmentSubCategoryDto dto, CancellationToken ct = default)
    {
        var entity = new DepartmentSubCategory
        {
            DepartmentId = dto.DepartmentId,
            Name = dto.Name.Trim(),
            Code = await SubCategoryCodeAsync(dto.Code, dto.Name, null, ct),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            DisplayOrder = dto.DisplayOrder,
            IsActive = true
        };

        if (!await _repository.DepartmentExistsAsync(entity.DepartmentId, ct))
            throw new InvalidOperationException("The selected department no longer exists.");

        if (await _repository.SubCategoryExistsAsync(entity.DepartmentId, entity.Name, null, ct))
            throw new InvalidOperationException($"Sub-category '{entity.Name}' already exists for this department.");

        var created = await _repository.AddSubCategoryAsync(entity, ct);
        return new DepartmentSubCategoryDto
        {
            Id = created.Id,
            DepartmentId = created.DepartmentId,
            DepartmentName = created.Department?.Name ?? string.Empty,
            Name = created.Name,
            Code = created.Code,
            Description = created.Description,
            DisplayOrder = created.DisplayOrder,
            IsActive = created.IsActive
        };
    }

    public async Task<DepartmentSubCategoryDto?> UpdateSubCategoryAsync(Guid id, UpdateDepartmentSubCategoryDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new InvalidOperationException("Sub-category name is required.");

        var existing = (await _repository.GetSubCategoriesAsync(null, false, ct)).FirstOrDefault(s => s.Id == id);
        if (existing == null) return null;

        // The sub-category keeps its parent department: renaming must never turn it into a
        // free-floating global value.
        if (await _repository.SubCategoryExistsAsync(existing.DepartmentId, dto.Name.Trim(), id, ct))
            throw new InvalidOperationException($"Sub-category '{dto.Name.Trim()}' already exists for this department.");

        var entity = new DepartmentSubCategory
        {
            Name = dto.Name.Trim(),
            Code = await SubCategoryCodeAsync(dto.Code, dto.Name, id, ct),
            Description = dto.Description == null ? existing.Description : (string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim()),
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        var updated = await InTransactionAsync(async () =>
        {
            var result = await _repository.UpdateSubCategoryAsync(id, entity, ct);
            if (result != null && !string.Equals(existing.Name, result.Name, StringComparison.Ordinal))
            {
                // Cases of THIS department keep pointing at the renamed sub-category.
                await _context.Cases.Where(c => c.DepartmentId == existing.DepartmentId && c.Subcategory != null && c.Subcategory.ToLower() == existing.Name.ToLower())
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.Subcategory, result.Name), ct);
                // Routing and skill rules match a sub-category by NAME across departments: rename them only when no other
                // sub-category still uses the old name, otherwise they would silently stop applying there.
                var nameStillUsed = await _context.DepartmentSubCategories.AnyAsync(sc => sc.Id != id && sc.Name.ToLower() == existing.Name.ToLower(), ct);
                if (!nameStillUsed)
                {
                    await RenameInRoutingRulesAsync(c => c.Category, (c, v) => c.Category = v, existing.Name, result.Name, ct);
                    await RenameInSkillRulesAsync("Subcategory", existing.Name, result.Name, ct);
                }
                await _context.SaveChangesAsync(ct);
            }
            return result;
        }, ct);
        if (updated == null) return null;

        return new DepartmentSubCategoryDto
        {
            Id = updated.Id,
            DepartmentId = updated.DepartmentId,
            DepartmentName = updated.Department?.Name ?? string.Empty,
            Name = updated.Name,
            Code = updated.Code,
            Description = updated.Description,
            DisplayOrder = updated.DisplayOrder,
            IsActive = updated.IsActive
        };
    }

    /// <summary>
    /// The code is the sub-category's business identifier and unique across departments. A code someone typed must be free; a code
    /// generated from the name (when none is given) gets a numeric suffix if another sub-category already uses it.
    /// </summary>
    private async Task<string> SubCategoryCodeAsync(string? requested, string name, Guid? excludeId, CancellationToken ct)
    {
        async Task<DepartmentSubCategory?> HolderAsync(string code) => await _context.DepartmentSubCategories.AsNoTracking().Include(s => s.Department)
            .FirstOrDefaultAsync(s => s.Id != excludeId && s.Code.ToLower() == code.ToLower(), ct);

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var code = requested.Trim();
            if (await HolderAsync(code) is { } holder)
                throw new InvalidOperationException($"The code '{code}' is already used by '{holder.Department.Name} / {holder.Name}'. Each sub-category needs its own code.");
            return code;
        }

        var generated = name.Trim().Replace(" ", "_");
        var candidate = generated;
        for (var n = 2; await HolderAsync(candidate) != null; n++) candidate = $"{generated}_{n}";
        return candidate;
    }

    public async Task<bool> DeleteSubCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var existing = (await _repository.GetSubCategoriesAsync(null, false, ct)).FirstOrDefault(s => s.Id == id);
        if (existing == null) return false;

        var inUse = await _repository.CountCasesBySubCategoryAsync(existing.DepartmentId, existing.Name, ct);
        if (inUse > 0)
            throw new InvalidOperationException(
                $"Sub-category '{existing.Name}' cannot be deleted because {inUse} case(s) use it. Deactivate it instead.");

        return await _repository.DeleteSubCategoryAsync(id, ct);
    }

    // ===== PRIORITY ("SEVERITY") MASTER DATA =====
    // A priority is one PrioritySlaRule row. The Cases SLA & Routing screen edits its targets and which
    // sub-categories map to it; this screen edits its name, order, active flag and (in whole hours) its
    // targets. Same rows, so the two screens can never disagree.

    private static void ValidateSlaHours(int hours, string label)
    {
        if (hours <= 0)
            throw new InvalidOperationException($"{label} must be at least 1 hour.");
        if (hours > MaxSlaHours)
            throw new InvalidOperationException($"{label} cannot exceed {MaxSlaHours} hours (1 year).");
    }

    private static int ToHours(int minutes) => (int)Math.Ceiling(minutes / 60.0);

    private async Task<SeverityDto> ToSeverityDtoAsync(PrioritySlaRule rule, CancellationToken ct) => new()
    {
        Id = rule.Id,
        Name = rule.Priority,
        DisplayOrder = rule.DisplayOrder,
        InternalHours = ToHours(rule.InternalResolutionMinutes),
        ExternalHours = ToHours(rule.ExternalResolutionMinutes),
        FirstResponseMinutes = rule.FirstResponseMinutes,
        IsSystem = false,
        CasesUsing = await _repository.CountCasesBySeverityAsync(rule.Priority, ct),
        IsActive = rule.IsActive
    };

    public async Task<IEnumerable<SeverityDto>> GetSeverityConfigurationsAsync(CancellationToken ct = default)
    {
        var rules = await _context.PrioritySlaRules.AsNoTracking()
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.Priority).ToListAsync(ct);

        var result = new List<SeverityDto>();
        foreach (var rule in rules) result.Add(await ToSeverityDtoAsync(rule, ct));
        return result;
    }

    public async Task<SeverityDto> AddSeverityAsync(CreateSeverityDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Priority name is required.");
        if (name.Length > 50)
            throw new InvalidOperationException("Priority name cannot exceed 50 characters.");

        var lowered = name.ToLower();
        if (await _context.PrioritySlaRules.AnyAsync(r => r.Priority.ToLower() == lowered, ct))
            throw new InvalidOperationException($"Priority '{name}' already exists.");

        // A priority without SLA targets would give cases arbitrary deadlines, so every target must be stated (no built-in defaults).
        if (dto.FirstResponseMinutes <= 0 || dto.InternalHours <= 0 || dto.ExternalHours <= 0)
            throw new InvalidOperationException("A new priority needs its first response, internal and external SLA targets. Add priorities on Cases SLA & Routing → Priority SLA Matrix.");
        var internalHours = dto.InternalHours;
        var externalHours = dto.ExternalHours;
        var firstResponseMinutes = dto.FirstResponseMinutes;
        ValidateSlaHours(internalHours, "Internal SLA");
        ValidateSlaHours(externalHours, "External SLA");

        var nextOrder = (await _context.PrioritySlaRules.MaxAsync(r => (int?)r.DisplayOrder, ct) ?? 0) + 1;
        var rule = new PrioritySlaRule
        {
            Id = Guid.NewGuid(),
            Priority = name,
            DisplayOrder = dto.DisplayOrder > 0 ? dto.DisplayOrder : nextOrder,
            IsActive = true,
            FirstResponseValue = firstResponseMinutes,
            FirstResponseUnit = "Minutes",
            FirstResponseMinutes = firstResponseMinutes,
            InternalResolutionValue = internalHours,
            InternalResolutionUnit = "Hours",
            InternalResolutionMinutes = internalHours * 60,
            ExternalResolutionValue = externalHours,
            ExternalResolutionUnit = "Hours",
            ExternalResolutionMinutes = externalHours * 60,
            Version = 1,
            CreatedAt = DateTime.UtcNow
        };
        _context.PrioritySlaRules.Add(rule);
        await _context.SaveChangesAsync(ct);

        await RecordAuditLogAsync("CREATE", $"Priority: {name}", $"Created priority '{name}'", null,
            $"Internal: {internalHours}h, External: {externalHours}h, First response: {firstResponseMinutes}m", ct);
        return await ToSeverityDtoAsync(rule, ct);
    }

    public async Task<SeverityDto?> UpdateSeverityAsync(Guid id, UpdateSeverityDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Priority name is required.");
        if (name.Length > 50)
            throw new InvalidOperationException("Priority name cannot exceed 50 characters.");

        var rule = await _context.PrioritySlaRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) return null;

        var lowered = name.ToLower();
        if (await _context.PrioritySlaRules.AnyAsync(r => r.Id != id && r.Priority.ToLower() == lowered, ct))
            throw new InvalidOperationException($"Priority '{name}' already exists.");

        if (!dto.IsActive && rule.IsActive &&
            !await _context.PrioritySlaRules.AnyAsync(r => r.Id != id && r.IsActive, ct))
            throw new InvalidOperationException("At least one priority must stay active.");

        var oldName = rule.Priority;
        var oldSummary = $"Name: {oldName}, Order: {rule.DisplayOrder}, Active: {rule.IsActive}, Internal: {ToHours(rule.InternalResolutionMinutes)}h, External: {ToHours(rule.ExternalResolutionMinutes)}h";

        rule.Priority = name;
        rule.DisplayOrder = dto.DisplayOrder > 0 ? dto.DisplayOrder : rule.DisplayOrder;
        rule.IsActive = dto.IsActive;

        // Targets are edited in whole hours here; only values that were supplied change.
        var slaChanged = false;
        if (dto.InternalHours.HasValue)
        {
            ValidateSlaHours(dto.InternalHours.Value, "Internal SLA");
            var minutes = dto.InternalHours.Value * 60;
            slaChanged |= minutes != rule.InternalResolutionMinutes;
            rule.InternalResolutionValue = dto.InternalHours.Value;
            rule.InternalResolutionUnit = "Hours";
            rule.InternalResolutionMinutes = minutes;
        }
        if (dto.ExternalHours.HasValue)
        {
            ValidateSlaHours(dto.ExternalHours.Value, "External SLA");
            var minutes = dto.ExternalHours.Value * 60;
            slaChanged |= minutes != rule.ExternalResolutionMinutes;
            rule.ExternalResolutionValue = dto.ExternalHours.Value;
            rule.ExternalResolutionUnit = "Hours";
            rule.ExternalResolutionMinutes = minutes;
        }
        if (dto.FirstResponseMinutes.HasValue)
        {
            if (dto.FirstResponseMinutes.Value <= 0)
                throw new InvalidOperationException("First response target must be at least 1 minute.");
            slaChanged |= dto.FirstResponseMinutes.Value != rule.FirstResponseMinutes;
            rule.FirstResponseValue = dto.FirstResponseMinutes.Value;
            rule.FirstResponseUnit = "Minutes";
            rule.FirstResponseMinutes = dto.FirstResponseMinutes.Value;
        }
        if (slaChanged) rule.Version += 1;   // cases snapshot this version
        rule.UpdatedAt = DateTime.UtcNow;

        // Everything that refers to the priority BY NAME follows a rename, so nothing is left dangling.
        if (!oldName.Equals(name, StringComparison.Ordinal))
        {
            await _repository.RenameCaseSeverityAsync(oldName, name, ct);
            await RenamePriorityInRoutingRulesAsync(oldName, name, ct);
        }

        await _context.SaveChangesAsync(ct);

        await RecordAuditLogAsync("UPDATE", $"Priority: {name}", $"Updated priority '{name}'", oldSummary,
            $"Name: {name}, Order: {rule.DisplayOrder}, Active: {rule.IsActive}, Internal: {ToHours(rule.InternalResolutionMinutes)}h, External: {ToHours(rule.ExternalResolutionMinutes)}h", ct);
        return await ToSeverityDtoAsync(rule, ct);
    }

    public async Task<bool> DeleteSeverityAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _context.PrioritySlaRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) return false;

        var inUse = await _repository.CountCasesBySeverityAsync(rule.Priority, ct);
        if (inUse > 0)
            throw new InvalidOperationException(
                $"'{rule.Priority}' cannot be deleted because {inUse} case(s) use it. Deactivate it instead.");

        var referencingRules = await RoutingRulesReferencingPriorityAsync(rule.Priority, ct);
        if (referencingRules.Count > 0)
            throw new InvalidOperationException(
                $"'{rule.Priority}' cannot be deleted because routing rule(s) match on it: {string.Join(", ", referencingRules)}.");

        if (!await _context.PrioritySlaRules.AnyAsync(r => r.Id != id, ct))
            throw new InvalidOperationException("At least one priority must remain configured.");

        _context.PrioritySlaRules.Remove(rule);   // its sub-category mappings are removed with it (cascade)
        await _context.SaveChangesAsync(ct);

        await RecordAuditLogAsync("DELETE", $"Priority: {rule.Priority}", $"Deleted priority '{rule.Priority}'", rule.Priority, null, ct);
        return true;
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (_context.Database.CurrentTransaction != null) return await work();
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>Routing rule conditions store names (exact, case-insensitive match): follow a rename of one of them.</summary>
    private async Task RenameInRoutingRulesAsync(Func<RuleConditionsDto, string?> read, Action<RuleConditionsDto, string> write, string oldName, string newName, CancellationToken ct)
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var rule in await _context.RoutingRules.ToListAsync(ct))
        {
            RuleConditionsDto? conditions;
            try { conditions = System.Text.Json.JsonSerializer.Deserialize<RuleConditionsDto>(rule.ConditionsJson, options); }
            catch (System.Text.Json.JsonException) { continue; }
            if (conditions == null || !string.Equals(read(conditions), oldName, StringComparison.OrdinalIgnoreCase)) continue;
            write(conditions, newName);
            rule.ConditionsJson = System.Text.Json.JsonSerializer.Serialize(conditions);
            rule.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Skill rules that require an EXACT value follow a rename ("contains" rules match text and are left alone).</summary>
    private async Task RenameInSkillRulesAsync(string matchField, string oldName, string newName, CancellationToken ct)
    {
        var rules = await _context.SkillRules
            .Where(r => r.MatchField == matchField && r.MatchType == "Equals" && r.MatchValue.ToLower() == oldName.ToLower())
            .ToListAsync(ct);
        foreach (var r in rules) { r.MatchValue = newName; r.UpdatedAt = DateTime.UtcNow; }
    }

    private static string? ConditionPriority(string conditionsJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<RuleConditionsDto>(conditionsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Priority;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private async Task<List<string>> RoutingRulesReferencingPriorityAsync(string priority, CancellationToken ct)
    {
        var rules = await _context.RoutingRules.AsNoTracking().Select(r => new { r.Name, r.ConditionsJson }).ToListAsync(ct);
        return rules
            .Where(r => string.Equals(ConditionPriority(r.ConditionsJson), priority, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name).ToList();
    }

    private async Task RenamePriorityInRoutingRulesAsync(string oldName, string newName, CancellationToken ct)
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var rule in await _context.RoutingRules.ToListAsync(ct))
        {
            if (!string.Equals(ConditionPriority(rule.ConditionsJson), oldName, StringComparison.OrdinalIgnoreCase)) continue;
            var conditions = System.Text.Json.JsonSerializer.Deserialize<RuleConditionsDto>(rule.ConditionsJson, options)!;
            conditions.Priority = newName;
            rule.ConditionsJson = System.Text.Json.JsonSerializer.Serialize(conditions);
            rule.UpdatedAt = DateTime.UtcNow;
        }
    }
}
