namespace CaseManagement.Api.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

public class DepartmentService : IDepartmentService
{
    private const string DepartmentListCacheKey = "departments:all";

    private readonly IDepartmentRepository _departmentRepository;
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DepartmentService> _logger;
    private readonly LookupCacheOptions _options;

    public DepartmentService(
        IDepartmentRepository departmentRepository,
        AppDbContext context,
        IMemoryCache cache,
        IOptions<LookupCacheOptions> options,
        ILogger<DepartmentService>? logger = null)
    {
        _departmentRepository = departmentRepository;
        _logger = logger ?? NullLogger<DepartmentService>.Instance;
        _context = context;
        _cache = cache;
        _options = options.Value;
    }

    /// <summary>
    /// Departments are read on nearly every screen but only change when an administrator edits
    /// them, so the list is cached. Both write paths below invalidate the entry immediately, and
    /// a short configurable expiry bounds staleness if a department is ever changed out of band.
    /// </summary>
    public async Task<IEnumerable<DepartmentDto>> GetDepartmentsAsync(CancellationToken ct = default)
    {
        if (_options.DepartmentsCacheSeconds <= 0)
        {
            return await _departmentRepository.GetDepartmentsAsync(ct);
        }

        if (_cache.TryGetValue(DepartmentListCacheKey, out IReadOnlyList<DepartmentDto>? cached) && cached is not null)
        {
            return cached;
        }

        var departments = (await _departmentRepository.GetDepartmentsAsync(ct)).ToList();
        _cache.Set(DepartmentListCacheKey, (IReadOnlyList<DepartmentDto>)departments,
            TimeSpan.FromSeconds(_options.DepartmentsCacheSeconds));

        return departments;
    }

    private void InvalidateDepartmentCache() => _cache.Remove(DepartmentListCacheKey);

    private async Task RecordAuditLogAsync(string actionType, string entityName, string description, string? oldValue, string? newValue, Guid userId)
    {
        try
        {
            var audit = new CaseEvent
            {
                Id = Guid.NewGuid(),
                CaseId = null,
                EventType = EventType.Other,
                Message = description,
                CreatedAt = DateTime.UtcNow,
                UserId = userId != Guid.Empty ? userId : Guid.Empty,
                Module = "Configurable Settings",
                EntityName = entityName,
                ActionType = actionType,
                OldValue = oldValue,
                NewValue = newValue
            };

            _context.CaseEvents.Add(audit);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DepartmentAuditLog Error");
        }
    }

    public async Task<Department> CreateDepartmentAsync(CreateDepartmentDto dto, Guid userId)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        var code = dto.Code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Department name is required.");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Department code is required.");

        if (await _departmentRepository.ExistsByNameOrCodeAsync(name, code))
            throw new InvalidOperationException($"A department with the name '{name}' or code '{code}' already exists.");

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = name,
            Code = code
        };
        await _departmentRepository.AddAsync(department);
        InvalidateDepartmentCache();
        await RecordAuditLogAsync("CREATE", $"Department: {department.Name}", $"Created department '{department.Name}' ({department.Code})", null, $"Name: {department.Name}, Code: {department.Code}", userId);
        return department;
    }

    public async Task<Department> UpdateDepartmentAsync(Guid id, UpdateDepartmentDto dto, Guid userId)
    {
        var department = await _departmentRepository.GetByIdAsync(id);
        if (department == null) throw new KeyNotFoundException("Department not found.");

        var name = dto.Name?.Trim() ?? string.Empty;
        var code = dto.Code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Department name is required.");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Department code is required.");

        if (await _departmentRepository.ExistsByNameOrCodeAsync(name, code, id))
            throw new InvalidOperationException($"A department with the name '{name}' or code '{code}' already exists.");

        var oldVal = $"Name: {department.Name}, Code: {department.Code}, Active: {department.IsActive}";
        department.Name = name;
        department.Code = code;
        department.IsActive = dto.IsActive;
        await _departmentRepository.UpdateAsync(department);
        InvalidateDepartmentCache();
        var newVal = $"Name: {department.Name}, Code: {department.Code}, Active: {department.IsActive}";
        await RecordAuditLogAsync("UPDATE", $"Department: {department.Name}", $"Updated department '{department.Name}'", oldVal, newVal, userId);
        return department;
    }

    public async Task DeleteDepartmentAsync(Guid id, Guid userId)
    {
        var department = await _departmentRepository.GetByIdAsync(id);
        if (department == null) throw new KeyNotFoundException("Department not found.");

        var caseCount = await _departmentRepository.CountCasesAsync(id);
        if (caseCount > 0)
            throw new InvalidOperationException($"'{department.Name}' cannot be deleted because {caseCount} case(s) are assigned to it.");

        var userCount = await _departmentRepository.CountUsersAsync(id);
        if (userCount > 0)
            throw new InvalidOperationException($"'{department.Name}' cannot be deleted because {userCount} user(s) belong to it.");

        var oldVal = $"Name: {department.Name}, Code: {department.Code}";
        var deptName = department.Name;

        await _departmentRepository.DeleteAsync(department);
        InvalidateDepartmentCache();
        await RecordAuditLogAsync("DELETE", $"Department: {deptName}", $"Deleted department '{deptName}'", oldVal, null, userId);
    }

    public async Task SetDepartmentOwnerAsync(Guid id, SetDepartmentOwnerDto dto, Guid userId)
    {
        var department = await _departmentRepository.GetByIdAsync(id);
        if (department == null) throw new KeyNotFoundException("Department not found.");

        if (department.OwnerId == dto.OwnerId)
            throw new InvalidOperationException("This user is already the owner of this department.");

        var oldOwner = department.OwnerId.HasValue ? department.OwnerId.ToString() : "Unassigned";
        department.OwnerId = dto.OwnerId;
        await _departmentRepository.UpdateAsync(department);
        InvalidateDepartmentCache();
        await RecordAuditLogAsync("UPDATE", $"Department Owner: {department.Name}", $"Updated owner for department '{department.Name}'", $"OwnerId: {oldOwner}", $"OwnerId: {dto.OwnerId}", userId);
    }
}
