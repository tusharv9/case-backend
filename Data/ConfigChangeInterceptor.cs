namespace CaseManagement.Api.Data;

using System.Runtime.CompilerServices;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Clears the configuration cache whenever configuration data is successfully saved — from ANY code
/// path (services, repositories, the SLA page, seeders), so a new place that edits configuration can
/// never forget to invalidate. A save that fails changes nothing and invalidates nothing.
/// </summary>
public sealed class ConfigChangeInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<Type> ConfigEntityTypes = new()
    {
        typeof(FieldConfiguration), typeof(LookupType), typeof(LookupValue), typeof(Country), typeof(CaseTypeConfig),
        typeof(DepartmentSubCategory), typeof(Department), typeof(PrioritySlaRule), typeof(PriorityCategoryMapping),
        typeof(BusinessHour), typeof(PublicHoliday), typeof(BusinessCalendarSetting), typeof(TeamMember), typeof(AssignmentConfiguration), typeof(SkillRule), typeof(AgentSkill), typeof(RoutingRule), typeof(EscalationLevelConfig),
        typeof(ManualAssignmentSettings), typeof(ManualAssignmentCrossTeam),
    };

    private readonly IConfigCache _cache;
    private readonly ConditionalWeakTable<DbContext, object> _touched = new();

    public ConfigChangeInterceptor(IConfigCache cache) => _cache = cache;

    private void Note(DbContext? context)
    {
        if (context == null) return;
        var changesConfig = context.ChangeTracker.Entries().Any(e =>
            e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
            ConfigEntityTypes.Contains(e.Entity.GetType()));
        if (changesConfig) _touched.AddOrUpdate(context, new object());
    }

    private void Flush(DbContext? context)
    {
        if (context != null && _touched.Remove(context)) _cache.InvalidateAll();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context != null) _touched.Remove(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null) _touched.Remove(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
