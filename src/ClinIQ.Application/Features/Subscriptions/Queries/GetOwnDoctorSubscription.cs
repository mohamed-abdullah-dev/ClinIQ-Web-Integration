using ClinIQ.Domain.Common.Results;
using MediatR;

namespace ClinIQ.Application.Features.Subscriptions.Queries;

public sealed record GetOwnDoctorSubscriptionQuery : IRequest<Result<DoctorSubscriptionDto>>;

public sealed class GetOwnDoctorSubscriptionHandler(IDoctorSubscriptionService service)
    : IRequestHandler<GetOwnDoctorSubscriptionQuery, Result<DoctorSubscriptionDto>>
{
    public Task<Result<DoctorSubscriptionDto>> Handle(GetOwnDoctorSubscriptionQuery request, CancellationToken cancellationToken) =>
        service.GetOwnAsync(cancellationToken);
}
