using Carpool.Core.Entities;

namespace Carpool.Core.Interfaces.Repositories;

/// <summary>Persistence contract for <see cref="Booking"/> (spec §21, §22).</summary>
public interface IBookingRepository
{
    /// <summary>Tracked load including the parent <c>Ride</c>, for the cancel flow (spec §25).</summary>
    Task<Booking?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Read-only load (<c>AsNoTracking</c>) for <c>GET /api/bookings/{id}</c> (spec §20).</summary>
    Task<Booking?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken);

    Task<IEnumerable<Booking>> GetByPassengerAsync(int passengerId, CancellationToken cancellationToken);

    /// <summary>True if the passenger already has an Active booking on the ride (spec §26).</summary>
    Task<bool> HasActiveBookingAsync(int rideId, int passengerId, CancellationToken cancellationToken);

    /// <summary>All Active bookings for a ride — used when a ride is cancelled or completed (spec §14, §18).</summary>
    Task<IEnumerable<Booking>> GetActiveByRideAsync(int rideId, CancellationToken cancellationToken);

    /// <summary>
    /// Every booking on a ride (any status), passenger included, for the driver's "who
    /// booked my ride" view. Not part of the base spec — see SPEC-COMPLIANCE.md.
    /// </summary>
    Task<IEnumerable<Booking>> GetByRideAsync(int rideId, CancellationToken cancellationToken);

    Task AddAsync(Booking booking, CancellationToken cancellationToken);

    void Update(Booking booking);
}
