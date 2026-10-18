using Carpool.Core.Entities;
using Carpool.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Carpool.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IVehicleRepository"/> (spec §8, §9, §52). Registered
/// Scoped alongside <see cref="CarpoolDbContext"/>.
///
/// Repositories only stage changes (<see cref="AddAsync"/>, <see cref="Update"/>,
/// <see cref="Remove"/>); the commit happens through <see cref="IUnitOfWork.SaveChangesAsync"/>
/// (spec §68). Read-only queries use <c>AsNoTracking</c> (spec §20).
/// </summary>
public class VehicleRepository : IVehicleRepository
{
    private readonly CarpoolDbContext _context;

    public VehicleRepository(CarpoolDbContext context)
    {
        _context = context;
    }

    /// <summary>Tracked load — the update and delete flows mutate/remove the returned entity.</summary>
    public Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Vehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    /// <summary>Read-only load for <c>GET /api/vehicles/{id}</c> (spec §20).</summary>
    public Task<Vehicle?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken) =>
        _context.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    /// <summary>Read-only load backing <c>GET /api/vehicles/my</c> (spec §9, §20).</summary>
    public async Task<IEnumerable<Vehicle>> GetByOwnerAsync(int ownerId, CancellationToken cancellationToken) =>
        await _context.Vehicles
            .AsNoTracking()
            .Where(v => v.OwnerId == ownerId)
            .OrderBy(v => v.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// True if the plate is already taken. <paramref name="excludeVehicleId"/> lets an
    /// update ignore the vehicle's own current plate (spec §8).
    /// </summary>
    public Task<bool> LicensePlateExistsAsync(string licensePlate, int? excludeVehicleId, CancellationToken cancellationToken) =>
        _context.Vehicles.AnyAsync(
            v => v.LicensePlate == licensePlate
                 && (excludeVehicleId == null || v.Id != excludeVehicleId.Value),
            cancellationToken);

    /// <summary>
    /// True if any ride references this vehicle, regardless of ride status. Deletion is
    /// blocked while this holds, so historical Ride → Vehicle links are preserved
    /// (spec §9, §73).
    /// </summary>
    public Task<bool> IsReferencedByAnyRideAsync(int vehicleId, CancellationToken cancellationToken) =>
        _context.Rides.AnyAsync(r => r.VehicleId == vehicleId, cancellationToken);

    public async Task AddAsync(Vehicle vehicle, CancellationToken cancellationToken) =>
        await _context.Vehicles.AddAsync(vehicle, cancellationToken);

    public void Update(Vehicle vehicle) =>
        _context.Vehicles.Update(vehicle);

    public void Remove(Vehicle vehicle) =>
        _context.Vehicles.Remove(vehicle);
}
