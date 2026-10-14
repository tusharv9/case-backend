namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class DepartmentRepository : IDepartmentRepository
{
    private readonly AppDbContext _context;

    public DepartmentRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<Department>> GetAllAsync()
    {
        return await _context.Departments.ToListAsync();
    }

    public async Task<Department?> GetByIdAsync(Guid id)
    {
        return await _context.Departments.FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task AddAsync(Department department)
    {
        _context.Departments.Add(department);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Department department)
    {
        _context.Departments.Update(department);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> ExistsByNameOrCodeAsync(string name, string code, Guid? excludeId = null)
    {
        return await _context.Departments.AnyAsync(d =>
            (d.Name.ToLower() == name.ToLower() || d.Code.ToLower() == code.ToLower()) &&
            (excludeId == null || d.Id != excludeId));
    }

    public async Task DeleteAsync(Department department)
    {
        _context.Departments.Remove(department);
        await _context.SaveChangesAsync();
    }

    public async Task<int> CountCasesAsync(Guid departmentId)
    {
        return await _context.Cases.CountAsync(c => c.DepartmentId == departmentId);
    }

    public async Task<int> CountUsersAsync(Guid departmentId)
    {
        return await _context.Users.CountAsync(u => u.DepartmentId == departmentId);
    }

    public async Task<bool> UserExistsAsync(Guid userId)
    {
        return await _context.Users.AnyAsync(u => u.Id == userId);
    }

    public async Task<IEnumerable<DepartmentDto>> GetDepartmentsAsync(CancellationToken ct = default)
    {
        return await _context.Departments
            .AsNoTracking()
            .MapToDepartmentDto()
            .ToListAsync(ct);
    }
}
