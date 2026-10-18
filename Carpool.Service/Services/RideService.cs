using AutoMapper;
using Carpool.Core.Dtos.Rides;
using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Core.Interfaces.Services;

namespace Carpool.Service.Services;

/// <summary>
/// Implements <see cref="IRideService"/> (spec §10–§20). All ride business rules live here:
/// the driver must own the vehicle, seat counts must fit the vehicle, tag ids must be valid,
/// and the cancel/complete state transitions cascade to the ride's active bookings inside a
/// transaction (spec §14, §18, §68). There is deliberately no update path — a ride cannot be
/// edited after creation (spec §13).
/// </summary>
public class RideService : IRideService
{
    private readonly IRideRepository _rideRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public RideService(
        IRideRepository rideRepository,
        IVehicleRepository vehicleRepository,
        ITagRepository tagRepository,
        IUnitOfWork unitOfWork,
        IMapper mapper)
    {
        _rideRepository = rideRepository;
        _vehicleRepository = vehicleRepository;
        _tagRepository = tagRepository;
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<RideResponse> CreateAsync(int currentUserId, CreateRideRequest request, CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleRepository.GetByIdAsync(request.VehicleId, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {request.VehicleId} was not found.");

        // The driver must own the selected vehicle — no Admin bypass, the driver drives
        // their own car (spec §12).
        if (vehicle.OwnerId != currentUserId)
        {
            throw new ForbiddenException("You can only create a ride with a vehicle you own.");
        }

        // Seat rule (spec §11): 0 < TotalSeats <= vehicle capacity.
        if (request.TotalSeats <= 0 || request.TotalSeats > vehicle.PassengerCapacity)
        {
            throw new BusinessRuleException(
                $"TotalSeats must be between 1 and the vehicle's passenger capacity ({vehicle.PassengerCapacity}).");
        }

        // Not part of the base spec — a deliberate extension: a ride can't be offered for
        // a departure time that has already passed.
        if (request.DepartureTime <= DateTime.UtcNow)
        {
            throw new BusinessRuleException("DepartureTime must be in the future.");
        }

        var ride = _mapper.Map<Ride>(request);
        ride.DriverId = currentUserId;
        ride.AvailableSeats = request.TotalSeats;
        ride.Status = RideStatus.Scheduled;
        ride.CreatedAt = DateTime.UtcNow;

        var tagIds = request.TagIds.Distinct().ToList();
        if (tagIds.Count > 0)
        {
            var tags = (await _tagRepository.GetByIdsAsync(tagIds, cancellationToken)).ToList();

            // A tag id must exist and be either global or privately owned by this driver
            // (spec extension — see Tag). Both failures report the same message: a tag
            // owned by someone else must not be distinguishable from one that doesn't exist.
            var usable = tags.Count == tagIds.Count && tags.All(t => t.OwnerId == null || t.OwnerId == currentUserId);
            if (!usable)
            {
                throw new BusinessRuleException("One or more tag ids are invalid.");
            }

            foreach (var tag in tags)
            {
                ride.RideTags.Add(new RideTag { TagId = tag.Id });
            }
        }

        await _rideRepository.AddAsync(ride, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Reload with driver/vehicle/tags so the response projection is complete (spec §32).
        var created = await _rideRepository.GetDetailedByIdAsync(ride.Id, cancellationToken);
        return _mapper.Map<RideResponse>(created!);
    }

    public async Task<RideResponse> GetByIdAsync(int rideId, CancellationToken cancellationToken)
    {
        var ride = await _rideRepository.GetDetailedByIdAsync(rideId, cancellationToken)
            ?? throw new NotFoundException($"Ride {rideId} was not found.");

        return _mapper.Map<RideResponse>(ride);
    }

    public async Task<RideListResponse> SearchAsync(RideQueryParameters query, CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _rideRepository.SearchAsync(query, cancellationToken);

        return new RideListResponse
        {
            Items = _mapper.Map<List<RideResponse>>(items),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<RideResponse> CancelAsync(int rideId, int currentUserId, UserRole currentUserRole, CancellationToken cancellationToken)
    {
        // Read, authorization, status check and save in ONE transaction (see BookingService).
        await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var ride = await _rideRepository.GetByIdAsync(rideId, token)
                ?? throw new NotFoundException($"Ride {rideId} was not found.");

            EnsureDriverOrAdmin(ride, currentUserId, currentUserRole);

            // Normal cancellation is allowed only while the ride is still Scheduled — not once
            // it is InProgress (spec §18).
            if (ride.Status != RideStatus.Scheduled)
            {
                throw new BusinessRuleException("Only a scheduled ride can be cancelled.");
            }

            var now = DateTime.UtcNow;
            ride.Status = RideStatus.Cancelled;

            // All active bookings are cancelled in the same transaction; historical booking
            // rows are kept (spec §18, §73).
            foreach (var booking in ride.Bookings.Where(b => b.Status == BookingStatus.Active))
            {
                booking.Status = BookingStatus.Cancelled;
                booking.CancelledAt = now;
            }

            await _unitOfWork.SaveChangesAsync(token);
        }, cancellationToken);

        var updated = await _rideRepository.GetDetailedByIdAsync(rideId, cancellationToken);
        return _mapper.Map<RideResponse>(updated!);
    }

    public async Task<RideResponse> CompleteAsync(int rideId, int currentUserId, UserRole currentUserRole, CancellationToken cancellationToken)
    {
        // Read, authorization, eligibility check and save in ONE transaction (see BookingService).
        await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var ride = await _rideRepository.GetByIdAsync(rideId, token)
                ?? throw new NotFoundException($"Ride {rideId} was not found.");

            EnsureDriverOrAdmin(ride, currentUserId, currentUserRole);

            var now = DateTime.UtcNow;

            // Eligibility (spec §17). A ride that is still Scheduled is acceptable only when its
            // departure time has already passed — i.e. the BackgroundService simply has not
            // advanced it to InProgress yet. Completing a ride that has not departed, or one
            // that is already Completed/Cancelled, is rejected.
            switch (ride.Status)
            {
                case RideStatus.InProgress:
                    break;
                case RideStatus.Scheduled when ride.DepartureTime <= now:
                    break;
                case RideStatus.Scheduled:
                    throw new BusinessRuleException("The ride has not departed yet and cannot be completed.");
                default:
                    throw new BusinessRuleException($"A {ride.Status.ToString().ToLowerInvariant()} ride cannot be completed.");
            }

            ride.Status = RideStatus.Completed;
            ride.CompletedAt = now;
            ride.StartedAt ??= now;

            // All active bookings transition to Completed in the same transaction (spec §14).
            foreach (var booking in ride.Bookings.Where(b => b.Status == BookingStatus.Active))
            {
                booking.Status = BookingStatus.Completed;
            }

            await _unitOfWork.SaveChangesAsync(token);
        }, cancellationToken);

        var updated = await _rideRepository.GetDetailedByIdAsync(rideId, cancellationToken);
        return _mapper.Map<RideResponse>(updated!);
    }

    /// <summary>
    /// Only the ride's own driver may cancel/complete it; an Admin may act on any ride
    /// (spec §17, §18, §43).
    /// </summary>
    private static void EnsureDriverOrAdmin(Ride ride, int currentUserId, UserRole currentUserRole)
    {
        if (ride.DriverId != currentUserId && currentUserRole != UserRole.Admin)
        {
            throw new ForbiddenException("You do not have permission to modify this ride.");
        }
    }
}
