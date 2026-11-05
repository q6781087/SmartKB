using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

/// <summary>
/// 运行时配置中心（仅管理员）：系统提示词、检索/重排/生成参数。
/// 保存后立即对新问答生效，无需重启、无需改代码重新发版。
/// 连接串与 ApiKey 不在此处——只允许环境变量注入。
/// </summary>
[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(ISettingsService settings) : ControllerBase
{
    private bool IsNotAdmin => !User.IsAdmin();

    /// <summary>配置项列表（含默认值、当前生效值、来源）</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await settings.GetAllAsync(ct));

    /// <summary>批量保存；key 白名单 + 类型/范围校验在 SettingsService 内强制</summary>
    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] Dictionary<string, JsonElement> updates, CancellationToken ct)
    {
        if (IsNotAdmin) return Forbid();
        var effective = await settings.UpdateAsync(updates, ct);
        return Ok(new { effective, message = "配置已保存，后续问答立即生效" });
    }

    /// <summary>恢复默认（删除所有 rag.* 覆盖记录）</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await settings.ResetAsync(ct));
}
