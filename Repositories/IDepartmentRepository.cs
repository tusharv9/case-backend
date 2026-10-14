namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.DTOs;

using CaseManagement.Api.Models;

public interface IDepartmentRepository
{
    Task<IEnumerable<Department>> GetAllAsync();
    Task<Department?> GetByIdAsync(Guid id);
    Task AddAsync(Department department);
    Task UpdateAsync(Department department);
    Task<bool> ExistsByNameOrCodeAsync(string name, string code, Guid? excludeId = null);
    Task DeleteAsync(Department department);
    Task<int> CountCasesAsync(Guid departmentId);
    Task<int> CountUsersAsync(Guid departmentId);
    Task<bool> UserExistsAsync(Guid userId);
    Task<IEnumerable<DepartmentDto>> GetDepartmentsAsync(CancellationToken ct = default);
}
