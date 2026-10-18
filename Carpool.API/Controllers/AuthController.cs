using Carpool.Core.Dtos.Auth;
using Carpool.Core.Dtos.Users;
using Carpool.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carpool.API.Controllers;

/// <summary>
/// Registration and login (spec §39). Both endpoints are anonymous — they are how a caller
/// obtains a token in the first place. All logic (email uniqueness, hashing, credential
/// verification, token issue) lives in <see cref="IAuthService"/>; this controller only
/// binds the request and returns the response (spec §36).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public class AuthController : ApiControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>POST /api/auth/register — create a new user account (role is always <c>User</c>). Anonymous.</summary>
    [HttpPost("register")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _authService.RegisterAsync(request, cancellationToken);
        return CreatedAtAction(nameof(UsersController.GetMe), "Users", null, user);
    }

    /// <summary>POST /api/auth/login — verify credentials and return a signed JWT plus the user profile. Anonymous.</summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return Ok(result);
    }
}
