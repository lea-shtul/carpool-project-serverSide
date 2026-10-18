using Carpool.Core.Entities;

namespace Carpool.Core.Interfaces.Repositories;

/// <summary>Persistence contract for <see cref="Vehicle"/> (spec §8, §9).</summary>
public interface IVehicleRepository
{
    Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>Read-only load (<c>AsNoTracking</c>) for <c>GET /api/vehicles/{id}</c> (spec §20).</summary>
    Task<Vehicle?> GetByIdNoTrackingAsync(int id, CancellationToken cancellationToken);

    Task<IEnumerable<Vehicle>> GetByOwnerAsync(int ownerId, CancellationToken cancellationToken);

    /// <summary><paramref name="excludeVehicleId"/> lets an update ignore the vehicle's own plate.</summary>
    Task<bool> LicensePlateExistsAsync(string licensePlate, int? excludeVehicleId, CancellationToken cancellationToken);

    /// <summary>True if any ride (of any status) references this vehicle — blocks deletion (spec §9, §73).</summary>
    Task<bool> IsReferencedByAnyRideAsync(int vehicleId, CancellationToken cancellationToken);

    Task AddAsync(Vehicle vehicle, CancellationToken cancellationToken);

    void Update(Vehicle vehicle);

    void Remove(Vehicle vehicle);
}
