using QAChatOps.Core.Services;
using QAChatOps.Infrastructure.OpenAI;
using QAChatOps.Infrastructure.Twilio;

var builder = WebApplication.CreateBuilder(args);


// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient<IAITestGenerator, AITestGenerator>();

// Configure Playwright
builder.Services.AddSingleton(sp =>
{
    return Microsoft.Playwright.Playwright.CreateAsync().GetAwaiter().GetResult();
});

// Configure core services
builder.Services.AddHttpClient<QAChatOps.Core.Services.IAITestGenerator, QAChatOps.Infrastructure.OpenAI.AITestGenerator>();
builder.Services.AddSingleton<QAChatOps.Core.Services.ITestOrchestrator, QAChatOps.Core.Services.TestOrchestrator>();
builder.Services.AddSingleton<QAChatOps.Infrastructure.Twilio.IWhatsAppService, QAChatOps.Infrastructure.Twilio.WhatsAppService>();

// Configure logging
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Serve static files (for screenshots)
builder.Services.AddDirectoryBrowser();

// Ensure artifacts directory exists early so DirectoryBrowser's PhysicalFileProvider
// doesn't throw when the app is built.
var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "artifacts");
Directory.CreateDirectory(Path.Combine(artifactsPath, "screenshots"));
Directory.CreateDirectory(Path.Combine(artifactsPath, "traces"));
Directory.CreateDirectory(Path.Combine(artifactsPath, "videos"));

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve static files from wwwroot
app.UseStaticFiles();

// Enable directory browsing for artifacts (dev only)
if (app.Environment.IsDevelopment())
{
    app.UseDirectoryBrowser(new DirectoryBrowserOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "artifacts")),
        RequestPath = "/artifacts"
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.UseStaticFiles(); 
// Serves files from wwwroot
// (Artifacts directory already ensured earlier)

app.Run();