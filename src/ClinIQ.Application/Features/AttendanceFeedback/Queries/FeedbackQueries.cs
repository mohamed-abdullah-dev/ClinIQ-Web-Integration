using ClinIQ.Application.Features.Attendance;
using ClinIQ.Domain.AttendanceFeedback;
using ClinIQ.Domain.Common.Results;
using FluentValidation;
using MediatR;

namespace ClinIQ.Application.Features.AttendanceFeedback.Queries;

public sealed record FeedbackListQuery(int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<FeedbackDto>>>;
public sealed record FeedbackGetQuery(Guid Id) : IRequest<Result<FeedbackDto>>;
public sealed record FeedbackReviewsQuery(AttendanceReviewState State = AttendanceReviewState.Open, int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<FeedbackDto>>>;
public sealed record FeedbackReviewQuery(Guid Id) : IRequest<Result<FeedbackReviewDetail>>;
public sealed record FeedbackResponsesQuery(Guid Id, bool Admin = false, int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<FeedbackResponseDto>>>;
public sealed record FeedbackStaffHistoryQuery(Guid Id, int Page = 1, int PageSize = 20) : IRequest<Result<AttendancePage<AttendanceHistory>>>;
public sealed class FeedbackListValidator : AbstractValidator<FeedbackListQuery>
{ public FeedbackListValidator() { RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class FeedbackGetValidator : AbstractValidator<FeedbackGetQuery>
{ public FeedbackGetValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class FeedbackReviewsValidator : AbstractValidator<FeedbackReviewsQuery>
{ public FeedbackReviewsValidator() { RuleFor(x => x.State).IsInEnum(); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class FeedbackReviewValidator : AbstractValidator<FeedbackReviewQuery>
{ public FeedbackReviewValidator() => RuleFor(x => x.Id).NotEmpty(); }
public sealed class FeedbackResponsesValidator : AbstractValidator<FeedbackResponsesQuery>
{ public FeedbackResponsesValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class FeedbackStaffHistoryValidator : AbstractValidator<FeedbackStaffHistoryQuery>
{ public FeedbackStaffHistoryValidator() { RuleFor(x => x.Id).NotEmpty(); RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); } }
public sealed class FeedbackListHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackListQuery, Result<AttendancePage<FeedbackDto>>>
{ public Task<Result<AttendancePage<FeedbackDto>>> Handle(FeedbackListQuery r, CancellationToken ct) => s.ListAsync(r.Page, r.PageSize, ct); }
public sealed class FeedbackGetHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackGetQuery, Result<FeedbackDto>>
{ public Task<Result<FeedbackDto>> Handle(FeedbackGetQuery r, CancellationToken ct) => s.GetAsync(r.Id, ct); }
public sealed class FeedbackReviewsHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackReviewsQuery, Result<AttendancePage<FeedbackDto>>>
{ public Task<Result<AttendancePage<FeedbackDto>>> Handle(FeedbackReviewsQuery r, CancellationToken ct) => s.ReviewsAsync(r.State, r.Page, r.PageSize, ct); }
public sealed class FeedbackReviewHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackReviewQuery, Result<FeedbackReviewDetail>>
{ public Task<Result<FeedbackReviewDetail>> Handle(FeedbackReviewQuery r, CancellationToken ct) => s.ReviewAsync(r.Id, ct); }
public sealed class FeedbackResponsesHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackResponsesQuery, Result<AttendancePage<FeedbackResponseDto>>>
{ public Task<Result<AttendancePage<FeedbackResponseDto>>> Handle(FeedbackResponsesQuery r, CancellationToken ct) => s.ResponsesAsync(r.Id, r.Page, r.PageSize, r.Admin, ct); }
public sealed class FeedbackStaffHistoryHandler(IAttendanceFeedbackService s) : IRequestHandler<FeedbackStaffHistoryQuery, Result<AttendancePage<AttendanceHistory>>>
{ public Task<Result<AttendancePage<AttendanceHistory>>> Handle(FeedbackStaffHistoryQuery r, CancellationToken ct) => s.StaffHistoryAsync(r.Id, r.Page, r.PageSize, ct); }
