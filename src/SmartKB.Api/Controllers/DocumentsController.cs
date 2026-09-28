using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Api;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

[ApiController]
[Route("api/kbs/{kbId:long}/documents")]
[Authorize]
public class DocumentsController(IDocumentService documentService) : ControllerBase
{
    /// <summary>上传文档（multipart/form-data），进入异步解析流水线</summary>
    [HttpPost]
    [Authorize(Roles = "admin,kb_manager")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 单文件 50MB 上限
    public async Task<ActionResult<DocumentDto>> Upload(
        long kbId, IFormFile file, [FromForm] string? metadata)
    {
        if (file.Length == 0)
            throw new BusinessRuleException("文件为空");

        await using var stream = file.OpenReadStream();
        var result = await documentService.UploadAsync(
            kbId, stream, file.FileName, User.GetUserId(), metadata);
        return Ok(result);
    }

    /// <summary>文档列表（含解析状态与分块数）</summary>
    [HttpGet]
    public async Task<ActionResult<List<DocumentDto>>> GetList(long kbId)
        => Ok(await documentService.GetListAsync(kbId));

    /// <summary>失败重试</summary>
    [HttpPost("{documentId:long}/retry")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<IActionResult> Retry(long kbId, long documentId)
    {
        await documentService.RetryAsync(documentId);
        return Ok(new { message = "已重新排队处理" });
    }

    /// <summary>删除文档（含向量与台账数据清理）</summary>
    [HttpDelete("{documentId:long}")]
    [Authorize(Roles = "admin,kb_manager")]
    public async Task<IActionResult> Delete(long kbId, long documentId)
    {
        await documentService.DeleteAsync(documentId);
        return Ok(new { message = "文档已删除" });
    }
}
