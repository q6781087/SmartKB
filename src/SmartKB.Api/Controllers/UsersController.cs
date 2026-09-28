using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "admin")]
public class UsersController(IUserService userService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var (items, total) = await userService.GetListAsync(keyword, page, pageSize);
        return Ok(new { items, total, page, pageSize });
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request)
        => Ok(await userService.CreateAsync(request));

    [HttpPut("{id:long}")]
    public async Task<ActionResult<UserDto>> Update(long id, UpdateUserRequest request)
        => Ok(await userService.UpdateAsync(id, request));

    [HttpPut("{id:long}/password")]
    public async Task<IActionResult> ResetPassword(long id, ResetPasswordRequest request)
    {
        await userService.ResetPasswordAsync(id, request.NewPassword);
        return Ok(new { message = "密码已重置" });
    }
}
