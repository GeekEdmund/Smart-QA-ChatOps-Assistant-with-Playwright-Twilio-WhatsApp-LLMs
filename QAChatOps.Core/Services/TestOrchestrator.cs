using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using QAChatOps.Core.Models;

namespace QAChatOps.Core.Services;

public interface ITestOrchestrator
{
    Task<TestExecutionResult> ExecuteTestAsync(
        TestRequest request,
        TestPlan plan,
        CancellationToken cancellationToken = default);
}

public class TestOrchestrator : ITestOrchestrator
{
    private readonly ILogger<TestOrchestrator> _logger;
    private readonly string _artifactsPath;

    public TestOrchestrator(
        ILogger<TestOrchestrator> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _artifactsPath = configuration["ArtifactsPath"] ?? "wwwroot/artifacts";
        
        // Ensure directories exist
        Directory.CreateDirectory(Path.Combine(_artifactsPath, "screenshots"));
        Directory.CreateDirectory(Path.Combine(_artifactsPath, "traces"));
        Directory.CreateDirectory(Path.Combine(_artifactsPath, "videos"));
    }

    public async Task<TestExecutionResult> ExecuteTestAsync(
        TestRequest request,
        TestPlan plan,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var startTime = DateTime.UtcNow;
        var executedSteps = new List<ExecutedStep>();
        var screenshots = new List<string>();
        string? tracePath = null;
        string? videoPath = null;
        bool success = false;
        string? errorMessage = null;

        _logger.LogInformation("Starting test execution for: {Intent} on {Url}", 
            request.TestIntent, request.Url);

        // Initialize Playwright
        using var playwright = await Playwright.CreateAsync();
        
        // Launch with stealth settings to avoid bot detection
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            SlowMo = 100,
            Args = new[]
            {
                "--disable-blink-features=AutomationControlled",
                "--disable-dev-shm-usage",
                "--disable-web-security",
                "--no-sandbox"
            }
        });

        var videoDir = Path.Combine(_artifactsPath, "videos", jobId);
        Directory.CreateDirectory(videoDir);

        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1920, Height = 1080 },
            RecordVideoDir = videoDir,
            RecordVideoSize = new() { Width = 1920, Height = 1080 },
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            Locale = "en-US",
            TimezoneId = "America/New_York",
            Permissions = new[] { "geolocation" },
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Accept-Encoding"] = "gzip, deflate, br",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8"
            }
        });

        // Start tracing
        tracePath = Path.Combine(_artifactsPath, "traces", $"{jobId}.zip");
        await context.Tracing.StartAsync(new()
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        var page = await context.NewPageAsync();

        // Anti-bot detection script
        await page.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => false });
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            window.chrome = { runtime: {} };
        ");

        try
        {
            // Execute each step in the plan
            foreach (var step in plan.Steps)
            {
                var stepResult = await ExecuteStepAsync(
                    page, 
                    step, 
                    request, 
                    jobId, 
                    cancellationToken);
                
                executedSteps.Add(stepResult);

                if (stepResult.ScreenshotPath != null)
                {
                    screenshots.Add(stepResult.ScreenshotPath);
                }

                if (!stepResult.Success && !step.IsOptional)
                {
                    _logger.LogWarning("Critical step failed: {Step}", step.Description);
                    break;
                }
            }

            // Final screenshot
            var finalScreenshot = await CaptureScreenshotAsync(page, jobId, "final");
            screenshots.Add(finalScreenshot);

            success = executedSteps.All(s => s.Success || plan.Steps
                .First(ps => ps.Description == s.Description).IsOptional);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test execution failed");
            success = false;
            errorMessage = ex.Message;

            // Capture failure screenshot
            try
            {
                var errorScreenshot = await CaptureScreenshotAsync(page, jobId, "error");
                screenshots.Add(errorScreenshot);
            }
            catch { }
        }
        finally
        {
            try
            {
                // Close page to finalize video
                await page.CloseAsync();
                
                // Stop tracing
                await context.Tracing.StopAsync(new() { Path = tracePath });
                
                // Close context to finalize video
                // Note: avoid calling context.CloseAsync() directly to remain compatible with different Playwright versions;
                // 'await using' will dispose the context when it goes out of scope.
                
                // Small delay to ensure video file is written
                await Task.Delay(500);
                
                // Get video path
                if (Directory.Exists(videoDir))
                {
                    var videoFiles = Directory.GetFiles(videoDir, "*.webm");
                    if (videoFiles.Any())
                    {
                        // Move video to root videos directory with jobId in name
                        var newVideoPath = Path.Combine(_artifactsPath, "videos", $"{jobId}.webm");
                        File.Move(videoFiles.First(), newVideoPath, true);
                        videoPath = newVideoPath;
                        _logger.LogInformation("Video saved: {VideoPath}", videoPath);
                    }
                    
                    // Clean up temp video directory
                    try { Directory.Delete(videoDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in cleanup/video handling");
            }
        }

        // Return result after video is saved
        return new TestExecutionResult
        {
            JobId = jobId,
            Url = request.Url,
            TestIntent = request.TestIntent,
            Success = success,
            Duration = DateTime.UtcNow - startTime,
            ExecutedSteps = executedSteps,
            ScreenshotPaths = screenshots,
            TracePath = tracePath,
            VideoPath = videoPath,
            ErrorMessage = errorMessage
        };
    }

    private async Task<ExecutedStep> ExecuteStepAsync(
        IPage page,
        TestStep step,
        TestRequest request,
        string jobId,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTime.UtcNow;
        _logger.LogInformation("Executing step: {Action} - {Description}", 
            step.Action, step.Description);

        try
        {
            string? screenshotPath = null;

            switch (step.Action.ToLower())
            {
                case "navigate":
                    var url = string.IsNullOrEmpty(step.Target) ? request.Url : step.Target;
                    
                    // Ensure URL has protocol
                    if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                    {
                        url = "https://" + url;
                    }
                    
                    _logger.LogInformation("Navigating to: {Url}", url);
                    
                    try
                    {
                        // Try with networkidle first
                        await page.GotoAsync(url, new() 
                        { 
                            WaitUntil = WaitUntilState.NetworkIdle,
                            Timeout = 30000 
                        });
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("NetworkIdle timeout, trying with DOMContentLoaded");
                        // Fallback to DOMContentLoaded if networkidle times out
                        await page.GotoAsync(url, new() 
                        { 
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 20000 
                        });
                    }
                    
                    // Wait a bit for dynamic content
                    await page.WaitForTimeoutAsync(2000);
                    screenshotPath = await CaptureScreenshotAsync(page, jobId, "navigate");
                    break;

                case "click":
                    await ClickElementAsync(page, step.Target);
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    await page.WaitForTimeoutAsync(1000);
                    screenshotPath = await CaptureScreenshotAsync(page, jobId, "click");
                    break;

                case "type":
                    await TypeTextAsync(page, step.Target, step.Value ?? "", request);
                    await page.WaitForTimeoutAsync(500);
                    screenshotPath = await CaptureScreenshotAsync(page, jobId, "type");
                    break;

                case "verify":
                    await VerifyElementAsync(page, step.Target);
                    break;

                case "wait":
                    var waitTime = int.TryParse(step.Value, out var ms) ? ms : 2000;
                    await page.WaitForTimeoutAsync(waitTime);
                    break;

                case "screenshot":
                    screenshotPath = await CaptureScreenshotAsync(page, jobId, step.Target);
                    break;

                case "scroll":
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    await page.WaitForTimeoutAsync(500);
                    break;

                default:
                    throw new NotSupportedException($"Action '{step.Action}' is not supported");
            }

            return new ExecutedStep
            {
                Action = step.Action,
                Description = step.Description,
                Success = true,
                Timestamp = timestamp,
                ScreenshotPath = screenshotPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Step failed: {Description}", step.Description);

            string? errorScreenshot = null;
            try
            {
                errorScreenshot = await CaptureScreenshotAsync(page, jobId, "error");
            }
            catch { }

            return new ExecutedStep
            {
                Action = step.Action,
                Description = step.Description,
                Success = false,
                Error = ex.Message,
                Timestamp = timestamp,
                ScreenshotPath = errorScreenshot
            };
        }
    }

    private async Task ClickElementAsync(IPage page, string selector)
    {
        // Try multiple selector strategies
        var selectors = selector.Split(',').Select(s => s.Trim()).ToList();

        foreach (var sel in selectors)
        {
            try
            {
                // Wait for element to be visible and enabled
                await page.WaitForSelectorAsync(sel, new() 
                { 
                    State = WaitForSelectorState.Visible,
                    Timeout = 3000 
                });

                // Try smart text-based clicking first (Playwright handles visibility)
                if (sel.Contains("text=") || sel.Contains("has-text"))
                {
                    await page.ClickAsync(sel, new() { Timeout = 5000 });
                    _logger.LogInformation("Clicked element using selector: {Selector}", sel);
                    return;
                }

                // Try CSS selector
                var element = await page.QuerySelectorAsync(sel);
                if (element != null && await element.IsVisibleAsync())
                {
                    // Scroll into view first
                    await element.ScrollIntoViewIfNeededAsync();
                    await page.WaitForTimeoutAsync(300);
                    await element.ClickAsync(new() { Timeout = 5000 });
                    _logger.LogInformation("Clicked element using selector: {Selector}", sel);
                    return;
                }
            }
            catch
            {
                continue; // Try next selector
            }
        }

        // SMART FALLBACK: Try to find submit buttons or login links
        _logger.LogWarning("Standard selectors failed, attempting smart button discovery");
        
        try
        {
            // Try common button patterns (using proper Playwright selectors)
            var buttonSelectors = new[]
            {
                "button[type='submit']",
                "input[type='submit']",
                "button:has-text('Log in')",
                "button:has-text('Sign in')",
                "button:has-text('Login')",
                "button:has-text('Submit')",
                "a:has-text('Log in')",
                "a:has-text('Sign in')",
                "a:has-text('Login')",
                "[role='button']:has-text('Log')",
                "[role='button']:has-text('Sign')",
                ".login-btn",
                ".signin-btn",
                "#login-button",
                "button"  // Last resort: any button
            };

            foreach (var btnSel in buttonSelectors)
            {
                try
                {
                    var buttons = await page.QuerySelectorAllAsync(btnSel);
                    
                    foreach (var button in buttons)
                    {
                        // Check if button is visible
                        if (await button.IsVisibleAsync())
                        {
                            // Get button text to verify it's a login/submit button
                            var buttonText = await button.InnerTextAsync();
                            var isLoginButton = buttonText.Contains("log", StringComparison.OrdinalIgnoreCase) ||
                                               buttonText.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
                                               buttonText.Contains("submit", StringComparison.OrdinalIgnoreCase) ||
                                               buttonText.Contains("enter", StringComparison.OrdinalIgnoreCase) ||
                                               string.IsNullOrWhiteSpace(buttonText); // Empty buttons might be icon buttons

                            if (isLoginButton || btnSel == "button") // Accept any button as last resort
                            {
                                await button.ScrollIntoViewIfNeededAsync();
                                await page.WaitForTimeoutAsync(300);
                                await button.ClickAsync(new() { Timeout = 5000 });
                                _logger.LogInformation("Successfully clicked using fallback: {Selector} (text: {Text})", btnSel, buttonText);
                                return;
                            }
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            // Final fallback: try clicking any link with "login" or "sign"
            _logger.LogWarning("Button discovery failed, trying link discovery");
            var links = await page.QuerySelectorAllAsync("a");
            foreach (var link in links)
            {
                try
                {
                    if (await link.IsVisibleAsync())
                    {
                        var linkText = await link.InnerTextAsync();
                        var href = await link.GetAttributeAsync("href") ?? "";
                        
                        if (linkText.Contains("log", StringComparison.OrdinalIgnoreCase) ||
                            linkText.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
                            href.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                            href.Contains("signin", StringComparison.OrdinalIgnoreCase))
                        {
                            await link.ScrollIntoViewIfNeededAsync();
                            await page.WaitForTimeoutAsync(300);
                            await link.ClickAsync(new() { Timeout = 5000 });
                            _logger.LogInformation("Successfully clicked link: {Text} (href: {Href})", linkText, href);
                            return;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart button discovery also failed");
        }

        throw new Exception($"Could not find clickable element with any selector: {selector}. Tried {selectors.Count} selectors plus extensive smart discovery.");
    }

    private async Task TypeTextAsync(IPage page, string selector, string value, TestRequest request)
    {
        // Replace placeholders with actual values
        var actualValue = value
            .Replace("{email}", request.Parameters.GetValueOrDefault("email", "test@example.com"))
            .Replace("{password}", request.Parameters.GetValueOrDefault("password", "Password123!"))
            .Replace("{username}", request.Parameters.GetValueOrDefault("username", "testuser"))
            .Replace("{search}", request.Parameters.GetValueOrDefault("search", "laptop"));

        var selectors = selector.Split(',').Select(s => s.Trim()).ToList();

        // Determine field type for better handling
        var isPasswordField = selector.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                             value.Contains("{password}");

        // For password fields, wait longer as they often appear after email entry
        var waitTimeout = isPasswordField ? 5000 : 3000;

        // Try provided selectors first
        foreach (var sel in selectors)
        {
            try
            {
                await page.WaitForSelectorAsync(sel, new() 
                { 
                    State = WaitForSelectorState.Visible,
                    Timeout = waitTimeout 
                });

                var element = await page.QuerySelectorAsync(sel);
                if (element != null && await element.IsVisibleAsync())
                {
                    await element.ScrollIntoViewIfNeededAsync();
                    await element.ClickAsync(); // Focus the input
                    await page.WaitForTimeoutAsync(300);
                    await element.FillAsync(actualValue, new() { Timeout = 5000 });
                    _logger.LogInformation("Typed into element using selector: {Selector}", sel);
                    
                    // After typing, wait a bit for any dynamic behavior
                    await page.WaitForTimeoutAsync(500);
                    return;
                }
            }
            catch
            {
                continue;
            }
        }

        // SMART FALLBACK: Try to find ANY visible input field that might match
        _logger.LogWarning("Standard selectors failed, attempting smart input discovery");
        
        try
        {
            // For password fields, give extra time for dynamic rendering
            if (isPasswordField)
            {
                _logger.LogInformation("Waiting for password field to appear dynamically...");
                await page.WaitForTimeoutAsync(2000);
            }

            string fallbackSelector;
            if (isPasswordField)
            {
                fallbackSelector = "input[type='password']:visible";
            }
            else
            {
                var isEmailField = selector.Contains("email", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("{email}") || value.Contains("@");
                
                if (isEmailField)
                {
                    fallbackSelector = "input[type='email']:visible, input[type='text']:visible:not([type='hidden']):not([type='submit'])";
                }
                else
                {
                    fallbackSelector = "input:visible:not([type='password']):not([type='hidden']):not([type='submit']):not([type='checkbox']):not([type='radio'])";
                }
            }

            // Try multiple times for dynamic fields
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var fallbackElements = await page.QuerySelectorAllAsync(fallbackSelector);
                
                // Filter to only visible elements
                var visibleElements = new List<IElementHandle>();
                foreach (var el in fallbackElements)
                {
                    if (await el.IsVisibleAsync())
                    {
                        visibleElements.Add(el);
                    }
                }
                
                if (visibleElements.Any())
                {
                    // Use the first matching visible input
                    var element = visibleElements.First();
                    await element.ScrollIntoViewIfNeededAsync();
                    await element.ClickAsync();
                    await page.WaitForTimeoutAsync(300);
                    await element.FillAsync(actualValue, new() { Timeout = 5000 });
                    _logger.LogInformation("Successfully typed using fallback discovery (attempt {Attempt})", attempt + 1);
                    
                    // After typing, wait a bit for any dynamic behavior
                    await page.WaitForTimeoutAsync(500);
                    return;
                }
                
                // Wait before next attempt
                if (attempt < 2)
                {
                    _logger.LogInformation("Field not found, waiting before retry (attempt {Attempt})", attempt + 1);
                    await page.WaitForTimeoutAsync(1500);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart input discovery also failed");
        }

        throw new Exception($"Could not find input element with selector: {selector}. Tried {selectors.Count} selectors plus smart discovery with retries.");
    }

    private async Task VerifyElementAsync(IPage page, string selector)
    {
        var selectors = selector.Split(',').Select(s => s.Trim()).ToList();

        foreach (var sel in selectors)
        {
            try
            {
                await page.WaitForSelectorAsync(sel, new() 
                { 
                    State = WaitForSelectorState.Visible,
                    Timeout = 5000 
                });
                return;
            }
            catch
            {
                continue;
            }
        }

        throw new Exception($"Element not found or not visible: {selector}");
    }

    private async Task<string> CaptureScreenshotAsync(IPage page, string jobId, string step)
    {
        var filename = $"{jobId}_{step}_{DateTime.UtcNow:HHmmss}.png";
        var path = Path.Combine(_artifactsPath, "screenshots", filename);
        
        await page.ScreenshotAsync(new() 
        { 
            Path = path,
            FullPage = true 
        });

        return path;
    }
}