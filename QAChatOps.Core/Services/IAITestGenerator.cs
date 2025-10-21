namespace QAChatOps.Core.Services;

using QAChatOps.Core.Models;

public interface IAITestGenerator
{
    // Parse and return a populated TestRequest from raw user message
    Task<TestRequest> ParseIntentAsync(string message, CancellationToken cancellationToken = default);

    // Generate a TestPlan for a given TestRequest
    Task<TestPlan> GenerateTestPlanAsync(TestRequest request, CancellationToken cancellationToken = default);

    // Analyze execution results and return a short analysis suitable for user messages
    Task<string> AnalyzeResultsAsync(TestExecutionResult result, CancellationToken cancellationToken = default);
}