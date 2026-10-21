namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.DTOs;
using Microsoft.EntityFrameworkCore;

public class CustomerRepository : ICustomerRepository
{
    private readonly AppDbContext _context;

    private readonly HostIntegration.ICaseAccessScope? _caseAccess;

    /// <param name="caseAccess">A customer's cases and timeline cover only the cases the caller may see.</param>
    public CustomerRepository(AppDbContext context, HostIntegration.ICaseAccessScope? caseAccess = null)
    {
        _context = context;
        _caseAccess = caseAccess;
    }

    public async Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Customers
            .AsNoTracking()
            .AsSplitQuery() // Protects against explosion if we add more nested fields later
            .Where(c => c.Id == id)
            .MapToCustomerDetail()
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Customer?> GetByIdAsync(Guid id)
    {
        return await _context.Customers
            .Include(c => c.Cases)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<IEnumerable<Customer>> GetAllAsync()
    {
        return await _context.Customers.ToListAsync();
    }

    public async Task<Customer> AddAsync(Customer customer)
    {
        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();
        return customer;
    }

    public async Task<bool> ExistsByNricAsync(string nric)
    {
        if (string.IsNullOrWhiteSpace(nric)) return false;
        var clean = nric.Trim();
        var unhyphenated = clean.Replace("-", "").Replace(" ", "");
        return await _context.Customers.AnyAsync(c =>
            (c.NRIC != null && (c.NRIC == clean || c.NRIC.Replace("-", "").Replace(" ", "") == unhyphenated)));
    }

    public async Task<bool> ExistsByPhoneAsync(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var clean = phone.Trim().Replace(" ", "").Replace("-", "");
        return await _context.Customers.AnyAsync(c =>
            c.PhoneNumber.Replace(" ", "").Replace("-", "") == clean);
    }

    /// <summary>
    /// Header smart-search over customers. Matches name, NRIC, Passport, AccountNumber, phone.
    /// </summary>
    public async Task<IEnumerable<SearchCustomerHitDto>> SearchCustomersAsync(string query, int limit, CancellationToken ct = default)
    {
        var pattern = SqlSearchPattern.Contains(query);

        return await _context.Customers
            .AsNoTracking()
            .Where(c => EF.Functions.ILike(c.FullName, pattern)
                     || (c.NRIC != null && EF.Functions.ILike(c.NRIC, pattern))
                     || (c.Passport != null && EF.Functions.ILike(c.Passport, pattern))
                     || (c.AccountNumber != null && EF.Functions.ILike(c.AccountNumber, pattern))
                     || EF.Functions.ILike(c.PhoneNumber, pattern))
            .OrderBy(c => c.FullName)
            .Take(limit)
            .Select(c => new SearchCustomerHitDto
            {
                Id = c.Id,
                FullName = c.FullName,
                NRIC = c.IdType == "Passport Number" ? (c.Passport ?? c.NRIC ?? "") : (c.IdType == "Account Number" ? (c.AccountNumber ?? c.NRIC ?? "") : (c.NRIC ?? ""))
            })
            .ToListAsync(ct);
    }

    public async Task<CustomerDetailDto?> SearchCustomerAsync(CustomerSearchDto dto, CancellationToken ct = default)
    {
        var query = _context.Customers.AsNoTracking().AsQueryable();

        bool hasFilter = false;

        if (!string.IsNullOrWhiteSpace(dto.IdValue))
        {
            var cleanId = dto.IdValue.Trim();
            var unhyphenated = cleanId.Replace("-", "").Replace(" ", "");
            query = query.Where(c => (c.NRIC != null && (c.NRIC == cleanId || c.NRIC.Replace("-", "") == unhyphenated))
                                  || (c.Passport != null && (c.Passport == cleanId || c.Passport.ToUpper() == cleanId.ToUpper()))
                                  || (c.AccountNumber != null && (c.AccountNumber == cleanId || c.AccountNumber.Replace("-", "") == unhyphenated)));
            hasFilter = true;
        }

        if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
        {
            var cleanPhone = dto.PhoneNumber.Trim().Replace(" ", "").Replace("-", "");
            query = query.Where(c => c.PhoneNumber.Replace(" ", "").Replace("-", "").Contains(cleanPhone) || cleanPhone.Contains(c.PhoneNumber.Replace(" ", "").Replace("-", "")));
            hasFilter = true;
        }

        if (dto.DateOfBirth.HasValue)
        {
            var dob = dto.DateOfBirth.Value.Date;
            query = query.Where(c => c.DateOfBirth.HasValue && c.DateOfBirth.Value.Date == dob);
            hasFilter = true;
        }

        if (!hasFilter) return null;

        return await query.MapToCustomerDetail().FirstOrDefaultAsync(ct);
    }
}
