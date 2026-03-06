using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.StaticFiles;
using StackExchange.Redis;
using System.Text.Json;

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MonitorController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public MonitorController(ApplicationDbContext context)
    {
        _context = context;
    }

    // ==========================================
    // 1. 取得 AI 系統判定統計數據 (對應 api.getAiStatistics)
    // ==========================================
    [HttpGet("statistics")]
    public async Task<IActionResult> GetAiStatistics()
    {
        // 🌟 修正：必須 Include(Status) 才能讀取 Code
        var logs = await _context.AiAnalysisLogs
            .Include(l => l.Status)
            .Select(l => l.Status!.Code)
            .ToListAsync();

        var successStatuses = new[] { "OUTPUT", "COMPLETED", "MATCH_VSTACK" };
        var skipStatuses = new[] { "SKIP", "REJECTED", "NOFACE", "GARBAGE", "AMBIGUOUS", "UNCERTAIN" };

        var successCount = logs.Count(status => successStatuses.Contains(status));
        var skipCount = logs.Count(status => skipStatuses.Contains(status));

        return Ok(new { successCount = successCount, skipCount = skipCount });
    }

    // ==========================================
    // 2. 取得系統告警清單 (對應 api.getSystemAlerts)
    // ==========================================
    [HttpGet("alerts")]
    public async Task<IActionResult> GetSystemAlerts()
    {
        // 🌟 修正：必須 Include(AlertType) 才能讀取 Code
        var alerts = await _context.SystemAlerts
            .Include(a => a.AlertType)
            .Where(a => !a.IsResolved)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new
            {
                id = a.Id,
                type = a.AlertType!.Code, // 🌟 修正為讀取 Code
                message = $"[{a.SourceComponent}] {a.Message}", 
                timestamp = a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            })
            .ToListAsync();

        return Ok(alerts);
    }
}