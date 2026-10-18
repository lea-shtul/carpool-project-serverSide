using AutoMapper;
using Carpool.Core.Dtos.Vehicles;
using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Service.Services;
using Carpool.Tests.TestKit;
using Moq;

namespace Carpool.Tests.Services;

/// <summary>
/// Tests for <see cref="VehicleService"/> — server-side ownership (spec §43), licence-plate
/// uniqueness (spec §8) and the rule that a vehicle referenced by any ride cannot be
/// deleted (spec §9).
/// </summary>
public class VehicleServiceTests
{
    private const int Owner = 1;
    private const int Other = 2;
    private const int VehicleId = 5;

    private readonly Mock<IVehicleRepository> _vehicles = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = TestMocks.UnitOfWork();
    private readonly IMapper _mapper = TestMapper.Create();

    private VehicleService CreateSut() => new(_vehicles.Object, _unitOfWork.Object, _mapper);

    private void VehicleExists(Vehicle vehicle)
    {
        _vehicles.Setup(v => v.GetByIdAsync(vehicle.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vehicle);
        _vehicles.Setup(v => v.GetByIdNoTrackingAsync(vehicle.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vehicle);
    }

    private static CreateVehicleRequest CreateRequest(string plate = "NEW-1") => new()
    {
        Manufacturer = "Make",
        Model = "Model",
        LicensePlate = plate,
        PassengerCapacity = 4,
    };

    // --- CreateAsync -----------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_SetsOwnerFromCallerAndPersists()
    {
        _vehicles.Setup(v => v.LicensePlateExistsAsync("NEW-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Vehicle? added = null;
        _vehicles.Setup(v => v.AddAsync(It.IsAny<Vehicle>(), It.IsAny<CancellationToken>()))
            .Callback<Vehicle, CancellationToken>((v, _) => added = v)
            .Returns(Task.CompletedTask);

        var result = await CreateSut().CreateAsync(Owner, CreateRequest(), default);

        Assert.NotNull(added);
        Assert.Equal(Owner, added!.OwnerId);
        Assert.Equal("NEW-1", added.LicensePlate);
        Assert.Equal(Owner, result.OwnerId);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenLicensePlateTaken_ThrowsConflict()
    {
        _vehicles.Setup(v => v.LicensePlateExistsAsync("NEW-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<ConflictException>(() => CreateSut().CreateAsync(Owner, CreateRequest(), default));
        _vehicles.Verify(v => v.AddAsync(It.IsAny<Vehicle>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- GetByIdAsync ---------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ThrowsNotFound()
    {
        _vehicles.Setup(v => v.GetByIdNoTrackingAsync(VehicleId, It.IsAny<CancellationToken>())).ReturnsAsync((Vehicle?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateSut().GetByIdAsync(VehicleId, Owner, UserRole.User, default));
    }

    [Fact]
    public async Task GetByIdAsync_WhenNeitherOwnerNorAdmin_ThrowsForbidden()
    {
        VehicleExists(TestData.Vehicle(VehicleId, Owner));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateSut().GetByIdAsync(VehicleId, Other, UserRole.User, default));
    }

    [Fact]
    public async Task GetByIdAsync_AsAdmin_ReturnsAnotherUsersVehicle()
    {
        VehicleExists(TestData.Vehicle(VehicleId, Owner));

        var result = await CreateSut().GetByIdAsync(VehicleId, Other, UserRole.Admin, default);

        Assert.Equal(VehicleId, result.Id);
    }

    // --- UpdateAsync --------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_WhenOwner_AppliesChangesAndPersists()
    {
        var vehicle = TestData.Vehicle(VehicleId, Owner);
        VehicleExists(vehicle);
        _vehicles.Setup(v => v.LicensePlateExistsAsync("UPD-1", VehicleId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var request = new UpdateVehicleRequest
        {
            Manufacturer = "NewMake",
            Model = "NewModel",
            LicensePlate = "UPD-1",
            PassengerCapacity = 6,
        };

        var result = await CreateSut().UpdateAsync(VehicleId, Owner, UserRole.User, request, default);

        Assert.Equal("NewMake", vehicle.Manufacturer);
        Assert.Equal("UPD-1", vehicle.LicensePlate);
        Assert.Equal(6, vehicle.PassengerCapacity);
        Assert.Equal(Owner, vehicle.OwnerId); // unchanged
        Assert.Equal("UPD-1", result.LicensePlate);
        _vehicles.Verify(v => v.Update(vehicle), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WhenNotOwnerNorAdmin_ThrowsForbidden()
    {
        VehicleExists(TestData.Vehicle(VehicleId, Owner));

        var request = new UpdateVehicleRequest { Manufacturer = "M", Model = "M", LicensePlate = "X", PassengerCapacity = 4 };

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateSut().UpdateAsync(VehicleId, Other, UserRole.User, request, default));
    }

    [Fact]
    public async Task UpdateAsync_WhenNewPlateBelongsToAnotherVehicle_ThrowsConflict()
    {
        VehicleExists(TestData.Vehicle(VehicleId, Owner));
        _vehicles.Setup(v => v.LicensePlateExistsAsync("TAKEN", VehicleId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var request = new UpdateVehicleRequest { Manufacturer = "M", Model = "M", LicensePlate = "TAKEN", PassengerCapacity = 4 };

        await Assert.ThrowsAsync<ConflictException>(() =>
            CreateSut().UpdateAsync(VehicleId, Owner, UserRole.User, request, default));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- DeleteAsync -------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_WhenNotReferencedByAnyRide_RemovesVehicle()
    {
        var vehicle = TestData.Vehicle(VehicleId, Owner);
        VehicleExists(vehicle);
        _vehicles.Setup(v => v.IsReferencedByAnyRideAsync(VehicleId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().DeleteAsync(VehicleId, Owner, UserRole.User, default);

        _vehicles.Verify(v => v.Remove(vehicle), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenReferencedByARide_ThrowsConflictAndDoesNotRemove()
    {
        var vehicle = TestData.Vehicle(VehicleId, Owner);
        VehicleExists(vehicle);
        _vehicles.Setup(v => v.IsReferencedByAnyRideAsync(VehicleId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            CreateSut().DeleteAsync(VehicleId, Owner, UserRole.User, default));
        _vehicles.Verify(v => v.Remove(It.IsAny<Vehicle>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotOwnerNorAdmin_ThrowsForbidden()
    {
        VehicleExists(TestData.Vehicle(VehicleId, Owner));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateSut().DeleteAsync(VehicleId, Other, UserRole.User, default));
    }

    // --- GetMineAsync ---------------------------------------------------------

    [Fact]
    public async Task GetMineAsync_ReturnsOnlyTheCallersVehicles()
    {
        _vehicles.Setup(v => v.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Vehicle> { TestData.Vehicle(1, Owner), TestData.Vehicle(2, Owner) });

        var result = (await CreateSut().GetMineAsync(Owner, default)).ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, v => Assert.Equal(Owner, v.OwnerId));
    }
}
