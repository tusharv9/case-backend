namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface ICustomerService
{
    Task<CustomerDetailDto?> GetCustomer360Async(Guid id, CancellationToken ct = default);
    Task<Customer> CreateCustomerAsync(CreateCustomerDto dto, Guid userId);
    Task<CustomerDetailDto?> SearchCustomerAsync(CustomerSearchDto dto, CancellationToken ct = default);
    Task<IEnumerable<SearchCustomerHitDto>> SearchCustomersAsync(string query, int limit, CancellationToken ct = default);
    Task<PagedResponseDto<CustomerSummaryDto>> GetPaginatedCustomersAsync(string? search, string? preferredLanguage, string? branch, int page, int pageSize, string? sortBy = null, bool descending = false, CancellationToken ct = default);
    Task<PagedResponseDto<CaseSummaryDto>> GetCustomerCasesAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default);
    Task<PagedResponseDto<CustomerTimelineItemDto>> GetCustomerTimelineAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default);
}
