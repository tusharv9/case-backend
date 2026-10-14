namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read-only view of users plus the caller's own operational status. Users are owned by the Host
/// App: there is deliberately no endpoint here to create, edit or delete one.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class UsersController : BaseApiController
{
    private readonly AppDbContext _context;
    private readonly CaseManagement.Api.Repositories.IUserRepository _userRepository;
    private readonly ICurrentUserAccessor _currentUser;

    public UsersController(
        AppDbContext context,
        CaseManagement.Api.Repositories.IUserRepository userRepository,
        ICurrentUserAccessor currentUser)
    {
        _context = context;
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    /// <summary>Active users, for pickers (assign, co-worker, team lead…).</summary>
    [HttpGet]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await _userRepository.GetUsersAsync(ct);
        return Ok(users);
    }

    /// <summary>
    /// The signed-in user as Case Management knows them: local id (used by the rest of the API),
    /// Host id, and the permissions the UI uses to show or hide features.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var me = await _context.Users.AsNoTracking()
            .Where(u => u.Id == CurrentUserId)
            .MapToUserDto(_context.TeamMembers)
            .FirstOrDefaultAsync(ct);
        if (me == null) return NotFound(new { error = "User not found." });

        return Ok(new
        {
            user = me,
            permissions = _currentUser.Permissions.OrderBy(p => p).ToArray()
        });
    }

    [HttpPut("me/status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateUserStatusDto dto)
    {
        var user = await _context.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound("User not found.");

        if (!Enum.TryParse<UserStatus>(dto.Status, true, out var newStatus) || !Enum.IsDefined(newStatus))
        {
            return BadRequest($"Invalid status '{dto.Status}'. Allowed values: {string.Join(", ", Enum.GetNames<UserStatus>())}.");
        }

        user.Status = newStatus;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Status updated successfully.", status = newStatus.ToString() });
    }
}
