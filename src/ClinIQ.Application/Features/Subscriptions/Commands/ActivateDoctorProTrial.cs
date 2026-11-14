using ClinIQ.Domain.Common.Results;
using MediatR;

namespace ClinIQ.Application.Features.Subscriptions.Commands;

public sealed record ActivateDoctorProTrialCommand : IRequest<Result<DoctorSubscriptionDto>>;

public sealed class ActivateDoctorProTrialHandler(IDoctorSubscriptionService service)
    : IRequestHandler<ActivateDoctorProTrialCommand, Result<DoctorSubscriptionDto>>
{
    public Task<Result<DoctorSubscriptionDto>> Handle(ActivateDoctorProTrialCommand request, CancellationToken cancellationToken) =>
        service.ActivateOwnProTrialAsync(cancellationToken);
}
