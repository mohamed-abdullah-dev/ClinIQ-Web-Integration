using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.Appointments.Commands;

public sealed record PreviewBookingQuery(Guid SessionId, Guid? SourceId = null) : IRequest<Result<BookingPreview>>;
public sealed record BookAppointmentCommand(BookingConfirmation Input) : IRequest<Result<AppointmentOperationResult>>;
public sealed record CancelAppointmentCommand(Guid Id, Guid RequestId, string Reason) : IRequest<Result<AppointmentOperationResult>>;
public sealed record RescheduleAppointmentCommand(Guid Id, BookingConfirmation Input) : IRequest<Result<AppointmentOperationResult>>;
public sealed record GetAppointmentQuery(Guid Id) : IRequest<Result<AppointmentDto>>;
public sealed record ListAppointmentsQuery(int Page = 1, int PageSize = 20) : IRequest<Result<AppointmentPage>>;
public sealed record CancelBookedSessionCommand(Guid Id, string Version, string Reason) : IRequest<Result<Application.Features.Scheduling.SessionDto>>;
public sealed record RetryBookingNoticesCommand : IRequest<Result<BookingNoticeRetryResult>>;

public sealed class BookingConfirmationValidator : AbstractValidator<BookingConfirmation>
{
    public BookingConfirmationValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty(); RuleFor(x => x.RequestId).NotEmpty();
        RuleFor(x => x.QuoteToken).NotEmpty().MaximumLength(16384);
        RuleFor(x => x.AcceptPrice).Equal(true); RuleFor(x => x.ConfirmContact).Equal(true);
    }
}
public sealed class PreviewBookingValidator : AbstractValidator<PreviewBookingQuery>
{ public PreviewBookingValidator() { RuleFor(x => x.SessionId).NotEmpty(); RuleFor(x => x.SourceId).NotEqual(Guid.Empty).When(x => x.SourceId.HasValue); } }
public sealed class BookAppointmentValidator : AbstractValidator<BookAppointmentCommand>
{ public BookAppointmentValidator() => RuleFor(x => x.Input).NotNull().SetValidator(new BookingConfirmationValidator()); }
public sealed class RescheduleAppointmentValidator : AbstractValidator<RescheduleAppointmentCommand>
{ public RescheduleAppointmentValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new BookingConfirmationValidator()); } }
public sealed class CancelAppointmentValidator : AbstractValidator<CancelAppointmentCommand>
{ public CancelAppointmentValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.RequestId).NotEmpty(); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000); } }
public sealed class GetAppointmentValidator : AbstractValidator<GetAppointmentQuery>
{ public GetAppointmentValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class ListAppointmentsValidator : AbstractValidator<ListAppointmentsQuery>
{ public ListAppointmentsValidator() { RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class CancelBookedSessionValidator : AbstractValidator<CancelBookedSessionCommand>
{
    public CancelBookedSessionValidator()
    {
        RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Version).NotEmpty().Must(x => { try { return Convert.FromBase64String(x ?? "").Length == 8; } catch (FormatException) { return false; } });
    }
}
public sealed class PreviewBookingHandler(IAppointmentService service) : IRequestHandler<PreviewBookingQuery, Result<BookingPreview>>
{ public Task<Result<BookingPreview>> Handle(PreviewBookingQuery r, CancellationToken ct) => service.PreviewAsync(r.SessionId, r.SourceId, ct); }
public sealed class BookAppointmentHandler(IAppointmentService service) : IRequestHandler<BookAppointmentCommand, Result<AppointmentOperationResult>>
{ public Task<Result<AppointmentOperationResult>> Handle(BookAppointmentCommand r, CancellationToken ct) => service.BookAsync(r.Input, ct); }
public sealed class CancelAppointmentHandler(IAppointmentService service) : IRequestHandler<CancelAppointmentCommand, Result<AppointmentOperationResult>>
{ public Task<Result<AppointmentOperationResult>> Handle(CancelAppointmentCommand r, CancellationToken ct) => service.CancelAsync(r.Id, r.RequestId, r.Reason, ct); }
public sealed class RescheduleAppointmentHandler(IAppointmentService service) : IRequestHandler<RescheduleAppointmentCommand, Result<AppointmentOperationResult>>
{ public Task<Result<AppointmentOperationResult>> Handle(RescheduleAppointmentCommand r, CancellationToken ct) => service.RescheduleAsync(r.Id, r.Input, ct); }
public sealed class GetAppointmentHandler(IAppointmentService service) : IRequestHandler<GetAppointmentQuery, Result<AppointmentDto>>
{ public Task<Result<AppointmentDto>> Handle(GetAppointmentQuery r, CancellationToken ct) => service.GetAsync(r.Id, ct); }
public sealed class ListAppointmentsHandler(IAppointmentService service) : IRequestHandler<ListAppointmentsQuery, Result<AppointmentPage>>
{ public Task<Result<AppointmentPage>> Handle(ListAppointmentsQuery r, CancellationToken ct) => service.ListAsync(r.Page, r.PageSize, ct); }
public sealed class CancelBookedSessionHandler(IAppointmentService service) : IRequestHandler<CancelBookedSessionCommand, Result<Application.Features.Scheduling.SessionDto>>
{ public Task<Result<Application.Features.Scheduling.SessionDto>> Handle(CancelBookedSessionCommand r, CancellationToken ct) => service.CancelSessionAsync(r.Id, r.Version, r.Reason, ct); }
public sealed class RetryBookingNoticesHandler(IAppointmentService service) : IRequestHandler<RetryBookingNoticesCommand, Result<BookingNoticeRetryResult>>
{ public Task<Result<BookingNoticeRetryResult>> Handle(RetryBookingNoticesCommand r, CancellationToken ct) => service.RetryNoticesAsync(ct); }
