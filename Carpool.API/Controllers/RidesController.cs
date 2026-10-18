using Carpool.Core.Dtos.Rides;
using Carpool.Core.Enums;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carpool.API.Controllers;

/// <summary>
/// Ride creation, search and state transitions (spec §12–§20). There is deliberately no
/// <c>PUT</c> — a ride cannot be edited after creation (spec §13). The only state transitions
/// are a driver-or-Admin <c>PATCH /api/rides/{id}</c> (Cancel/Complete), enforced in
/// <see cref="IRideService"/>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/rides")]
public class RidesController : ApiControllerBase
{
    private readonly IRideService _rideService;

    public RidesController(IRideService rideService)
    {
        _rideService = rideService;
    }

    /// <summary>POST /api/rides — create a ride on a vehicle the caller owns.</summary>
    [HttpPost]
    [ProducesResponseType<RideResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RideResponse>> Create(
        [FromBody] CreateRideRequest request,
        CancellationToken cancellationToken)
    {
        var ride = await _rideService.CreateAsync(CurrentUserId, request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = ride.Id }, ride);
    }

    /// <summary>GET /api/rides — search rides with filtering, sorting and paging.</summary>
    [HttpGet]
    public async Task<ActionResult<RideListResponse>> Search(
        [FromQuery] RideQueryParameters query,
        CancellationToken cancellationToken)
    {
        var page = await _rideService.SearchAsync(query, cancellationToken);
        return Ok(page);
    }

    /// <summary>GET /api/rides/{id} — one ride with driver, vehicle and tags.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<RideResponse>> GetById(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var ride = await _rideService.GetByIdAsync(id, cancellationToken);
        return Ok(ride);
    }

    /// <summary>
    /// PATCH /api/rides/{id} — the only two manual state transitions (driver or Admin):
    /// <c>{"status":"Cancelled"}</c> cancels a scheduled ride (active bookings are cancelled
    /// too), <c>{"status":"Completed"}</c> completes it (active bookings are completed too).
    /// </summary>
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<RideResponse>> UpdateStatus(
        [FromRoute] int id,
        [FromBody] UpdateRideStatusRequest request,
        CancellationToken cancellationToken)
    {
        var ride = request.Status switch
        {
            RideStatus.Cancelled => await _rideService.CancelAsync(id, CurrentUserId, CurrentUserRole, cancellationToken),
            RideStatus.Completed => await _rideService.CompleteAsync(id, CurrentUserId, CurrentUserRole, cancellationToken),
            _ => throw new BusinessRuleException("Unsupported status transition."),
        };
        return Ok(ride);
    }
}
