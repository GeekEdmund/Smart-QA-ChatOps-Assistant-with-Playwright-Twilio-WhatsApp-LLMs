using Microsoft.AspNetCore.Mvc;
using QAChatOps.Core.Services;
using QAChatOps.Core.Models;
using QAChatOps.Infrastructure.Twilio;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace QAChatOps.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IAITestGenerator _aiGenerator;
    private readonly ITestOrchestrator _orchestrator;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<WebhookController> _logger;
    private static readonly Regex UrlRegex = new Regex(
        @"https?://[^\s\)\]\}\,]+", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _baseUrl;

    public WebhookController(
        IAITestGenerator aiGenerator,
        ITestOrchestrator orchestrator,
        IWhatsAppService whatsApp,
        ILogger<WebhookController> logger,
        IConfiguration configuration)  // ADD THIS
    {
        _aiGenerator = aiGenerator;
        _orchestrator = orchestrator;
        _whatsApp = whatsApp;
        _logger = logger;
        _baseUrl = configuration["BaseUrl"] ?? "http://localhost:5000";  // ADD THIS
    }

    [HttpPost("whatsapp")]
    public async Task<IActionResult> ReceiveWhatsAppMessage(
        [FromForm] WhatsAppWebhookRequest request)
    {
        _logger.LogInformation("Received WhatsApp message from {From}: {Body}",
            request.From, request.Body);

        // Process asynchronously
        _ = Task.Run(async () => await ProcessTestRequestAsync(request));

        return Ok();
    }

    private async Task ProcessTestRequestAsync(WhatsAppWebhookRequest request)
    {
        var message = request.Body?.Trim() ?? string.Empty;
        var from = request.From;

        try
        {
            // Handle help command
            if (message.ToLower() == "help" || message.ToLower() == "start")
            {
                await SendHelpMessageAsync(from);
                return;
            }

            // Send acknowledgment
            await _whatsApp.SendMessageAsync(
                from,
                "🤖 Got it! Analyzing your request and generating test plan...\n\n⏳ This usually takes 30-60 seconds.");

            // Step 1: Parse intent using AI
            var testRequest = await _aiGenerator.ParseIntentAsync(message);
            
            // CRITICAL FIX: Fallback URL extraction if AI failed
            if (string.IsNullOrEmpty(testRequest.Url))
            {
                var extractedUrl = ExtractUrlFromMessage(message);
                if (!string.IsNullOrEmpty(extractedUrl))
                {
                    testRequest = testRequest with { Url = extractedUrl };
                    _logger.LogInformation("URL extracted via regex fallback: {Url}", extractedUrl);
                }
            }
            
            _logger.LogInformation("Parsed intent: {Intent} for {Url}", 
                testRequest.TestIntent, testRequest.Url);

            // Validate URL after fallback attempt
            if (string.IsNullOrEmpty(testRequest.Url))
            {
                await _whatsApp.SendMessageAsync(
                    from,
                    "❌ I couldn't find a website URL in your message.\n\n" +
                    "Please include the URL. Example:\n" +
                    "Test login on https://example.com");
                return;
            }

            // Validate URL format
            if (!Uri.TryCreate(testRequest.Url, UriKind.Absolute, out var uri) || 
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                await _whatsApp.SendMessageAsync(
                    from,
                    $"❌ Invalid URL format: {testRequest.Url}\n\n" +
                    "Please provide a valid URL starting with http:// or https://");
                return;
            }

            // Step 2: Generate test plan using AI
            var testPlan = await _aiGenerator.GenerateTestPlanAsync(testRequest);
            
            await _whatsApp.SendMessageAsync(
                from,
                $"✅ Test plan created!\n\n" +
                $"📋 Steps: {testPlan.Steps.Count}\n" +
                $"🎯 Goal: {testPlan.Description}\n\n" +
                $"🚀 Executing tests now...");

            // Step 3: Execute test with Playwright
            var result = await _orchestrator.ExecuteTestAsync(testRequest, testPlan);

            // Step 4: AI analysis of results
            result = result with 
            { 
                AIAnalysis = await _aiGenerator.AnalyzeResultsAsync(result) 
            };

            // Step 5: Send results back to WhatsApp
            await SendTestResultsAsync(from, result);

            _logger.LogInformation("Test completed for {Intent}. Success: {Success}",
                testRequest.TestIntent, result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing test request");
            
            await _whatsApp.SendMessageAsync(
                from,
                $"❌ Oops! Something went wrong:\n\n{ex.Message}\n\n" +
                "Send 'help' to see examples.");
        }
    }

    private string? ExtractUrlFromMessage(string message)
    {
        var match = UrlRegex.Match(message);
        if (match.Success)
        {
            // Clean up any trailing punctuation
            var url = match.Value.TrimEnd('.', ',', ')', ']', '}', '!', '?');
            return url;
        }
        return null;
    }

    private async Task SendTestResultsAsync(string to, TestExecutionResult result)
    {
        var icon = result.Success ? "✅" : "❌";
        var status = result.Success ? "*PASSED*" : "*FAILED*";

        // Build the base URL for artifacts (you should configure this in appsettings.json)
        var baseUrl = $"{_baseUrl}/api/artifacts"; // TODO: Make this configurable

        var report = $"{icon} *Test Results* {icon}\n";
        report += "━━━━━━━━━━━━━━━━━━━━\n\n";
        report += $"🌐 *URL:* {result.Url}\n";
        report += $"🎯 *Intent:* {result.TestIntent}\n";
        report += $"📊 *Status:* {status}\n";
        report += $"⏱️ *Duration:* {result.Duration.TotalSeconds:F1}s\n";
        report += $"📋 *Steps:* {result.ExecutedSteps.Count(s => s.Success)}/{result.ExecutedSteps.Count} succeeded\n\n";

        // AI Analysis Section
        if (!string.IsNullOrEmpty(result.AIAnalysis))
        {
            report += "🤖 *AI Analysis:*\n";
            report += $"{result.AIAnalysis}\n\n";
        }

        // Steps Executed
        if (result.ExecutedSteps.Any())
        {
            report += "*📝 Steps Executed:*\n";
            foreach (var step in result.ExecutedSteps.Take(8))
            {
                var stepIcon = step.Success ? "✓" : "✗";
                report += $"{stepIcon} {step.Description}\n";
                if (!step.Success && !string.IsNullOrEmpty(step.Error))
                {
                    report += $"   ↳ _Error: {step.Error}_\n";
                }
            }
            if (result.ExecutedSteps.Count > 8)
            {
                report += $"... and {result.ExecutedSteps.Count - 8} more\n";
            }
            report += "\n";
        }

        // Media Links Section
        report += "━━━━━━━━━━━━━━━━━━━━\n";
        report += "*📥 Download Test Artifacts:*\n\n";

        // Screenshots
        if (result.ScreenshotPaths.Any())
        {
            report += $"📸 *Screenshots:* {result.ScreenshotPaths.Count} captured\n";
            report += $"{baseUrl}/report/{result.JobId}\n\n";
        }

        // Video
        if (!string.IsNullOrEmpty(result.TracePath))
        {
            report += $"🎬 *Video Recording:* Available\n";
            report += $"{baseUrl}/video/{result.JobId}.webm\n\n";
        }

        // Trace
        if (!string.IsNullOrEmpty(result.TracePath))
        {
            report += $"🔍 *Playwright Trace:* Available\n";
            report += $"{baseUrl}/trace/{Path.GetFileName(result.TracePath)}\n";
            report += "   _(Open at trace.playwright.dev)_\n\n";
        }

        report += "━━━━━━━━━━━━━━━━━━━━\n";
        report += "💡 Try another test or send 'help'";

        // Send report: always send the textual analysis first so the user receives the AI summary
        // even if media URLs are not accessible. Then attempt to send screenshots as a follow-up.
        try
        {
            await _whatsApp.SendMessageAsync(to, report);
            _logger.LogInformation("Sent test results (text) to {To}", to);

            if (result.ScreenshotPaths.Any())
            {
                // Send with up to 3 screenshots as a separate message
                var screenshotsToSend = result.ScreenshotPaths.Take(3).ToList();
                try
                {
                    await _whatsApp.SendMessageWithImagesAsync(to, "📸 Test screenshots", screenshotsToSend);
                    _logger.LogInformation("Sent test results with {Count} screenshots to {To}", screenshotsToSend.Count, to);
                }
                catch (Exception imgEx)
                {
                    _logger.LogError(imgEx, "Failed to send screenshots to {To}", to);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send textual test results to {To}", to);
        }
    }

    private async Task SendHelpMessageAsync(string to)
    {
        var help = @"🤖 *Smart QA ChatOps Assistant*

I can test websites for you! Just tell me what to test in natural language.

*Examples:*

1️⃣ *Login Testing*
`Test login on https://example.com with user@test.com`

2️⃣ *Search Testing*
`Check if search works on https://amazon.com for laptops`

3️⃣ *Add to Cart*
`Try adding a product to cart on https://shop.com`

4️⃣ *Navigation*
`Test navigation on https://mysite.com - click about, then contact`

5️⃣ *Form Submission*
`Test contact form on https://example.com with name John Doe`

6️⃣ *Checkout Flow*
`Test the full checkout on https://store.com`

*Features:*
✨ AI-powered test generation
🎯 Dynamic element discovery
📸 Automatic screenshots
🔍 Intelligent error analysis
💬 Plain language commands

Just describe what you want to test!";

        await _whatsApp.SendMessageAsync(to, help);
    }
}

// DTOs
public record WhatsAppWebhookRequest
{
    public string MessageSid { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
}