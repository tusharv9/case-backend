namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using Microsoft.EntityFrameworkCore;

public class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<bool> UserExistsAsync(Guid userId)
    {
        return await _context.Users.AnyAsync(u => u.Id == userId);
    }

    public async Task<bool> ExistsByEmailAsync(string email)
    {
        return await _context.Users.AnyAsync(u => u.Email == email);
    }

    public async Task<IEnumerable<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        return await _context.Users
            .AsNoTracking()
            .Where(u => u.IsActive)   // deactivated Host users cannot be assigned work
            .MapToUserDto(_context.TeamMembers)
            .ToListAsync(ct);
    }
}
