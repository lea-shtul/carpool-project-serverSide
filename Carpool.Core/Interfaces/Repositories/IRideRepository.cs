using Carpool.Core.Dtos.Rides;
using Carpool.Core.Entities;

namespace Carpool.Core.Interfaces.Repositories;

/// <summary>Persistence contract for <see cref="Ride"/> (spec §10, §20).</summary>
public interface IRideRepository
{
    /// <summary>
    /// Tracked load including <c>Bookings</c>, for state-changing flows
    /// (booking, cancel, complete). No <c>AsNoTracking</c> — the entity will be modified.
    /// </summary>
    Task<Ride?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only load (<c>AsNoTracking</c>) including Driver, Vehicle and Tags,
    /// for <c>GET /api/rides/{id}</c> (spec §64).
    /// </summary>
    Task<Ride?> GetDetailedByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only load (<c>AsNoTracking</c>), no includes — for existence/ownership checks
    /// that do not modify the ride, e.g. the driver's "who booked my ride" view (spec §20).
    /// </summary>
    Task<Ride?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Filtered, sorted, database-paged search for <c>GET /api/rides</c>. Paging is
    /// done with <c>Skip</c>/<c>Take</c> in the database and the total count is
    /// returned alongside the page (spec §20, §65).
    /// </summary>
    Task<(IEnumerable<Ride> Items, int TotalCount)> SearchAsync(RideQueryParameters query, CancellationToken cancellationToken);

    Task AddAsync(Ride ride, CancellationToken cancellationToken);

    /// <summary>Scheduled rides whose departure time has passed — for automatic Scheduled → InProgress (spec §15).</summary>
    Task<IEnumerable<Ride>> GetRidesDueToStartAsync(DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>
    /// InProgress rides whose departure + estimated duration has passed, including
    /// their Bookings — for automatic InProgress → Completed plus booking completion (spec §14, §15).
    /// </summary>
    Task<IEnumerable<Ride>> GetRidesDueToCompleteAsync(DateTime utcNow, CancellationToken cancellationToken);
}
