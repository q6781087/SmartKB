using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request);
}

/// <summary>登录认证：BCrypt 校验 + JWT 签发</summary>
public class AuthService(SmartKbDbContext db, ITokenService tokenService) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await db.Users.Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Username == request.Username)
            ?? throw new BusinessRuleException("用户名或密码错误");

        if (!user.IsActive)
            throw new BusinessRuleException("账号已禁用，请联系管理员");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new BusinessRuleException("用户名或密码错误");

        var token = tokenService.GenerateToken(user);
        return new LoginResponse(token, 720,
            new CurrentUserDto(user.Id, user.Username, user.DisplayName,
                user.IsAdmin, user.Roles.Select(r => r.Name).ToList()));
    }
}
