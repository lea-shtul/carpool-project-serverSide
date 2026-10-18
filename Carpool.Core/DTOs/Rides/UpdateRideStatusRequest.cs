using System.ComponentModel.DataAnnotations;
using Carpool.Core.Enums;

namespace Carpool.Core.Dtos.Rides;

/// <summary>
/// Body of <c>PATCH /api/rides/{id}</c> (spec §17, §18). The only state transitions a
/// caller may request manually are Cancel and Complete — Scheduled/InProgress are reached
/// only by the system (ride creation, <see cref="Carpool.Core.Interfaces.Services.IRideStatusService"/>).
/// </summary>
public class UpdateRideStatusRequest
{
    [AllowedValues(RideStatus.Cancelled, RideStatus.Completed)]
    public RideStatus Status { get; set; }
}
