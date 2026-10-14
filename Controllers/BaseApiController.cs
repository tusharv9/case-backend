namespace CaseManagement.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using System;

[ApiController]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    protected Guid CurrentUserId
    {
        get
        {
            if (HttpContext.Items.TryGetValue("UserId", out var userIdObj) && userIdObj is Guid userId)
            {
                return userId;
            }
            throw new UnauthorizedAccessException("User ID is missing from the context.");
        }
    }

    protected Guid GetCurrentUserIdSafe()
    {
        if (HttpContext.Items.TryGetValue("UserId", out var userIdObj) && userIdObj is Guid userId)
        {
            return userId;
        }
        return Guid.Empty;
    }
}
