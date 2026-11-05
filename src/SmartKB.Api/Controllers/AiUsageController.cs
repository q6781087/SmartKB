using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

/// <summary>
/// AI 调用观测统计（仅管理员）：每次问答的 token 用量、响应耗时、工具调用等。
/// </summary>
[ApiController]
[Authorize]
[Route("api/ai-usage")]
public class AiUsageController(IAiUsageService usage) : ControllerBase
{
    private bool IsNotAdmin => !User.IsAdmin();

    /// <summary>汇总（近 N 天）：调用次数、成功率、token 总量、平均首字/总耗时、活跃用户数</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int days = 30)
        => IsNotAdmin ? Forbid() : Ok(await usage.GetSummaryAsync(Math.Clamp(days, 1, 365)));

    /// <summary>按天聚合（近 N 天，升序），供前端趋势图</summary>
    [HttpGet("daily")]
    public async Task<IActionResult> Daily([FromQuery] int days = 30)
        => IsNotAdmin ? Forbid() : Ok(await usage.GetDailyAsync(Math.Clamp(days, 1, 365)));

    /// <summary>最近调用明细（新→旧，默认 50 条）</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 50)
        => IsNotAdmin ? Forbid() : Ok(await usage.GetRecentAsync(limit));
}
