namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface IDepartmentService
{
    Task<IEnumerable<DepartmentDto>> GetDepartmentsAsync(CancellationToken ct = default);
    Task<Department> CreateDepartmentAsync(CreateDepartmentDto dto, Guid userId);
    Task<Department> UpdateDepartmentAsync(Guid id, UpdateDepartmentDto dto, Guid userId);
    Task DeleteDepartmentAsync(Guid id, Guid userId);
    Task SetDepartmentOwnerAsync(Guid id, SetDepartmentOwnerDto dto, Guid userId);
}
