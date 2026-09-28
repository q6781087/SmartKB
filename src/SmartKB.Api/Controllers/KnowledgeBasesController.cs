using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

[ApiController]
[Route("api/kbs")]
[Authorize]
public class KnowledgeBasesController(
    IKnowledgeBaseService kbService,
    IPermissionService permissionService) : ControllerBase
{
    /// <summary>知识库列表：非管理员只返回被授权的库</summary>
    [HttpGet]
    public async Task<ActionResult<List<KnowledgeBaseDto>>> GetList()
        => Ok(await kbService.GetListAsync(User.GetUserId(), User.IsAdmin()));

    [HttpPost]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<ActionResult<KnowledgeBaseDto>> Create(SaveKnowledgeBaseRequest request)
        => Ok(await kbService.CreateAsync(request));

    [HttpPut("{id:long}")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<ActionResult<KnowledgeBaseDto>> Update(long id, SaveKnowledgeBaseRequest request)
        => Ok(await kbService.UpdateAsync(id, request));

    [HttpDelete("{id:long}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(long id)
    {
        await kbService.DeleteAsync(id);
        return Ok(new { message = "知识库已删除" });
    }

    // ---------- 授权管理 ----------

    [HttpGet("{kbId:long}/permissions")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<ActionResult<List<PermissionDto>>> GetPermissions(long kbId)
        => Ok(await permissionService.GetKbPermissionsAsync(kbId));

    [HttpPost("permissions")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<IActionResult> Grant(GrantPermissionRequest request)
    {
        await permissionService.GrantAsync(request);
        return Ok(new { message = "授权成功" });
    }

    [HttpDelete("permissions/{permissionId:long}")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<IActionResult> Revoke(long permissionId)
    {
        await permissionService.RevokeAsync(permissionId);
        return Ok(new { message = "已回收授权" });
    }
}
