using AutoMapper;
using Carpool.Core.Dtos.Bookings;
using Carpool.Core.Entities;
using Carpool.Core.Enums;
using Carpool.Core.Exceptions;
using Carpool.Core.Interfaces.Repositories;
using Carpool.Service.Services;
using Carpool.Tests.TestKit;
using Moq;

namespace Carpool.Tests.Services;

/// <summary>
/// Business-rule tests for <see cref="BookingService"/> — covers the minimum booking
/// scenarios required by spec §70 (successful booking, insufficient seats, driver cannot
/// book own ride, duplicate active booking, booking cancellation) plus the surrounding
/// guards. Repositories are mocked; the unit of work runs the transactional body.
/// </summary>
public class BookingServiceTests
{
    private const int Driver = 1;
    private const int Passenger = 2;
    private const int RideId = 10;

    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IRideRepository> _rides = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = TestMocks.UnitOfWork();
    private readonly IMapper _mapper = TestMapper.Create();

    private BookingService CreateSut() => new(_bookings.Object, _rides.Object, _unitOfWork.Object, _mapper);

    private void RideExists(Ride ride)
    {
        _rides.Setup(r => r.GetByIdAsync(ride.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ride);
        _rides.Setup(r => r.GetByIdNoTrackingAsync(ride.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ride);
    }

    // --- CreateAsync -------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_WithAvailableSeats_DecrementsSeatsAndInsertsBooking()
    {
        var ride = TestData.Ride(RideId, Driver, availableSeats: 4, totalSeats: 4);
        RideExists(ride);
        _bookings.Setup(b => b.HasActiveBookingAsync(RideId, Passenger, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 2 }, default);

        Assert.Equal(2, ride.AvailableSeats);
        Assert.Equal(2, result.NumberOfSeats);
        Assert.Equal(BookingStatus.Active, result.Status);
        _bookings.Verify(b => b.AddAsync(
            It.Is<Booking>(x => x.RideId == RideId && x.PassengerId == Passenger && x.NumberOfSeats == 2 && x.Status == BookingStatus.Active),
            It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenNotEnoughSeats_ThrowsBusinessRuleAndDoesNotInsert()
    {
        var ride = TestData.Ride(RideId, Driver, availableSeats: 1, totalSeats: 4);
        RideExists(ride);
        _bookings.Setup(b => b.HasActiveBookingAsync(RideId, Passenger, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 2 }, default));

        Assert.Equal(1, ride.AvailableSeats);
        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenBookerIsTheDriver_ThrowsBusinessRule()
    {
        var ride = TestData.Ride(RideId, Driver);
        RideExists(ride);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateSut().CreateAsync(RideId, currentUserId: Driver, new CreateBookingRequest { NumberOfSeats = 1 }, default));

        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenPassengerAlreadyHasActiveBooking_ThrowsConflict()
    {
        var ride = TestData.Ride(RideId, Driver);
        RideExists(ride);
        _bookings.Setup(b => b.HasActiveBookingAsync(RideId, Passenger, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 1 }, default));

        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenRideDoesNotExist_ThrowsNotFound()
    {
        _rides.Setup(r => r.GetByIdAsync(RideId, It.IsAny<CancellationToken>())).ReturnsAsync((Ride?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 1 }, default));
    }

    [Theory]
    [InlineData(RideStatus.InProgress)]
    [InlineData(RideStatus.Completed)]
    [InlineData(RideStatus.Cancelled)]
    public async Task CreateAsync_WhenRideIsNotScheduled_ThrowsBusinessRule(RideStatus status)
    {
        var ride = TestData.Ride(RideId, Driver, status: status);
        RideExists(ride);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 1 }, default));
    }

    [Fact]
    public async Task CreateAsync_WhenRideAlreadyDeparted_ThrowsBusinessRule()
    {
        var ride = TestData.Ride(RideId, Driver, departureTime: DateTime.UtcNow.AddMinutes(-5));
        RideExists(ride);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 1 }, default));
    }

    [Fact]
    public async Task CreateAsync_WhenNumberOfSeatsNotPositive_ThrowsBusinessRule()
    {
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateSut().CreateAsync(RideId, Passenger, new CreateBookingRequest { NumberOfSeats = 0 }, default));
    }

    // --- CancelAsync ------------------------------------------------------------------

    [Fact]
    public async Task CancelAsync_WhenActiveAndRideScheduled_CancelsAndReturnsSeats()
    {
        var ride = TestData.Ride(RideId, Driver, availableSeats: 2, totalSeats: 4);
        var booking = TestData.Booking(7, RideId, Passenger, numberOfSeats: 2);
        booking.Ride = ride;
        _bookings.Setup(b => b.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        var result = await CreateSut().CancelAsync(7, Passenger, default);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.NotNull(booking.CancelledAt);
        Assert.Equal(4, ride.AvailableSeats);
        Assert.Equal(BookingStatus.Cancelled, result.Status);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAsync_WhenNotTheOwner_ThrowsForbidden()
    {
        var booking = TestData.Booking(7, RideId, Passenger);
        booking.Ride = TestData.Ride(RideId, Driver);
        _bookings.Setup(b => b.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        await Assert.ThrowsAsync<ForbiddenException>(() => CreateSut().CancelAsync(7, currentUserId: 999, default));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelAsync_WhenBookingNotActive_ThrowsBusinessRule()
    {
        var booking = TestData.Booking(7, RideId, Passenger, status: BookingStatus.Cancelled);
        booking.Ride = TestData.Ride(RideId, Driver);
        _bookings.Setup(b => b.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        await Assert.ThrowsAsync<BusinessRuleException>(() => CreateSut().CancelAsync(7, Passenger, default));
    }

    [Fact]
    public async Task CancelAsync_WhenRideNoLongerScheduled_ThrowsBusinessRule()
    {
        var booking = TestData.Booking(7, RideId, Passenger);
        booking.Ride = TestData.Ride(RideId, Driver, status: RideStatus.InProgress);
        _bookings.Setup(b => b.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        await Assert.ThrowsAsync<BusinessRuleException>(() => CreateSut().CancelAsync(7, Passenger, default));
    }

    [Fact]
    public async Task CancelAsync_WhenBookingNotFound_ThrowsNotFound()
    {
        _bookings.Setup(b => b.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync((Booking?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateSut().CancelAsync(7, Passenger, default));
    }

    // --- GetByIdAsync ---------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_WhenNeitherOwnerNorAdmin_ThrowsForbidden()
    {
        var booking = TestData.Booking(7, RideId, Passenger);
        _bookings.Setup(b => b.GetByIdNoTrackingAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateSut().GetByIdAsync(7, currentUserId: 999, UserRole.User, default));
    }

    [Fact]
    public async Task GetByIdAsync_AsAdmin_ReturnsBookingOfAnotherUser()
    {
        var booking = TestData.Booking(7, RideId, Passenger);
        _bookings.Setup(b => b.GetByIdNoTrackingAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(booking);

        var result = await CreateSut().GetByIdAsync(7, currentUserId: 999, UserRole.Admin, default);

        Assert.Equal(7, result.Id);
    }

    // --- GetForRideAsync (spec extension — driver's view of a ride's bookings) --------

    [Fact]
    public async Task GetForRideAsync_AsTheDriver_ReturnsBookingsWithPassengerNames()
    {
        var ride = TestData.Ride(RideId, Driver);
        RideExists(ride);
        var booking = TestData.Booking(7, RideId, Passenger, numberOfSeats: 2);
        _bookings.Setup(b => b.GetByRideAsync(RideId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { booking });

        var result = (await CreateSut().GetForRideAsync(RideId, Driver, UserRole.User, default)).ToList();

        Assert.Single(result);
        Assert.Equal(2, result[0].NumberOfSeats);
        Assert.Equal($"First{Passenger}", result[0].Passenger.FirstName);
        Assert.Equal(Passenger, result[0].Passenger.Id);
    }

    [Fact]
    public async Task GetForRideAsync_AsAdmin_ReturnsBookingsOfSomeoneElsesRide()
    {
        var ride = TestData.Ride(RideId, Driver);
        RideExists(ride);
        _bookings.Setup(b => b.GetByRideAsync(RideId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { TestData.Booking(7, RideId, Passenger) });

        var result = await CreateSut().GetForRideAsync(RideId, currentUserId: 999, UserRole.Admin, default);

        Assert.Single(result);
    }

    [Fact]
    public async Task GetForRideAsync_WhenCallerIsNeitherDriverNorAdmin_ThrowsForbidden()
    {
        var ride = TestData.Ride(RideId, Driver);
        RideExists(ride);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateSut().GetForRideAsync(RideId, currentUserId: Passenger, UserRole.User, default));

        _bookings.Verify(b => b.GetByRideAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetForRideAsync_WhenRideDoesNotExist_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateSut().GetForRideAsync(RideId, Driver, UserRole.User, default));
    }
}
