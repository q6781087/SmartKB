using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService, IUserService userService) : ControllerBase
{
    /// <summary>登录获取 JWT</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
        => Ok(await authService.LoginAsync(request));

    /// <summary>当前用户信息</summary>
    [HttpGet("me")]
    [Authorize]
    public ActionResult<CurrentUserDto> Me()
        => Ok(new CurrentUserDto(
            User.GetUserId(),
            User.Identity?.Name ?? string.Empty,
            User.FindFirstValue("displayName") ?? string.Empty,
            User.IsAdmin(),
            User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList()));

    /// <summary>修改自己的密码（用户自助）</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        await userService.ChangePasswordAsync(User.GetUserId(), request.OldPassword, request.NewPassword);
        return Ok(new { message = "密码已修改" });
    }
}
