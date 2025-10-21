using Microsoft.AspNetCore.Mvc;

namespace QAChatOps.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArtifactsController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public ArtifactsController(IWebHostEnvironment env)
    {
        _env = env;
    }

    // GET /api/artifacts/video/{jobId}.webm
    [HttpGet("video/{fileName}")]
    public IActionResult GetVideo(string fileName)
    {
        var filePath = Path.Combine(_env.ContentRootPath, "wwwroot", "artifacts", "videos", fileName);
        if (!System.IO.File.Exists(filePath)) return NotFound();
        return PhysicalFile(filePath, "video/webm", enableRangeProcessing: true);
    }

    // GET /api/artifacts/trace/{fileName}
    [HttpGet("trace/{fileName}")]
    public IActionResult GetTrace(string fileName)
    {
        var filePath = Path.Combine(_env.ContentRootPath, "wwwroot", "artifacts", "traces", fileName);
        if (!System.IO.File.Exists(filePath)) return NotFound();
        return PhysicalFile(filePath, "application/zip");
    }

    // GET /api/artifacts/report/{jobId}
    [HttpGet("report/{jobId}")]
    public IActionResult GetReport(string jobId)
    {
        var baseDir = Path.Combine(_env.ContentRootPath, "wwwroot", "artifacts");

        // screenshots
        var screenshotsDir = Path.Combine(baseDir, "screenshots");
        var screenshots = new List<string>();
        if (Directory.Exists(screenshotsDir))
        {
            screenshots = Directory.GetFiles(screenshotsDir)
                .Where(f => Path.GetFileName(f).StartsWith(jobId))
                .Select(f => Url.Content($"/artifacts/screenshots/{Path.GetFileName(f)}"))
                .ToList();
        }

        // video
        var videoPath = Path.Combine(baseDir, "videos", $"{jobId}.webm");
        var hasVideo = System.IO.File.Exists(videoPath);
        var videoUrl = hasVideo ? Url.Action("GetVideo", "Artifacts", new { fileName = Path.GetFileName(videoPath) }) : null;

        // trace
        var traceDir = Path.Combine(baseDir, "traces");
        string? traceFile = null;
        if (Directory.Exists(traceDir))
        {
            var match = Directory.GetFiles(traceDir).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(jobId));
            if (match != null) traceFile = Path.GetFileName(match);
        }
        var traceUrl = traceFile != null ? Url.Action("GetTrace", "Artifacts", new { fileName = traceFile }) : null;

        // Build a nicely styled HTML report
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n<title>Test Report</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{--bg:#f6f9fc;--card:#ffffff;--muted:#6b7280;--accent:#2563eb}");
        html.AppendLine("body{font-family:Inter, system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial; background:var(--bg); color:#0f172a; margin:0; padding:24px}");
        html.AppendLine(".container{max-width:1000px;margin:24px auto;padding:24px;background:var(--card);box-shadow:0 6px 18px rgba(15,23,42,0.08);border-radius:12px}");
        html.AppendLine("h1{margin:0 0 6px;font-size:20px}");
        html.AppendLine(".meta{color:var(--muted);margin-bottom:18px}");
        html.AppendLine(".badges{display:flex;gap:8px;margin:12px 0}");
        html.AppendLine(".badge{background:#eef2ff;color:var(--accent);padding:6px 10px;border-radius:999px;font-weight:600;font-size:13px}");
        html.AppendLine(".section{margin-top:20px}");
        html.AppendLine(".shots{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}");
        html.AppendLine(".shot img{width:100%;height:120px;object-fit:cover;border-radius:8px;border:1px solid #e6eef8}");
        html.AppendLine(".video-wrap{display:flex;justify-content:center;margin-top:12px}");
        html.AppendLine("video{width:100%;max-width:880px;border-radius:8px;border:1px solid #e6eef8}");
        html.AppendLine(".actions{display:flex;gap:12px;margin-top:14px}");
        html.AppendLine(".btn{background:var(--accent);color:#fff;padding:10px 14px;border-radius:8px;text-decoration:none;font-weight:600}");
        html.AppendLine(".btn.secondary{background:#f3f4f6;color:#111}");
        html.AppendLine(".muted{color:var(--muted)}");
        html.AppendLine("footer{margin-top:20px;color:var(--muted);font-size:13px;text-align:center}");
        html.AppendLine("@media (max-width:520px){.shot img{height:100px}}\n</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<div class=\"container\">\n");
        html.AppendLine($"<h1>Test Report</h1>");
        html.AppendLine($"<div class=\"meta\">Job ID: <strong>{jobId}</strong></div>");

        // Summary badges
        html.AppendLine("<div class=\"badges\">");
        html.AppendLine($"<div class=\"badge\">📸 Screenshots: {screenshots.Count}</div>");
        html.AppendLine($"<div class=\"badge\">🎬 Video: {(hasVideo ? "Available" : "None")}</div>");
        html.AppendLine($"<div class=\"badge\">🔍 Trace: {(traceUrl != null ? "Available" : "None")}</div>");
        html.AppendLine("</div>");

        // Screenshots section
        html.AppendLine("<div class=\"section\">\n<h2>Screenshots</h2>\n");
        if (screenshots.Any())
        {
            html.AppendLine("<div class=\"shots\">\n");
            foreach (var s in screenshots)
            {
                html.AppendLine($"<div class=\"shot\"><a href=\"{s}\" target=\"_blank\"><img src=\"{s}\" alt=\"screenshot\"></a></div>");
            }
            html.AppendLine("</div>");
        }
        else
        {
            html.AppendLine("<p class=\"muted\">No screenshots captured for this job.</p>");
        }
        html.AppendLine("</div>");

        // Video section
        html.AppendLine("<div class=\"section\">\n<h2>Video Recording</h2>\n");
        if (hasVideo)
        {
            html.AppendLine($"<div class=\"video-wrap\"><video controls><source src=\"{videoUrl}\" type=\"video/webm\">Your browser does not support the video tag.</video></div>");
            html.AppendLine("<div class=\"actions\"><a class=\"btn\" href=\"{0}\">Download Video</a></div>".Replace("{0}", Url.Action("GetVideo", "Artifacts", new { fileName = Path.GetFileName(videoPath) }) ?? "#"));
        }
        else
        {
            html.AppendLine("<p class=\"muted\">No video available for this job.</p>");
        }
        html.AppendLine("</div>");

        // Trace section
        html.AppendLine("<div class=\"section\">\n<h2>Playwright Trace</h2>\n");
        if (traceUrl != null)
        {
            html.AppendLine($"<a class=\"btn secondary\" href=\"{traceUrl}\">Download Trace (.zip)</a>");
        }
        else
        {
            html.AppendLine("<p class=\"muted\">No trace file available for this job.</p>");
        }
        html.AppendLine("</div>");

        html.AppendLine("<footer>Artifacts are served from the test runner. Use the links above to download or view them.</footer>");
        html.AppendLine("</div>");
        html.AppendLine("</body></html>");

        return Content(html.ToString(), "text/html");
    }
}
