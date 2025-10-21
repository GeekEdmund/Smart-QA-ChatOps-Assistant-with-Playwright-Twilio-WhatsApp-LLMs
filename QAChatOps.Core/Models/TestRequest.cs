namespace QAChatOps.Core.Models;

public record TestRequest
{
    public string Url { get; init; } = string.Empty;
    public string TestIntent { get; init; } = string.Empty;
    public TestType Type { get; init; }
    // Parameters expected to be simple key/value pairs
    public Dictionary<string, string> Parameters { get; init; } = new();
}

public enum TestType
{
    Login,
    Search,
    Navigation,
    FormSubmission,
    AddToCart,
    Checkout,
    General,
    ElementInteraction
}

public record TestPlan
{
    public List<TestStep> Steps { get; init; } = new();
    public Dictionary<string, string> Selectors { get; init; } = new();
    public string Description { get; init; } = string.Empty;
}

public record TestStep
{
    public string Action { get; init; } = string.Empty; // navigate, click, type, verify, etc.
    public string Target { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsOptional { get; init; }
}

public record TestExecutionResult
{
    public string JobId { get; init; } = Guid.NewGuid().ToString();
    public string Url { get; init; } = string.Empty;
    public string TestIntent { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public List<ExecutedStep> ExecutedSteps { get; init; } = new();
    public List<string> ScreenshotPaths { get; init; } = new();
    public string? TracePath { get; init; }
    public string? VideoPath { get; init; }
    public string? ErrorMessage { get; init; }
    public string AIAnalysis { get; init; } = string.Empty;
}

public record ExecutedStep
{
    public string Action { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; }
    public string? ScreenshotPath { get; init; }
}