using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

/// <summary>操作审计日志查询（仅管理员）</summary>
[ApiController]
[Authorize]
[Route("api/audit-logs")]
public class AuditLogsController(IAuditService audit) : ControllerBase
{
    private bool IsNotAdmin => !User.IsAdmin();

    /// <summary>分页查询：支持按分类、用户、成败、关键字、时间范围过滤</summary>
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null,
        [FromQuery] long? userId = null,
        [FromQuery] bool? success = null,
        [FromQuery] string? keyword = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (IsNotAdmin) return Forbid();
        var result = await audit.QueryAsync(
            new AuditLogQuery(page, pageSize, category, userId, success, keyword, from, to), ct);
        return Ok(result);
    }
}
