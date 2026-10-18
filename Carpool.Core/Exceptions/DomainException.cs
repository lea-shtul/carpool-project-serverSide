namespace Carpool.Core.Exceptions;

/// <summary>
/// Base type for expected business failures raised by the Service layer. The API's
/// global exception middleware maps each concrete subtype to the right HTTP status
/// code (spec §71) — these must never surface as 500.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }
}

/// <summary>Requested resource does not exist → <c>404 Not Found</c>.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Authentication failed — invalid credentials or a disabled account → <c>401 Unauthorized</c>
/// (spec §39, §71). Distinct from <see cref="ForbiddenException"/>: here the caller is not
/// (successfully) authenticated at all.
/// </summary>
public sealed class UnauthorizedException : DomainException
{
    public UnauthorizedException(string message) : base(message)
    {
    }
}

/// <summary>Caller is authenticated but not allowed to act on this resource → <c>403 Forbidden</c>.</summary>
public sealed class ForbiddenException : DomainException
{
    public ForbiddenException(string message) : base(message)
    {
    }
}

/// <summary>A business rule was violated (e.g. not enough seats, ride already started) → <c>400 Bad Request</c>.</summary>
public sealed class BusinessRuleException : DomainException
{
    public BusinessRuleException(string message) : base(message)
    {
    }
}

/// <summary>
/// A resource conflict: duplicate active booking, duplicate rating, or a vehicle
/// still referenced by a ride → <c>409 Conflict</c> (spec §71).
/// </summary>
public class ConflictException : DomainException
{
    public ConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// A concurrent request modified the ride first; EF Core raised
/// <c>DbUpdateConcurrencyException</c> and the Data layer translated it to this →
/// <c>409 Conflict</c> (spec §23, §24).
/// </summary>
public sealed class ConcurrencyConflictException : ConflictException
{
    public ConcurrencyConflictException(
        string message = "This ride was just changed by someone else — for example, a seat was booked or its status changed. Please refresh and try again.")
        : base(message)
    {
    }
}
