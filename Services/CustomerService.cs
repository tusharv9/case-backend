namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;

public class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IPiiMaskingService _piiMasking;
    private readonly IFieldValidationEngine _fieldValidation;
    private readonly ICountryService _countries;
    private readonly IIdFormatService _idFormats;
    private readonly ISlaClockProvider? _slaClock;

    public CustomerService(ICustomerRepository customerRepository, IPiiMaskingService piiMasking, IFieldValidationEngine fieldValidation, ICountryService countries, IIdFormatService idFormats, ISlaClockProvider? slaClock = null)
    {
        _customerRepository = customerRepository;
        _piiMasking = piiMasking;
        _fieldValidation = fieldValidation;
        _countries = countries;
        _idFormats = idFormats;
        _slaClock = slaClock;
    }

    /// <summary>Adds the SLA clock's verdict to cases shown on a customer's pages, so they read exactly like the case board.</summary>
    private async Task EnrichSlaAsync(IEnumerable<CaseSummaryDto?>? cases, CancellationToken ct)
    {
        if (_slaClock == null || cases == null) return;
        (await _slaClock.GetAsync(ct)).Enrich(cases);
    }

    public async Task<CustomerDetailDto?> GetCustomer360Async(Guid id, CancellationToken ct = default)
    {
        var detail = await _customerRepository.GetCustomerDetailAsync(id, ct);
        if (detail != null) await EnrichSlaAsync(detail.Cases, ct);
        return detail;
    }

    private async Task<string> PhoneLabelAsync()
    {
        var f = await _fieldValidation.GetConfigAsync("Customer360", "AddNewCustomer", "phoneNumber");
        return string.IsNullOrWhiteSpace(f?.DisplayLabel) ? "Phone number" : f.DisplayLabel;
    }

    public async Task<Customer> CreateCustomerAsync(CreateCustomerDto dto, Guid userId)
    {
        var idVal = !string.IsNullOrWhiteSpace(dto.IdValue)
            ? dto.IdValue.Trim()
            : (!string.IsNullOrWhiteSpace(dto.NRIC)
                ? dto.NRIC.Trim()
                : (!string.IsNullOrWhiteSpace(dto.Passport)
                    ? dto.Passport.Trim()
                    : (!string.IsNullOrWhiteSpace(dto.AccountNumber)
                        ? dto.AccountNumber.Trim()
                        : string.Empty)));

        // Required / optional, lengths, patterns, field types, lookup membership and custom fields all come from
        // the "Add New Customer" field configuration.
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["fullName"] = dto.FullName,
            ["idType"] = dto.IdType,
            ["idValue"] = idVal,
            ["dateOfBirth"] = dto.DateOfBirth?.ToString("yyyy-MM-dd"),
            ["phoneNumber"] = dto.PhoneNumber,
            ["email"] = dto.Email,
            ["preferredLanguage"] = dto.PreferredLanguage,
            ["branch"] = dto.Branch,
        };
        foreach (var (key, value) in dto.CustomAttributes ?? new Dictionary<string, string>())
            values[key] = value;

        var validation = await _fieldValidation.ValidateAsync(
            "Customer360", "AddNewCustomer", values, customFieldKeys: dto.CustomAttributes?.Keys);
        validation.ThrowIfInvalid();

        var idType = validation.Normalized["idType"];     // locked-required, so present; configured spelling
        string Normalized(string field) => validation.Normalized.TryGetValue(field, out var v) ? v : string.Empty;

        string? nricVal = null;
        string? passportVal = null;
        string? accountVal = null;

        if (idType.Equals("Passport Number", StringComparison.OrdinalIgnoreCase))
        {
            passportVal = idVal;
            idType = "Passport Number";
        }
        else if (idType.Equals("Account Number", StringComparison.OrdinalIgnoreCase))
        {
            accountVal = idVal;
            idType = "Account Number";
        }
        else if (idType.Equals("NRIC Number", StringComparison.OrdinalIgnoreCase))
        {
            nricVal = idVal;
            idType = "NRIC Number";
        }
        else
        {
            // Enabled in the ID_TYPE lookup but nowhere to store it yet: say so instead of filing it as an NRIC.
            throw new FieldValidationException(new[] { new FieldError("idType", $"ID type '{idType}' is not supported by this system yet.") });
        }

        // What an ID value of this type must look like is configuration (the format rule on the ID type option), not code.
        var idFormat = await _idFormats.GetAsync(idType);
        var idFormatError = IdFormatRules.Check(idFormat, idVal) ?? IdFormatRules.CheckAgainstDateOfBirth(idFormat, idVal, dto.DateOfBirth);
        if (idFormatError != null)
            throw new FieldValidationException(new[] { new FieldError(idFormatError.StartsWith("Date of Birth") ? "dateOfBirth" : "idValue", idFormatError) });

        // Duplicate NRIC check (Requirement 6)
        if (!string.IsNullOrWhiteSpace(nricVal))
        {
            if (await _customerRepository.ExistsByNricAsync(nricVal))
                throw new InvalidOperationException("A customer already exists with this NRIC number.");
        }

        // The phone number is checked against ITS country's rules (Countries table) and stored as "+<dial> <digits>".
        // An optional field may be blank.
        var normalizedPhone = string.Empty;
        var phoneCountry = CountryService.DefaultIso2;
        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            var phoneLabel = await PhoneLabelAsync();
            var phone = await _countries.NormalizePhoneAsync(dto.PhoneCountryIso2, dto.PhoneNumber, phoneLabel);
            if (phone.Error != null)
                throw new FieldValidationException(new[] { new FieldError("phoneNumber", phone.Error) });
            normalizedPhone = phone.Normalized;
            phoneCountry = phone.Iso2;

            // Duplicate Phone Number check (on the stored, normalised form — what the unique index compares)
            if (await _customerRepository.ExistsByPhoneAsync(normalizedPhone))
                throw new InvalidOperationException("A customer already exists with this phone number.");
        }

        var newCustomer = new Customer
        {
            FullName = Normalized("fullName"),
            IdType = idType,
            NRIC = nricVal,
            Passport = passportVal,
            AccountNumber = accountVal,
            PhoneNumber = normalizedPhone,
            PhoneCountryIso2 = phoneCountry,
            Email = Normalized("email"),
            Branch = Normalized("branch"),
            CustomerSegment = dto.CustomerSegment?.Trim(),
            PreferredLanguage = Normalized("preferredLanguage"),
            DateOfBirth = dto.DateOfBirth
        };

        // Custom attributes (also mirror idType for any legacy readers)
        newCustomer.CustomAttributes.Add(new CustomerCustomAttribute
        {
            Id = Guid.NewGuid(),
            CustomerId = newCustomer.Id,
            FieldKey = "idType",
            FieldValue = idType,
            CreatedAt = DateTime.UtcNow
        });

        foreach (var key in (dto.CustomAttributes ?? new Dictionary<string, string>()).Keys)
        {
            var value = Normalized(key);
            if (value.Length == 0 || key.Equals("idType", StringComparison.OrdinalIgnoreCase)) continue;
            newCustomer.CustomAttributes.Add(new CustomerCustomAttribute
            {
                Id = Guid.NewGuid(),
                CustomerId = newCustomer.Id,
                FieldKey = key,
                FieldValue = value,
                CreatedAt = DateTime.UtcNow
            });
        }

        try
        {
            return await _customerRepository.AddAsync(newCustomer);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("IX_Customers_NRIC", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A customer already exists with this NRIC number.");
            if (msg.Contains("IX_Customers_PhoneNumber", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A customer already exists with this phone number.");
            throw;
        }
    }

    public async Task<CustomerDetailDto?> SearchCustomerAsync(CustomerSearchDto dto, CancellationToken ct = default)
    {
        var detail = await _customerRepository.SearchCustomerAsync(dto, ct);
        if (detail != null) await EnrichSlaAsync(detail.Cases, ct);
        return detail;
    }

    public async Task<IEnumerable<SearchCustomerHitDto>> SearchCustomersAsync(string query, int limit, CancellationToken ct = default)
    {
        var hits = (await _customerRepository.SearchCustomersAsync(query, limit, ct)).ToList();
        foreach (var hit in hits)
            await _piiMasking.MaskAsync(hit, ct);
        return hits;
    }

    public async Task<PagedResponseDto<CustomerSummaryDto>> GetPaginatedCustomersAsync(
        string? search,
        string? preferredLanguage,
        string? branch,
        int page,
        int pageSize,
        string? sortBy = null,
        bool descending = false,
        CancellationToken ct = default)
    {
        var result = await _customerRepository.GetPaginatedAsync(search, preferredLanguage, branch, page, pageSize, sortBy, descending, ct);
        foreach (var item in result.Items)
        {
            await _piiMasking.MaskCustomerSummaryAsync(item, ct);
        }
        return result;
    }

    public async Task<PagedResponseDto<CaseSummaryDto>> GetCustomerCasesAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default)
    {
        var paged = await _customerRepository.GetCustomerCasesAsync(customerId, page, pageSize, ct);
        await EnrichSlaAsync(paged.Items, ct);
        return paged;
    }

    public Task<PagedResponseDto<CustomerTimelineItemDto>> GetCustomerTimelineAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default) =>
        _customerRepository.GetCustomerTimelineAsync(customerId, Math.Max(1, page), Math.Clamp(pageSize, 1, 100), ct);
}
