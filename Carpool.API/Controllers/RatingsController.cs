using Carpool.Core.Dtos.Ratings;
using Carpool.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carpool.API.Controllers;

/// <summary>
/// Passenger-to-driver ratings (spec §27–§29). Eligibility (ride completed, reviewer had a
/// completed booking, not the driver, one rating per ride/reviewer) is enforced in
/// <see cref="IRatingService"/>. Routes span <c>/api/rides/...</c> and <c>/api/users/...</c>,
/// so the controller uses <c>[Route("api")]</c> with explicit per-action templates.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public class RatingsController : ApiControllerBase
{
    private readonly IRatingService _ratingService;

    public RatingsController(IRatingService ratingService)
    {
        _ratingService = ratingService;
    }

    /// <summary>POST /api/rides/{rideId}/ratings — rate the driver of a completed ride you had a completed booking on.</summary>
    [HttpPost("rides/{rideId:int}/ratings")]
    [ProducesResponseType<RatingResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RatingResponse>> Create(
        [FromRoute] int rideId,
        [FromBody] CreateRatingRequest request,
        CancellationToken cancellationToken)
    {
        var rating = await _ratingService.CreateAsync(rideId, CurrentUserId, request, cancellationToken);
        return CreatedAtAction(nameof(GetForUser), new { userId = rating.DriverId }, rating);
    }

    /// <summary>GET /api/users/{userId}/ratings — ratings that user received as a driver.</summary>
    [HttpGet("users/{userId:int}/ratings")]
    public async Task<ActionResult<IEnumerable<RatingResponse>>> GetForUser(
        [FromRoute] int userId,
        CancellationToken cancellationToken)
    {
        var ratings = await _ratingService.GetForUserAsync(userId, cancellationToken);
        return Ok(ratings);
    }
}
