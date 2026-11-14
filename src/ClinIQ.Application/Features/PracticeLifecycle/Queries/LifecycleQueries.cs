using ClinIQ.Application.Features.PracticeLifecycle.Commands;
using ClinIQ.Domain.Common.Results;
using ClinIQ.Domain.PracticeLifecycle;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.PracticeLifecycle.Queries;

public sealed record LifecycleScopeQuery(OperationalScopeKind Kind, Guid Id) : IRequest<Result<LifecycleScopeDto>>;
public sealed record LifecycleHistoryQuery(OperationalScopeKind Kind, Guid Id, int Page = 1, int PageSize = 20) : IRequest<Result<LifecyclePage<LifecycleHistoryDto>>>;
public sealed record EmergencyDetailQuery(Guid Id, bool Admin, int Page = 1, int PageSize = 20) : IRequest<Result<EmergencyDetail>>;
public sealed record EmergencyCancellationsQuery(Guid Id, int Page = 1, int PageSize = 20) : IRequest<Result<LifecyclePage<EmergencyCancellationDto>>>;
public sealed record EmergencyListQuery(bool Admin, OperationalScopeKind? Kind, Guid? ScopeId,
    EmergencyRequestStatus? Status, int Page = 1, int PageSize = 20) : IRequest<Result<LifecyclePage<EmergencySummary>>>;
public sealed class LifecycleScopeValidator : AbstractValidator<LifecycleScopeQuery>
{ public LifecycleScopeValidator() { RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Id).NotEmpty(); } }
public sealed class LifecycleHistoryValidator : AbstractValidator<LifecycleHistoryQuery>
{
    public LifecycleHistoryValidator()
    { RuleFor(x => x.Kind).IsInEnum(); RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x).Must(x => LifecycleValidation.Page(x.Page, x.PageSize)); }
}
public sealed class EmergencyDetailValidator : AbstractValidator<EmergencyDetailQuery>
{
    public EmergencyDetailValidator()
    { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x).Must(x => LifecycleValidation.Page(x.Page, x.PageSize)); }
}
public sealed class EmergencyCancellationsValidator : AbstractValidator<EmergencyCancellationsQuery>
{
    public EmergencyCancellationsValidator()
    { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x).Must(x => LifecycleValidation.Page(x.Page, x.PageSize)); }
}
public sealed class EmergencyListValidator : AbstractValidator<EmergencyListQuery>
{
    public EmergencyListValidator()
    {
        RuleFor(x => x).Must(x => LifecycleValidation.Page(x.Page, x.PageSize));
        RuleFor(x => x).Must(x => x.Admin || (x.Kind.HasValue && Enum.IsDefined(x.Kind.Value) && x.ScopeId.HasValue && x.ScopeId != Guid.Empty));
        RuleFor(x => x.Status!.Value).IsInEnum().When(x => x.Status.HasValue);
    }
}
public sealed class LifecycleScopeHandler(IPracticeLifecycleService s) : IRequestHandler<LifecycleScopeQuery, Result<LifecycleScopeDto>>
{ public Task<Result<LifecycleScopeDto>> Handle(LifecycleScopeQuery r, CancellationToken ct) => s.GetAsync(r.Kind, r.Id, ct); }
public sealed class LifecycleHistoryHandler(IPracticeLifecycleService s) : IRequestHandler<LifecycleHistoryQuery, Result<LifecyclePage<LifecycleHistoryDto>>>
{ public Task<Result<LifecyclePage<LifecycleHistoryDto>>> Handle(LifecycleHistoryQuery r, CancellationToken ct) => s.HistoryAsync(r.Kind, r.Id, r.Page, r.PageSize, ct); }
public sealed class EmergencyDetailHandler(IPracticeLifecycleService s) : IRequestHandler<EmergencyDetailQuery, Result<EmergencyDetail>>
{ public Task<Result<EmergencyDetail>> Handle(EmergencyDetailQuery r, CancellationToken ct) => s.DetailAsync(r.Id, r.Admin, r.Page, r.PageSize, ct); }
public sealed class EmergencyCancellationsHandler(IPracticeLifecycleService s) : IRequestHandler<EmergencyCancellationsQuery, Result<LifecyclePage<EmergencyCancellationDto>>>
{ public Task<Result<LifecyclePage<EmergencyCancellationDto>>> Handle(EmergencyCancellationsQuery r, CancellationToken ct) => s.CancellationsAsync(r.Id, r.Page, r.PageSize, ct); }
public sealed class EmergencyListHandler(IPracticeLifecycleService s) : IRequestHandler<EmergencyListQuery, Result<LifecyclePage<EmergencySummary>>>
{ public Task<Result<LifecyclePage<EmergencySummary>>> Handle(EmergencyListQuery r, CancellationToken ct) => s.RequestsAsync(r.Admin, r.Kind, r.ScopeId, r.Status, r.Page, r.PageSize, ct); }
