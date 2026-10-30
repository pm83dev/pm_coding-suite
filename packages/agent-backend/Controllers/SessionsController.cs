using Microsoft.AspNetCore.Mvc;
using LocalCodeAgent.Models;
using AgentBackend.Services;
using AgentBackend.Models.Dto;

namespace AgentBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly AgentSessionService _sessionService;
    private readonly AgentConfig _config;

    public SessionsController(AgentSessionService sessionService, AgentConfig config)
    {
        _sessionService = sessionService;
        _config = config;
    }

    [HttpGet]
    public ActionResult<List<SessionDto>> List()
    {
        return Ok(_sessionService.ListSessions());
    }

    [HttpGet("{sessionId}")]
    public ActionResult<SessionDto> Get(string sessionId)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null) return NotFound(new { error = "Session not found" });
        return Ok(new SessionDto
        {
            Id = session.SessionId, Title = session.Title,
            Workspace = session.WorkspaceRoot,
            UpdatedAt = session.UpdatedAt.ToString("o"),
            Status = session.Status, Model = session.Model
        });
    }

    [HttpDelete("{sessionId}")]
    public ActionResult Delete(string sessionId)
    {
        _sessionService.DeleteSession(sessionId);
        return Ok();
    }

    [HttpGet("config")]
    public ActionResult<ConfigDto> GetConfig()
    {
        return Ok(new ConfigDto
        {
            Llm = new LlmConfigDto
            {
                BaseUrl = _config.LlmSettings.BaseUrl,
                Model = _config.LlmSettings.Model,
                Temperature = _config.LlmSettings.Temperature,
                FrequencyPenalty = _config.LlmSettings.FrequencyPenalty
            },
            Agent = new AgentConfigDto
            {
                MaxSteps = _config.AgentSettings.MaxSteps,
                MaxContextTokens = _config.AgentSettings.MaxContextTokens,
                RolloverTokens = _config.AgentSettings.RolloverTokens,
                MaxLinesPerFile = _config.AgentSettings.MaxLinesPerFile
            },
            WebSearch = new WebSearchConfigDto
            {
                BraveApiKey = string.IsNullOrEmpty(_config.WebSearchSettings.BraveApiKey) ? "" : "***configured***"
            }
        });
    }
}
