using Carpool.Core.Dtos.Tags;
using Carpool.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Carpool.API.Controllers;

/// <summary>
/// Tag listing and creation (spec §30, extended — see <c>Carpool.Core.Entities.Tag</c>).
/// Authenticated only: every other endpoint in this API that could use a tag list
/// (<c>RidesController</c>, ride creation) already requires login, so there is no anonymous
/// consumer left to support, and the response now depends on the caller's identity (it
/// includes their private tags).
/// </summary>
[ApiController]
[Authorize]
[Route("api/tags")]
public class TagsController : ApiControllerBase
{
    private readonly ITagService _tagService;

    public TagsController(ITagService tagService)
    {
        _tagService = tagService;
    }

    /// <summary>GET /api/tags — global tags plus the authenticated caller's own private tags.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TagResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var tags = await _tagService.GetVisibleToAsync(CurrentUserId, cancellationToken);
        return Ok(tags);
    }

    /// <summary>POST /api/tags — create a private tag owned by the authenticated caller.</summary>
    [HttpPost]
    [ProducesResponseType<TagResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TagResponse>> Create(
        [FromBody] CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var tag = await _tagService.CreateAsync(CurrentUserId, request, cancellationToken);
        return CreatedAtAction(nameof(GetAll), null, tag);
    }
}
