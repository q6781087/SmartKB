using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

/// <summary>
/// 配置化 Agent 工具管理（仅管理员）：登记一个内部 HTTP 接口，问答时 LLM 可按需调用。
/// 密钥不经过本接口——只能由运维在服务端设置环境变量，这里只登记变量名。
/// 所有写操作会被全局 AuditLogFilter 自动记录（只记元数据，不记请求体）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/agent-tools")]
public class AgentToolsController(IAgentToolService tools) : ControllerBase
{
    private bool IsNotAdmin => !User.IsAdmin();

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.ListAsync(ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgentToolUpsertDto dto, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.CreateAsync(dto, ct));

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AgentToolUpsertDto dto, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.UpdateAsync(id, dto, ct));

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        if (IsNotAdmin) return Forbid();
        await tools.DeleteAsync(id, ct);
        return Ok(new { message = "已删除" });
    }

    /// <summary>启停（不必打开编辑表单）</summary>
    [HttpPatch("{id:long}/enabled")]
    public async Task<IActionResult> SetEnabled(
        long id, [FromBody] SetEnabledDto body, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.SetEnabledAsync(id, body.Enabled, ct));

    /// <summary>试运行：用给定参数真实调用一次，返回上游文本（会真实产生副作用，仅用于只读接口）</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] AgentToolTestDto dto, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.TestAsync(dto, ct));

    /// <summary>查看调用白名单（空 = 公开）</summary>
    [HttpGet("{id:long}/grants")]
    public async Task<IActionResult> GetGrants(long id, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.GetGrantsAsync(id, ct));

    /// <summary>全量覆盖调用白名单；传空列表恢复公开</summary>
    [HttpPut("{id:long}/grants")]
    public async Task<IActionResult> SetGrants(
        long id, [FromBody] SetToolGrantsDto dto, CancellationToken ct)
        => IsNotAdmin ? Forbid() : Ok(await tools.SetGrantsAsync(id, dto, ct));
}

public record SetEnabledDto(bool Enabled);
