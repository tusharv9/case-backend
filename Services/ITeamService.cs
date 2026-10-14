namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;

public interface ITeamService
{
    Task<IEnumerable<TeamDto>> GetTeamsAsync(CancellationToken ct = default);
    Task<TeamDto?> GetTeamByIdAsync(Guid id, CancellationToken ct = default);
    Task<TeamDto> CreateTeamAsync(CreateTeamDto dto, Guid currentUserId, CancellationToken ct = default);
    Task<TeamDto> UpdateTeamAsync(Guid id, UpdateTeamDto dto, Guid currentUserId, CancellationToken ct = default);
    Task DeleteTeamAsync(Guid id, Guid currentUserId, CancellationToken ct = default);
    Task AddMemberAsync(Guid teamId, AddTeamMemberDto dto, Guid currentUserId, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid teamId, Guid userId, Guid currentUserId, CancellationToken ct = default);
    Task<TeamDto> ToggleTeamStatusAsync(Guid id, Guid currentUserId, CancellationToken ct = default);
}
