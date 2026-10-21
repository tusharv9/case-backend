namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface ICustomerService
{
    Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid id, CancellationToken ct = default);
    Task<Customer> CreateCustomerAsync(CreateCustomerDto dto, Guid userId);
    Task<CustomerDetailDto?> SearchCustomerAsync(CustomerSearchDto dto, CancellationToken ct = default);
    Task<IEnumerable<SearchCustomerHitDto>> SearchCustomersAsync(string query, int limit, CancellationToken ct = default);
}
