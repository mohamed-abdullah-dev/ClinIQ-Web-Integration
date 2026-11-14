using ClinIQ.Application.Features.Notifications;
using ClinIQ.Application.Features.Notifications.Commands;
using ClinIQ.Application.Features.Notifications.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace ClinIQ.API.Controllers;
[Authorize(Policy = "PatientMobile")]
[Route("api/patients/me/push-devices")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16 * 1024)]
public sealed class PushDevicesController(ISender sender) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType<PushDeviceDto>(200)]
    public async Task<IActionResult> Register(RegisterPushDeviceInput input, CancellationToken ct)
    { var r = await sender.Send(new RegisterPushDeviceCommand(input), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PushDeviceDto>>(200)]
    public async Task<IActionResult> List(CancellationToken ct)
    { var r = await sender.Send(new MyPushDevicesQuery(), ct); return r.Match<IActionResult>(Ok, Problem); }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Detach(Guid id, CancellationToken ct)
    { var r = await sender.Send(new DetachPushDeviceCommand(id), ct); return r.Match<IActionResult>(_ => NoContent(), Problem); }
}
