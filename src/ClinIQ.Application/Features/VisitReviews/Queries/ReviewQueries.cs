using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;
namespace ClinIQ.Application.Features.VisitReviews.Queries;
public sealed record OwnVisitReviewQuery(Guid AppointmentId):IRequest<Result<VisitReviewDto>>;
public sealed record PublicVisitReviewsQuery(Guid DoctorId,int Page=1,int PageSize=20):IRequest<Result<VisitReviewPage<PublicVisitReview>>>;
public sealed record AdminVisitReviewsQuery(int Page=1,int PageSize=20):IRequest<Result<VisitReviewPage<VisitReviewDto>>>;
public sealed record VisitReviewHistoryQuery(Guid Id,int Page=1,int PageSize=20):IRequest<Result<VisitReviewPage<VisitReviewAuditDto>>>;
public sealed class OwnVisitReviewValidator:AbstractValidator<OwnVisitReviewQuery>{public OwnVisitReviewValidator()=>RuleFor(x=>x.AppointmentId).NotEmpty();}
public sealed class PublicVisitReviewsValidator:AbstractValidator<PublicVisitReviewsQuery>{public PublicVisitReviewsValidator(){RuleFor(x=>x.DoctorId).NotEmpty();RuleFor(x=>x.Page).InclusiveBetween(1,100000);RuleFor(x=>x.PageSize).InclusiveBetween(1,100);}}
public sealed class AdminVisitReviewsValidator:AbstractValidator<AdminVisitReviewsQuery>{public AdminVisitReviewsValidator(){RuleFor(x=>x.Page).InclusiveBetween(1,100000);RuleFor(x=>x.PageSize).InclusiveBetween(1,100);}}
public sealed class VisitReviewHistoryValidator:AbstractValidator<VisitReviewHistoryQuery>{public VisitReviewHistoryValidator(){RuleFor(x=>x.Id).NotEmpty();RuleFor(x=>x.Page).InclusiveBetween(1,100000);RuleFor(x=>x.PageSize).InclusiveBetween(1,100);}}
public sealed class OwnVisitReviewHandler(IVisitReviewService s):IRequestHandler<OwnVisitReviewQuery,Result<VisitReviewDto>>{public Task<Result<VisitReviewDto>> Handle(OwnVisitReviewQuery r,CancellationToken ct)=>s.OwnAsync(r.AppointmentId,ct);}
public sealed class PublicVisitReviewsHandler(IVisitReviewService s):IRequestHandler<PublicVisitReviewsQuery,Result<VisitReviewPage<PublicVisitReview>>>{public Task<Result<VisitReviewPage<PublicVisitReview>>> Handle(PublicVisitReviewsQuery r,CancellationToken ct)=>s.PublicAsync(r.DoctorId,r.Page,r.PageSize,ct);}
public sealed class AdminVisitReviewsHandler(IVisitReviewService s):IRequestHandler<AdminVisitReviewsQuery,Result<VisitReviewPage<VisitReviewDto>>>{public Task<Result<VisitReviewPage<VisitReviewDto>>> Handle(AdminVisitReviewsQuery r,CancellationToken ct)=>s.AdminAsync(r.Page,r.PageSize,ct);}
public sealed class VisitReviewHistoryHandler(IVisitReviewService s):IRequestHandler<VisitReviewHistoryQuery,Result<VisitReviewPage<VisitReviewAuditDto>>>{public Task<Result<VisitReviewPage<VisitReviewAuditDto>>> Handle(VisitReviewHistoryQuery r,CancellationToken ct)=>s.HistoryAsync(r.Id,r.Page,r.PageSize,ct);}
