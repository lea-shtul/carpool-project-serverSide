using Carpool.Core.Dtos.Rides;
using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Carpool.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRideRepository"/> (spec §10, §15, §19, §20, §52).
/// Registered Scoped alongside <see cref="CarpoolDbContext"/>.
///
/// Reads that feed a response projection use <c>AsNoTracking</c> plus explicit
/// <c>Include</c>/<c>ThenInclude</c> to avoid N+1 (spec §20, §32). State-changing loads are
/// tracked because the entity (and its bookings) will be modified. Filtering, sorting and
/// paging for <see cref="SearchAsync"/> all run in the database via LINQ +
/// <c>Skip</c>/<c>Take</c> — the table is never pulled into memory first (spec §65).
/// </summary>
public class RideRepository : IRideRepository
{
    private readonly CarpoolDbContext _context;

    public RideRepository(CarpoolDbContext context)
    {
        _context = context;
    }

    /// <summary>Tracked load including <c>Bookings</c>, for the booking / cancel / complete flows.</summary>
    public Task<Ride?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Rides
            .Include(r => r.Bookings)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <summary>Read-only load with driver, vehicle and tags, for <c>GET /api/rides/{id}</c> (spec §32, §64).</summary>
    public Task<Ride?> GetDetailedByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Rides
            .AsNoTracking()
            .Include(r => r.Driver)
            .Include(r => r.Vehicle)
            .Include(r => r.RideTags)
                .ThenInclude(rt => rt.Tag)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <summary>Read-only load, no includes — ownership/existence checks that don't modify the ride.</summary>
    public Task<Ride?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken) =>
        _context.Rides
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    /// <summary>
    /// Filtered, sorted, database-paged search for <c>GET /api/rides</c> (spec §20, §65).
    /// The total count is taken before paging so the caller can build paging metadata.
    /// </summary>
    public async Task<(IEnumerable<Ride> Items, int TotalCount)> SearchAsync(
        RideQueryParameters query,
        CancellationToken cancellationToken)
    {
        IQueryable<Ride> rides = _context.Rides
            .AsNoTracking()
            .Include(r => r.Driver)
            .Include(r => r.Vehicle)
            .Include(r => r.RideTags)
                .ThenInclude(rt => rt.Tag);

        if (!string.IsNullOrWhiteSpace(query.Origin))
        {
            rides = rides.Where(r => EF.Functions.ILike(r.Origin, $"%{query.Origin}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Destination))
        {
            rides = rides.Where(r => EF.Functions.ILike(r.Destination, $"%{query.Destination}%"));
        }

        if (query.TagId.HasValue)
        {
            rides = rides.Where(r => r.RideTags.Any(rt => rt.TagId == query.TagId.Value));
        }

        if (query.MinPrice.HasValue)
        {
            rides = rides.Where(r => r.PricePerSeat >= query.MinPrice.Value);
        }

        if (query.MaxPrice.HasValue)
        {
            rides = rides.Where(r => r.PricePerSeat <= query.MaxPrice.Value);
        }

        if (query.AvailableOnly)
        {
            // Still bookable: Scheduled, not yet departed, seats left. Full rides are
            // hidden from this list but remain in the database (spec §19).
            var now = DateTime.UtcNow;
            rides = rides.Where(r =>
                r.Status == RideStatus.Scheduled
                && r.DepartureTime > now
                && r.AvailableSeats > 0);
        }

        var totalCount = await rides.CountAsync(cancellationToken);

        var descending = string.Equals(query.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        rides = query.SortBy?.ToLowerInvariant() switch
        {
            "price" => descending
                ? rides.OrderByDescending(r => r.PricePerSeat)
                : rides.OrderBy(r => r.PricePerSeat),
            "availableseats" => descending
                ? rides.OrderByDescending(r => r.AvailableSeats)
                : rides.OrderBy(r => r.AvailableSeats),
            _ => descending
                ? rides.OrderByDescending(r => r.DepartureTime)
                : rides.OrderBy(r => r.DepartureTime),
        };

        // Stable secondary order so paging is deterministic when the sort key ties.
        rides = ((IOrderedQueryable<Ride>)rides).ThenBy(r => r.Id);

        var items = await rides
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Ride ride, CancellationToken cancellationToken) =>
        await _context.Rides.AddAsync(ride, cancellationToken);

    /// <summary>
    /// Scheduled rides whose departure time has passed — tracked, for automatic
    /// Scheduled → InProgress (spec §15).
    /// </summary>
    public async Task<IEnumerable<Ride>> GetRidesDueToStartAsync(DateTime utcNow, CancellationToken cancellationToken) =>
        await _context.Rides
            .Where(r => r.Status == RideStatus.Scheduled && r.DepartureTime <= utcNow)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// InProgress rides whose departure + estimated duration has passed, with their
    /// bookings — tracked, for automatic InProgress → Completed plus booking completion
    /// (spec §14, §15).
    /// </summary>
    public async Task<IEnumerable<Ride>> GetRidesDueToCompleteAsync(DateTime utcNow, CancellationToken cancellationToken) =>
        await _context.Rides
            .Include(r => r.Bookings)
            .Where(r => r.Status == RideStatus.InProgress
                       && r.DepartureTime.AddMinutes(r.EstimatedDurationMinutes) <= utcNow)
            .ToListAsync(cancellationToken);
}
