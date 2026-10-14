namespace CaseManagement.Api.Validators;

using CaseManagement.Api.DTOs;
using FluentValidation;
using System;
using System.Linq;

public class ManualEscalateRequestDtoValidator : AbstractValidator<ManualEscalateRequestDto>
{
    public ManualEscalateRequestDtoValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Escalation reason is required.")
            .MinimumLength(3).WithMessage("Escalation reason must be at least 3 characters.")
            .MaximumLength(500).WithMessage("Escalation reason cannot exceed 500 characters.");
    }
}

public class CreatePublicHolidayDtoValidator : AbstractValidator<CreatePublicHolidayDto>
{
    public CreatePublicHolidayDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Holiday name is required.")
            .MaximumLength(100).WithMessage("Holiday name cannot exceed 100 characters.");

        RuleFor(x => x.HolidayDate)
            .NotEmpty().WithMessage("Holiday date is required.");
    }
}

public class UpdatePublicHolidayDtoValidator : AbstractValidator<UpdatePublicHolidayDto>
{
    public UpdatePublicHolidayDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Holiday name is required.")
            .MaximumLength(100).WithMessage("Holiday name cannot exceed 100 characters.");

        RuleFor(x => x.HolidayDate)
            .NotEmpty().WithMessage("Holiday date is required.");
    }
}

public class UpdateSlaRoutingConfigRequestDtoValidator : AbstractValidator<UpdateSlaRoutingConfigRequestDto>
{
    private static readonly string[] AllowedUnits = { "Minutes", "Hours" };

    public UpdateSlaRoutingConfigRequestDtoValidator()
    {
        RuleFor(x => x.PriorityRules)
            .NotEmpty().WithMessage("At least one priority SLA rule is required.")
            .Must(rules => rules.All(r => !string.IsNullOrWhiteSpace(r.Priority)))
            .WithMessage("Every priority needs a name.")
            .Must(rules =>
            {
                var names = rules.Select(r => r.Priority.Trim().ToLowerInvariant()).ToList();
                return names.Count == names.Distinct().Count();
            }).WithMessage("Priority names must be unique.")
            .Must(rules =>
            {
                // One priority per sub-category (also enforced, with names, by the service).
                var ids = rules.SelectMany(r => r.AppliedSubCategoryIds ?? new List<Guid>()).ToList();
                return ids.Count == ids.Distinct().Count();
            }).WithMessage("A sub-category can have only one priority.");

        RuleForEach(x => x.PriorityRules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.FirstResponseValue)
                .GreaterThan(0).WithMessage("First response SLA must be greater than 0.");
            rule.RuleFor(r => r.FirstResponseUnit)
                .Must(u => AllowedUnits.Contains(u, StringComparer.OrdinalIgnoreCase))
                .WithMessage("First response unit must be 'Minutes' or 'Hours'.");

            rule.RuleFor(r => r.InternalResolutionValue)
                .GreaterThan(0).WithMessage("Internal resolution SLA must be greater than 0.");
            rule.RuleFor(r => r.InternalResolutionUnit)
                .Must(u => AllowedUnits.Contains(u, StringComparer.OrdinalIgnoreCase))
                .WithMessage("Internal resolution unit must be 'Minutes' or 'Hours'.");

            rule.RuleFor(r => r.ExternalResolutionValue)
                .GreaterThan(0).WithMessage("External resolution SLA must be greater than 0.");
            rule.RuleFor(r => r.ExternalResolutionUnit)
                .Must(u => AllowedUnits.Contains(u, StringComparer.OrdinalIgnoreCase))
                .WithMessage("External resolution unit must be 'Minutes' or 'Hours'.");
        });

        RuleFor(x => x.BusinessHours)
            .NotEmpty().WithMessage("Business hours schedule is required.")
            .Must(bh => bh.Count == 7).WithMessage("Business hours schedule must define all 7 days of the week.");

        RuleForEach(x => x.BusinessHours).ChildRules(bh =>
        {
            bh.RuleFor(b => b)
                .Must(b =>
                {
                    if (!b.IsEnabled) return true;
                    if (TimeSpan.TryParse(b.StartTime, out var s) && TimeSpan.TryParse(b.EndTime, out var e))
                    {
                        return s < e;
                    }
                    return false;
                }).WithMessage(b => $"Start time must precede end time for {b.DayName}.");
        });

        RuleFor(x => x.EscalationLevels)
            .NotEmpty().WithMessage("At least one escalation level is required.")
            .Must(levels =>
            {
                var sorted = levels.OrderBy(l => l.LevelNumber).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (sorted[i].LevelNumber != i + 1) return false;
                }
                return true;
            }).WithMessage("Escalation levels must be sequential starting at Level 1 without gaps.");

        RuleForEach(x => x.EscalationLevels).ChildRules(level =>
        {
            level.RuleFor(l => l.Name).NotEmpty().WithMessage("Escalation level name is required.");
            level.RuleFor(l => l.TargetRole).NotEmpty().WithMessage("Target role or assignment is required.");
        });
    }
}
