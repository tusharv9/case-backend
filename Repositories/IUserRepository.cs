namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.DTOs;

public interface IUserRepository
{
    Task<bool> UserExistsAsync(Guid userId);
    Task<bool> ExistsByEmailAsync(string email);
    Task<IEnumerable<UserDto>> GetUsersAsync(CancellationToken ct = default);
}
