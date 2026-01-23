// SessionsController：提供会话列表/创建接口（读取与可选写入 ~/.codex/sessions）。
using codex_bridge_server.Bridge;
using Microsoft.AspNetCore.Mvc;

namespace codex_bridge_server.Controllers;

[ApiController]
[Route("api/v1/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly BridgeRequestAuthorizer _authorizer;
    private readonly CodexSessionStore _sessionStore;
    private readonly CodexTurnPlanStore _turnPlanStore;

    public SessionsController(BridgeRequestAuthorizer authorizer, CodexSessionStore sessionStore, CodexTurnPlanStore turnPlanStore)
    {
        _authorizer = authorizer;
        _sessionStore = sessionStore;
        _turnPlanStore = turnPlanStore;
    }

    [HttpGet]
    public IActionResult List([FromQuery] int? limit)
    {
        if (!_authorizer.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        var sessions = _sessionStore.ListRecent(limit ?? 30);
        return Ok(sessions);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CodexSessionCreateRequest? request)
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        var cwd = request?.Cwd?.Trim();
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return BadRequest(new { message = "cwd 不能为空" });
        }

        if (!Directory.Exists(cwd))
        {
            return BadRequest(new { message = $"cwd 目录不存在或不可访问: {cwd}" });
        }

        try
        {
            var session = _sessionStore.Create(cwd);
            return Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{sessionId}/messages")]
    public IActionResult GetMessages([FromRoute] string sessionId, [FromQuery] int? limit)
    {
        if (!_authorizer.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { message = "sessionId 不能为空" });
        }

        var messages = _sessionStore.ReadMessages(sessionId, limit ?? 200);
        if (messages is null)
        {
            return NotFound();
        }

        return Ok(messages);
    }

    [HttpGet("{sessionId}/plan")]
    public IActionResult GetLatestPlan([FromRoute] string sessionId)
    {
        if (!_authorizer.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { message = "sessionId 不能为空" });
        }

        if (!_turnPlanStore.TryGet(sessionId.Trim(), out var snapshot))
        {
            return NotFound();
        }

        return Ok(snapshot);
    }

    [HttpGet("{sessionId}/settings")]
    public IActionResult GetLatestSettings([FromRoute] string sessionId)
    {
        if (!_authorizer.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { message = "sessionId 不能为空" });
        }

        var snapshot = _sessionStore.TryReadLatestSettings(sessionId.Trim());
        if (snapshot is null)
        {
            return NotFound();
        }

        return Ok(snapshot);
    }

    [HttpDelete("{sessionId}")]
    public IActionResult Delete([FromRoute] string sessionId)
    {
        if (!_authorizer.IsManagementAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { message = "sessionId 不能为空" });
        }

        var success = _sessionStore.Delete(sessionId);
        if (!success)
        {
            return NotFound(new { message = $"未找到会话或删除失败: {sessionId}" });
        }

        return Ok(new { message = "会话已删除", sessionId });
    }
}
