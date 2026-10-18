using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Carpool.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBookingRepository"/> (spec §21–§26, §52). Registered
/// Scoped alongside <see cref="CarpoolDbContext"/>.
///
/// Repositories only stage changes (<see cref="AddAsync"/>, <see cref="Update"/>); the commit
/// happens through <see cref="IUnitOfWork.SaveChangesAsync"/> (spec §68). Booking rows are
/// never removed — cancelling changes the status (spec §21).
/// </summary>
public class BookingRepository : IBookingRepository
{
    private readonly CarpoolDbContext _context;

    public BookingRepository(CarpoolDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Tracked load including the parent <see cref="Ride"/>. The cancel flow returns the
    /// booked seats to the ride and checks that the ride is still Scheduled (spec §25).
    /// </summary>
    public Task<Booking?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Bookings
            .Include(b => b.Ride)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    /// <summary>Read-only load for <c>GET /api/bookings/{id}</c> (spec §20).</summary>
    public Task<Booking?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken) =>
        _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    /// <summary>Read-only listing backing <c>GET /api/bookings/my</c> (spec §20).</summary>
    public async Task<IEnumerable<Booking>> GetByPassengerAsync(int passengerId, CancellationToken cancellationToken) =>
        await _context.Bookings
            .AsNoTracking()
            .Where(b => b.PassengerId == passengerId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>True if the passenger already holds an Active booking on the ride (spec §26).</summary>
    public Task<bool> HasActiveBookingAsync(int rideId, int passengerId, CancellationToken cancellationToken) =>
        _context.Bookings.AnyAsync(
            b => b.RideId == rideId
                 && b.PassengerId == passengerId
                 && b.Status == BookingStatus.Active,
            cancellationToken);

    /// <summary>
    /// Tracked load of every Active booking on a ride. The ride cancel / complete flows
    /// transition these to Cancelled / Completed inside the same transaction (spec §14, §18).
    /// </summary>
    public async Task<IEnumerable<Booking>> GetActiveByRideAsync(int rideId, CancellationToken cancellationToken) =>
        await _context.Bookings
            .Where(b => b.RideId == rideId && b.Status == BookingStatus.Active)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Read-only listing (any status) backing the driver's <c>GET /api/rides/{rideId}/bookings</c>
    /// view. Loads the passenger in the same query to avoid N+1 (spec §20, extended).
    /// </summary>
    public async Task<IEnumerable<Booking>> GetByRideAsync(int rideId, CancellationToken cancellationToken) =>
        await _context.Bookings
            .AsNoTracking()
            .Include(b => b.Passenger)
            .Where(b => b.RideId == rideId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken) =>
        await _context.Bookings.AddAsync(booking, cancellationToken);

    public void Update(Booking booking) =>
        _context.Bookings.Update(booking);
}
