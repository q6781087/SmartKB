using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Roles = "admin")]
public class RolesController(IRoleService roleService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RoleDto>>> GetList()
        => Ok(await roleService.GetListAsync());

    [HttpPost]
    public async Task<ActionResult<RoleDto>> Create(CreateRoleRequest request)
        => Ok(await roleService.CreateAsync(request));

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await roleService.DeleteAsync(id);
        return Ok(new { message = "角色已删除" });
    }
}
