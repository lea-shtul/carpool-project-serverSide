using Carpool.Core.Dtos.Bookings;
using Carpool.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carpool.API.Controllers;

/// <summary>
/// Seat booking and cancellation (spec §22–§26). Creating a booking is a seat-limited,
/// concurrency-guarded operation handled in <see cref="IBookingService"/>; cancellation is
/// owner-only. Routes span two prefixes (<c>/api/rides/{rideId}/bookings</c> and
/// <c>/api/bookings/...</c>), so the controller uses <c>[Route("api")]</c> with explicit
/// per-action templates.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public class BookingsController : ApiControllerBase
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    /// <summary>POST /api/rides/{rideId}/bookings — book seats on a ride (optimistic-concurrency guarded).</summary>
    [HttpPost("rides/{rideId:int}/bookings")]
    [ProducesResponseType<BookingResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BookingResponse>> Create(
        [FromRoute] int rideId,
        [FromBody] CreateBookingRequest request,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.CreateAsync(rideId, CurrentUserId, request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = booking.Id }, booking);
    }

    /// <summary>
    /// GET /api/rides/{rideId}/bookings — every booking on the ride, passenger names
    /// included. The ride's driver, or an Admin, only. Not part of the base spec.
    /// </summary>
    [HttpGet("rides/{rideId:int}/bookings")]
    public async Task<ActionResult<IEnumerable<RideBookingResponse>>> GetForRide(
        [FromRoute] int rideId,
        CancellationToken cancellationToken)
    {
        var bookings = await _bookingService.GetForRideAsync(rideId, CurrentUserId, CurrentUserRole, cancellationToken);
        return Ok(bookings);
    }

    /// <summary>GET /api/bookings/my — the authenticated user's bookings.</summary>
    [HttpGet("bookings/my")]
    public async Task<ActionResult<IEnumerable<BookingResponse>>> GetMine(CancellationToken cancellationToken)
    {
        var bookings = await _bookingService.GetMineAsync(CurrentUserId, cancellationToken);
        return Ok(bookings);
    }

    /// <summary>GET /api/bookings/{id} — one booking. Owner or Admin only.</summary>
    [HttpGet("bookings/{id:int}")]
    public async Task<ActionResult<BookingResponse>> GetById(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetByIdAsync(id, CurrentUserId, CurrentUserRole, cancellationToken);
        return Ok(booking);
    }

    /// <summary>
    /// DELETE /api/bookings/{id} — cancel your own active booking (soft delete: the row
    /// stays, with status Cancelled); seats are returned to the ride.
    /// </summary>
    [HttpDelete("bookings/{id:int}")]
    public async Task<IActionResult> Cancel(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        await _bookingService.CancelAsync(id, CurrentUserId, cancellationToken);
        return NoContent();
    }
}
