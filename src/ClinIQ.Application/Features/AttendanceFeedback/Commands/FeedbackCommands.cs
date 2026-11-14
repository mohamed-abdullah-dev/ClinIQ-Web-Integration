using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.AttendanceFeedback.Commands;

public sealed record AnswerFeedbackCommand(Guid Id, FeedbackAnswerInput Input, bool Edit = false) : IRequest<Result<FeedbackAnswerReceipt>>;
public sealed record FinalizeFeedbackReviewCommand(Guid Id, FeedbackReviewInput Input) : IRequest<Result<FeedbackReviewDetail>>;
public static class FeedbackVersion
{
    public static bool IsValid(string? value)
    { try { return value is not null && value.Length <= 24 && Convert.FromBase64String(value).Length == 8; } catch (FormatException) { return false; } }
}
public sealed class FeedbackAnswerInputValidator : AbstractValidator<FeedbackAnswerInput>
{
    public FeedbackAnswerInputValidator()
    {
        RuleFor(x => x.SubmissionId).NotEmpty(); RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Version).Must(FeedbackVersion.IsValid).WithMessage("Provide the feedback row version.");
    }
}
public sealed class AnswerFeedbackValidator : AbstractValidator<AnswerFeedbackCommand>
{
    public AnswerFeedbackValidator()
    { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new FeedbackAnswerInputValidator()); }
}
public sealed class FeedbackReviewInputValidator : AbstractValidator<FeedbackReviewInput>
{
    public FeedbackReviewInputValidator()
    {
        RuleFor(x => x.Version).Must(FeedbackVersion.IsValid); RuleFor(x => x.AppointmentVersion).Must(FeedbackVersion.IsValid);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x).Must(x => x.State == AttendanceReviewState.Resolved
            ? x.Finding.HasValue && Enum.IsDefined(x.Finding.Value)
            : x.State == AttendanceReviewState.Inconclusive && x.Finding is null).WithMessage("Resolved requires a finding; Inconclusive has no finding.");
        RuleFor(x => x).Must(x => x.State != AttendanceReviewState.Inconclusive || (x.ArrivedAtUtc is null && x.CompletedAtUtc is null))
            .WithMessage("Inconclusive does not correct attendance timestamps.");
        RuleFor(x => x.CompletedAtUtc).NotNull().When(x => x.State == AttendanceReviewState.Resolved && x.Finding == PatientAttendanceOutcome.AttendedAndSeen);
        RuleFor(x => x).Must(x => x.Finding != PatientAttendanceOutcome.DidNotAttend || (x.ArrivedAtUtc is null && x.CompletedAtUtc is null))
            .WithMessage("Confirmed absence has no arrival or completion time.");
        RuleFor(x => x.CompletedAtUtc).Null().When(x => x.Finding == PatientAttendanceOutcome.AttendedNotSeen);
    }
}
public sealed class FinalizeFeedbackReviewValidator : AbstractValidator<FinalizeFeedbackReviewCommand>
{
    public FinalizeFeedbackReviewValidator()
    { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Input).NotNull().SetValidator(new FeedbackReviewInputValidator()); }
}
public sealed class AnswerFeedbackHandler(IAttendanceFeedbackService service) : IRequestHandler<AnswerFeedbackCommand, Result<FeedbackAnswerReceipt>>
{ public Task<Result<FeedbackAnswerReceipt>> Handle(AnswerFeedbackCommand r, CancellationToken ct) => service.AnswerAsync(r.Id, r.Input, r.Edit, ct); }
public sealed class FinalizeFeedbackReviewHandler(IAttendanceFeedbackService service) : IRequestHandler<FinalizeFeedbackReviewCommand, Result<FeedbackReviewDetail>>
{ public Task<Result<FeedbackReviewDetail>> Handle(FinalizeFeedbackReviewCommand r, CancellationToken ct) => service.FinalizeAsync(r.Id, r.Input, ct); }
