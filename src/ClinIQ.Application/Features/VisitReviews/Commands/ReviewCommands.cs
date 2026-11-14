using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;
namespace ClinIQ.Application.Features.VisitReviews.Commands;
public static class VisitReviewVersion
{
    public static bool IsValid(string? value)
    {
        try { return value is not null && value.Length <= 24 && Convert.FromBase64String(value).Length == 8; }
        catch (FormatException) { return false; }
    }
}
public sealed record WriteVisitReviewCommand(Guid AppointmentId,WriteVisitReviewInput Input,bool Edit=false):IRequest<Result<VisitReviewWriteResult>>;
public sealed record ModerateVisitReviewCommand(Guid Id,ModerateVisitReviewInput Input):IRequest<Result<VisitReviewDto>>;
public sealed class WriteReviewInputValidator:AbstractValidator<WriteVisitReviewInput>
{public WriteReviewInputValidator(){RuleFor(x=>x.Stars).InclusiveBetween(1,5);RuleFor(x=>x.Text).MaximumLength(2000);}}
public sealed class WriteVisitReviewValidator:AbstractValidator<WriteVisitReviewCommand>
{public WriteVisitReviewValidator(){RuleFor(x=>x.AppointmentId).NotEmpty();RuleFor(x=>x.Input).NotNull().SetValidator(new WriteReviewInputValidator());RuleFor(x=>x.Input.Version).Must(VisitReviewVersion.IsValid).When(x=>x.Edit&&x.Input!=null);}}
public sealed class ModerateReviewInputValidator:AbstractValidator<ModerateVisitReviewInput>
{public ModerateReviewInputValidator(){RuleFor(x=>x.Version).Must(VisitReviewVersion.IsValid);RuleFor(x=>x.Reason).NotEmpty().MaximumLength(2000);RuleFor(x=>x).Must(x=>x.Hide?x.Category.HasValue&&Enum.IsDefined(x.Category.Value):x.Category is null);}}
public sealed class ModerateVisitReviewValidator:AbstractValidator<ModerateVisitReviewCommand>
{public ModerateVisitReviewValidator(){RuleFor(x=>x.Id).NotEmpty();RuleFor(x=>x.Input).NotNull().SetValidator(new ModerateReviewInputValidator());}}
public sealed class WriteVisitReviewHandler(IVisitReviewService s):IRequestHandler<WriteVisitReviewCommand,Result<VisitReviewWriteResult>>
{public Task<Result<VisitReviewWriteResult>> Handle(WriteVisitReviewCommand r,CancellationToken ct)=>s.WriteAsync(r.AppointmentId,r.Input,r.Edit,ct);}
public sealed class ModerateVisitReviewHandler(IVisitReviewService s):IRequestHandler<ModerateVisitReviewCommand,Result<VisitReviewDto>>
{public Task<Result<VisitReviewDto>> Handle(ModerateVisitReviewCommand r,CancellationToken ct)=>s.ModerateAsync(r.Id,r.Input,ct);}
