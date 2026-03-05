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
        // 抓取全域的 AI 分析日誌 [3]
        var logs = await _context.AiAnalysisLogs
            .Select(l => l.RecognitionStatus)
            .ToListAsync();

        // 定義哪些狀態屬於「成功」，哪些屬於「跳過/失敗」
        // 依據 S2_Worker 的判定邏輯與 S4 狀態回寫 [8, 9]
        var successStatuses = new[] { "OUTPUT", "COMPLETED", "MATCH_VSTACK" };
        var skipStatuses = new[] { "SKIP", "REJECTED", "NOFACE", "GARBAGE", "AMBIGUOUS", "UNCERTAIN" };

        var successCount = logs.Count(status => successStatuses.Contains(status));
        var skipCount = logs.Count(status => skipStatuses.Contains(status));

        // 回傳對應 Vue3 期待的 JSON 格式 [1]
        return Ok(new 
        { 
            successCount = successCount, 
            skipCount = skipCount 
        });
    }

    // ==========================================
    // 2. 取得系統告警清單 (對應 api.getSystemAlerts)
    // ==========================================
    [HttpGet("alerts")]
    public async Task<IActionResult> GetSystemAlerts()
    {
        // 撈取尚未解決的系統告警，依照時間由新到舊排序 [4, 6]
        var alerts = await _context.SystemAlerts
            .Where(a => !a.IsResolved)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5) // 取最新 5 筆即可
            .Select(a => new
            {
                id = a.Id,
                type = a.AlertType,
                message = $"[{a.SourceComponent}] {a.Message}", // 組合來源與訊息
                timestamp = a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            })
            .ToListAsync();

        return Ok(alerts);
    }
}