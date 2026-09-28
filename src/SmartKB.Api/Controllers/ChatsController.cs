using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Controllers;

/// <summary>问答会话管理（流式问答走 /hubs/chat）</summary>
[Authorize]
[ApiController]
[Route("api/chats")]
public class ChatsController(IChatService chat) : ControllerBase
{
    /// <summary>我的会话列表</summary>
    [HttpGet]
    public async Task<ActionResult<List<ChatSessionDto>>> GetSessions() =>
        await chat.GetSessionsAsync(User.GetUserId());

    /// <summary>新建会话</summary>
    [HttpPost]
    public async Task<ActionResult<ChatSessionDto>> Create([FromBody] CreateSessionRequest request) =>
        await chat.CreateSessionAsync(User.GetUserId(), request);

    /// <summary>会话历史消息</summary>
    [HttpGet("{id:long}/messages")]
    public async Task<ActionResult<List<ChatMessageDto>>> GetMessages(long id) =>
        await chat.GetMessagesAsync(User.GetUserId(), id);

    /// <summary>删除会话</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await chat.DeleteSessionAsync(User.GetUserId(), id);
        return NoContent();
    }
}
