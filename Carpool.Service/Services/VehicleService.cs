using AutoMapper;
using Carpool.Core.Dtos.Vehicles;
using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Core.Interfaces.Services;

namespace Carpool.Service.Services;

/// <summary>
/// Implements <see cref="IVehicleService"/> (spec §8, §9, §43). All ownership checks run
/// here, against the acting user's id/role passed in from the JWT — never a client-supplied
/// identity. Licence-plate uniqueness is enforced here (backed by the unique index), and a
/// vehicle referenced by any ride cannot be deleted (spec §9).
/// </summary>
public class VehicleService : IVehicleService
{
    private readonly IVehicleRepository _vehicleRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public VehicleService(IVehicleRepository vehicleRepository, IUnitOfWork unitOfWork, IMapper mapper)
    {
        _vehicleRepository = vehicleRepository;
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<VehicleResponse> CreateAsync(int currentUserId, CreateVehicleRequest request, CancellationToken cancellationToken)
    {
        if (await _vehicleRepository.LicensePlateExistsAsync(request.LicensePlate, null, cancellationToken))
        {
            throw new ConflictException($"Licence plate '{request.LicensePlate}' is already registered.");
        }

        var vehicle = _mapper.Map<Vehicle>(request);
        vehicle.OwnerId = currentUserId; // from the JWT, never the request body (spec §43)

        await _vehicleRepository.AddAsync(vehicle, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return _mapper.Map<VehicleResponse>(vehicle);
    }

    public async Task<VehicleResponse> GetByIdAsync(int vehicleId, int currentUserId, UserRole currentUserRole, CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleRepository.GetByIdNoTrackingAsync(vehicleId, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {vehicleId} was not found.");

        EnsureOwnerOrAdmin(vehicle, currentUserId, currentUserRole);

        return _mapper.Map<VehicleResponse>(vehicle);
    }

    public async Task<IEnumerable<VehicleResponse>> GetMineAsync(int currentUserId, CancellationToken cancellationToken)
    {
        var vehicles = await _vehicleRepository.GetByOwnerAsync(currentUserId, cancellationToken);
        return _mapper.Map<IEnumerable<VehicleResponse>>(vehicles);
    }

    public async Task<VehicleResponse> UpdateAsync(
        int vehicleId,
        int currentUserId,
        UserRole currentUserRole,
        UpdateVehicleRequest request,
        CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleRepository.GetByIdAsync(vehicleId, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {vehicleId} was not found.");

        EnsureOwnerOrAdmin(vehicle, currentUserId, currentUserRole);

        if (await _vehicleRepository.LicensePlateExistsAsync(request.LicensePlate, vehicleId, cancellationToken))
        {
            throw new ConflictException($"Licence plate '{request.LicensePlate}' is already registered.");
        }

        _mapper.Map(request, vehicle);
        _vehicleRepository.Update(vehicle);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return _mapper.Map<VehicleResponse>(vehicle);
    }

    public async Task DeleteAsync(int vehicleId, int currentUserId, UserRole currentUserRole, CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleRepository.GetByIdAsync(vehicleId, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {vehicleId} was not found.");

        EnsureOwnerOrAdmin(vehicle, currentUserId, currentUserRole);

        if (await _vehicleRepository.IsReferencedByAnyRideAsync(vehicleId, cancellationToken))
        {
            // Applies regardless of ride status — historical rides keep their vehicle (spec §9, §73).
            throw new ConflictException("This vehicle is referenced by one or more rides and cannot be deleted.");
        }

        _vehicleRepository.Remove(vehicle);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A normal user may only act on their own vehicle; an Admin may act on any (spec §9, §43).
    /// </summary>
    private static void EnsureOwnerOrAdmin(Vehicle vehicle, int currentUserId, UserRole currentUserRole)
    {
        if (vehicle.OwnerId != currentUserId && currentUserRole != UserRole.Admin)
        {
            throw new ForbiddenException("You do not have permission to access this vehicle.");
        }
    }
}
