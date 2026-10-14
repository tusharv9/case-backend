namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Models;
using CaseManagement.Api.DTOs;

public interface ICustomerRepository
{
    Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid id, CancellationToken ct = default);
    Task<Customer?> GetByIdAsync(Guid id);
    Task<IEnumerable<Customer>> GetAllAsync();
    Task<Customer> AddAsync(Customer customer);
    Task<bool> ExistsByNricAsync(string nric);
    Task<bool> ExistsByPhoneAsync(string phone);
    Task<CustomerDetailDto?> SearchCustomerAsync(CustomerSearchDto dto, CancellationToken ct = default);

    /// <summary>Header smart-search over customers, capped at <paramref name="limit"/> hits.</summary>
    Task<IEnumerable<SearchCustomerHitDto>> SearchCustomersAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>Server-side paginated and filtered list of customers.</summary>
    Task<PagedResponseDto<CustomerSummaryDto>> GetPaginatedAsync(string? search, string? preferredLanguage, string? branch, int page, int pageSize, string? sortBy = null, bool descending = false, CancellationToken ct = default);

    /// <summary>Server-side paginated list of cases belonging to a customer.</summary>
    Task<PagedResponseDto<CaseSummaryDto>> GetCustomerCasesAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The customer's timeline across all their cases: case milestones and customer-visible messages, newest first.</summary>
    Task<PagedResponseDto<CustomerTimelineItemDto>> GetCustomerTimelineAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default);
}
