using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Notifications.Queries;

public sealed record MyPushDevicesQuery : IRequest<Result<IReadOnlyList<PushDeviceDto>>>;
public sealed record FeedbackDeliveriesQuery(int Page = 1, int PageSize = 20) : IRequest<Result<IReadOnlyList<FeedbackDeliveryDto>>>;
public sealed class FeedbackDeliveriesValidator : AbstractValidator<FeedbackDeliveriesQuery>
{ public FeedbackDeliveriesValidator() { RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class MyPushDevicesHandler(IPushDeviceService s) : IRequestHandler<MyPushDevicesQuery, Result<IReadOnlyList<PushDeviceDto>>>
{ public Task<Result<IReadOnlyList<PushDeviceDto>>> Handle(MyPushDevicesQuery r, CancellationToken ct) => s.ListAsync(ct); }
public sealed class FeedbackDeliveriesHandler(INotificationAdministrationService s) : IRequestHandler<FeedbackDeliveriesQuery, Result<IReadOnlyList<FeedbackDeliveryDto>>>
{ public Task<Result<IReadOnlyList<FeedbackDeliveryDto>>> Handle(FeedbackDeliveriesQuery r, CancellationToken ct) => s.ListAsync(r.Page, r.PageSize, ct); }
