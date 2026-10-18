namespace Carpool.API.Middleware;

/// <summary>
/// The single, consistent error body returned for every error (spec §54) — thrown
/// exceptions via <see cref="ExceptionHandlingMiddleware"/>, and model-validation failures
/// caught by <c>[ApiController]</c> via <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c>
/// (<c>Program.cs</c>), which builds this same shape instead of the framework's default
/// RFC 7807 <c>ProblemDetails</c>.
/// </summary>
/// <param name="StatusCode">The HTTP status code also set on the response.</param>
/// <param name="Message">
/// A safe, client-facing message. For mapped domain exceptions this is the exception's own
/// business message; for unexpected errors it is a generic string — internal details are
/// never exposed (spec §54).
/// </param>
/// <param name="CorrelationId">The request's correlation id, so a client report ties to the server logs (spec §55).</param>
public sealed record ErrorResponse(int StatusCode, string Message, string CorrelationId);
