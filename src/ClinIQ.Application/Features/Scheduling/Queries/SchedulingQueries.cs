using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Scheduling.Queries;

public sealed record GetScheduleRuleQuery(Guid Id) : IRequest<Result<ScheduleRuleDto>>;
public sealed record ListScheduleRulesQuery(Guid? LocationId = null, int Page = 1, int PageSize = 20) : IRequest<Result<SchedulePage<ScheduleRuleDto>>>;
public sealed record GetBookingSessionQuery(Guid Id) : IRequest<Result<SessionDto>>;
public sealed record ListBookingSessionsQuery(Guid? LocationId, DateOnly From, DateOnly Until, int Page = 1, int PageSize = 20) : IRequest<Result<SchedulePage<SessionDto>>>;
public sealed record GetScheduleExceptionQuery(Guid Id) : IRequest<Result<ScheduleExceptionDto>>;
public sealed record ListScheduleExceptionsQuery(DateOnly From, DateOnly Until, int Page = 1, int PageSize = 20) : IRequest<Result<SchedulePage<ScheduleExceptionDto>>>;
public static class ScheduleQueryValidation
{ public static bool Range(DateOnly from, DateOnly until) => until >= from && until.DayNumber - from.DayNumber <= 365; }
public sealed class GetScheduleRuleValidator : AbstractValidator<GetScheduleRuleQuery> { public GetScheduleRuleValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class GetBookingSessionValidator : AbstractValidator<GetBookingSessionQuery> { public GetBookingSessionValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class GetScheduleExceptionValidator : AbstractValidator<GetScheduleExceptionQuery> { public GetScheduleExceptionValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class ListScheduleRulesValidator : AbstractValidator<ListScheduleRulesQuery>
{ public ListScheduleRulesValidator() { RuleFor(x => x.LocationId).NotEqual(Guid.Empty).When(x => x.LocationId.HasValue); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class ListBookingSessionsValidator : AbstractValidator<ListBookingSessionsQuery>
{ public ListBookingSessionsValidator() { RuleFor(x => x.LocationId).NotEqual(Guid.Empty).When(x => x.LocationId.HasValue); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); RuleFor(x => x).Must(x => ScheduleQueryValidation.Range(x.From, x.Until)); } }
public sealed class ListScheduleExceptionsValidator : AbstractValidator<ListScheduleExceptionsQuery>
{ public ListScheduleExceptionsValidator() { RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); RuleFor(x => x).Must(x => ScheduleQueryValidation.Range(x.From, x.Until)); } }
public sealed class GetScheduleRuleHandler(ISchedulingService s) : IRequestHandler<GetScheduleRuleQuery, Result<ScheduleRuleDto>>
{ public Task<Result<ScheduleRuleDto>> Handle(GetScheduleRuleQuery r, CancellationToken ct) => s.GetRuleAsync(r.Id, ct); }
public sealed class ListScheduleRulesHandler(ISchedulingService s) : IRequestHandler<ListScheduleRulesQuery, Result<SchedulePage<ScheduleRuleDto>>>
{ public Task<Result<SchedulePage<ScheduleRuleDto>>> Handle(ListScheduleRulesQuery r, CancellationToken ct) => s.RulesAsync(r.LocationId, r.Page, r.PageSize, ct); }
public sealed class GetBookingSessionHandler(ISchedulingService s) : IRequestHandler<GetBookingSessionQuery, Result<SessionDto>>
{ public Task<Result<SessionDto>> Handle(GetBookingSessionQuery r, CancellationToken ct) => s.GetSessionAsync(r.Id, ct); }
public sealed class ListBookingSessionsHandler(ISchedulingService s) : IRequestHandler<ListBookingSessionsQuery, Result<SchedulePage<SessionDto>>>
{ public Task<Result<SchedulePage<SessionDto>>> Handle(ListBookingSessionsQuery r, CancellationToken ct) => s.SessionsAsync(r.LocationId, r.From, r.Until, r.Page, r.PageSize, ct); }
public sealed class GetScheduleExceptionHandler(ISchedulingService s) : IRequestHandler<GetScheduleExceptionQuery, Result<ScheduleExceptionDto>>
{ public Task<Result<ScheduleExceptionDto>> Handle(GetScheduleExceptionQuery r, CancellationToken ct) => s.GetExceptionAsync(r.Id, ct); }
public sealed class ListScheduleExceptionsHandler(ISchedulingService s) : IRequestHandler<ListScheduleExceptionsQuery, Result<SchedulePage<ScheduleExceptionDto>>>
{ public Task<Result<SchedulePage<ScheduleExceptionDto>>> Handle(ListScheduleExceptionsQuery r, CancellationToken ct) => s.ExceptionsAsync(r.From, r.Until, r.Page, r.PageSize, ct); }
