using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QAChatOps.Core.Models;
using QAChatOps.Core.Services;

namespace QAChatOps.Infrastructure.OpenAI;

public class AITestGenerator : IAITestGenerator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AITestGenerator> _logger;
    private readonly string _apiKey;
    private readonly string _modelName;
    private const string OpenAIApiUrl = "https://api.openai.com/v1/chat/completions";

    public AITestGenerator(
        IConfiguration configuration,
        ILogger<AITestGenerator> logger,
        HttpClient httpClient)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["OpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _modelName = configuration["OpenAI:Model"] ?? "gpt-4-turbo-preview";

        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public async Task<TestRequest> ParseIntentAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var prompt = $@"You are a QA test intent parser. Extract testing information from user messages.

User message: ""{message}""

Extract and return ONLY a valid JSON object (no markdown, no backticks) with this structure:
{{
  ""url"": ""the website URL (required, must start with http:// or https://)"",
  ""testIntent"": ""brief description of what to test"",
  ""testType"": ""Login|Search|Checkout|Navigation|Form|General"",
  ""parameters"": {{
    ""email"": ""extracted email or test@example.com"",
    ""password"": ""extracted password or Test123!"",
    ""username"": ""extracted username or testuser"",
    ""search"": ""search term if mentioned""
  }}
}}

CRITICAL RULES:
1. ALWAYS extract the URL from the message - look for http:// or https://
2. If URL is on a separate line, still extract it
3. Return ONLY valid JSON, no other text
4. If no URL found, set url to empty string
5. Infer testType from the intent (login, search, checkout, navigation, form, or general)

Examples:
- ""Test login on https://example.com"" → url: ""https://example.com"", testType: ""Login""
- ""Check search on https://shop.com for laptops"" → url: ""https://shop.com"", testType: ""Search"", search: ""laptops""
- Message with URL on new line should still extract the URL

Now parse the user message above and respond with ONLY the JSON object.";

        try
        {
            var response = await CallOpenAIAsync(prompt, cancellationToken);
            
            _logger.LogInformation("OpenAI ParseIntent Response: {Response}", response);

            // Clean up response - remove markdown code blocks if present
            response = response.Trim()
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var parsed = JsonSerializer.Deserialize<ParsedIntent>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed == null)
            {
                throw new InvalidOperationException("Failed to parse AI response");
            }

            return new TestRequest
            {
                Url = parsed.Url ?? string.Empty,
                TestIntent = parsed.TestIntent ?? "General test",
                Type = Enum.TryParse<TestType>(parsed.TestType, true, out var type) ? type : TestType.General,
                Parameters = parsed.Parameters ?? new Dictionary<string, string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing intent with AI");
            
            // Fallback to basic parsing
            return new TestRequest
            {
                Url = string.Empty,
                TestIntent = message,
                Type = TestType.General,
                Parameters = new Dictionary<string, string>()
            };
        }
    }

    public async Task<TestPlan> GenerateTestPlanAsync(
        TestRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = $@"You are a QA test automation expert. Generate a detailed test plan.

Test Request:
- URL: {request.Url}
- Intent: {request.TestIntent}
- Type: {request.Type}
- Parameters: {JsonSerializer.Serialize(request.Parameters)}

Generate a test plan with specific Playwright actions. Return ONLY valid JSON (no markdown):

{{
  ""description"": ""brief description of test"",
  ""steps"": [
    {{
      ""action"": ""navigate|click|type|verify|wait|scroll"",
      ""target"": ""CSS selector or text locator"",
      ""value"": ""value for type action, or wait duration"",
      ""description"": ""human readable step description"",
      ""isOptional"": false
    }}
  ],
  ""selectors"": {{}}
}}

CRITICAL SELECTOR RULES FOR MAXIMUM FLEXIBILITY:
1. For email/username inputs, use this EXACT selector pattern:
   ""input[type='email'], input[type='text'], input[name*='email' i], input[name*='user' i], input[id*='email' i], input[id*='user' i], input[placeholder*='email' i], input[placeholder*='user' i], #email, #username, .email-input, [data-testid*='email'], [data-testid*='user']""

2. For password inputs, use this EXACT selector pattern:
   ""input[type='password'], input[name*='pass' i], input[id*='pass' i], input[placeholder*='pass' i], #password, .password-input, [data-testid*='pass']""

3. For login/submit buttons, use this EXACT selector pattern:
   ""button:has-text('Log in'), button:has-text('Sign in'), button:has-text('Login'), button:has-text('Submit'), a:has-text('Log in'), a:has-text('Sign in'), button[type='submit'], input[type='submit'], .login-btn, .signin-btn, .submit-btn, #login-button, [data-testid*='login'], [data-testid*='submit'], [role='button']:has-text('Log')""

4. For ANY input field, ALWAYS include these fallback selectors:
   - type attribute
   - name attribute with case-insensitive contains (*=)
   - id attribute with case-insensitive contains
   - placeholder attribute with case-insensitive contains
   - class names
   - data-testid attributes

5. List selectors from MOST SPECIFIC to MOST GENERIC (separated by commas)

6. For verify steps after login, use multiple success indicators:
   ""text=/dashboard|home|welcome/i, .user-menu, .logout-btn, .profile, [data-testid*='user'], nav:has-text('Logout')""

Example for LOGIN test (FOLLOW THIS PATTERN EXACTLY):
{{
  ""description"": ""Test user login flow"",
  ""steps"": [
    {{""action"": ""navigate"", ""target"": """", ""description"": ""Navigate to login page""}},
    {{""action"": ""wait"", ""value"": ""2000"", ""description"": ""Wait for page to load""}},
    {{""action"": ""click"", ""target"": ""button:has-text('Log in'), a:has-text('Log in'), button:has-text('Sign in'), a:has-text('Sign in'), a[href*='login'], a[href*='signin'], .login-link, #login"", ""description"": ""Click login link if not already on login page"", ""isOptional"": true}},
    {{""action"": ""wait"", ""value"": ""1500"", ""description"": ""Wait for login form to appear""}},
    {{""action"": ""type"", ""target"": ""input[type='email'], input[type='text'], input[name*='email' i], input[name*='user' i], input[id*='email' i], input[id*='user' i], input[placeholder*='email' i], #email, #username"", ""value"": ""{{email}}"", ""description"": ""Enter email or username""}},
    {{""action"": ""wait"", ""value"": ""1500"", ""description"": ""Wait for password field to appear (many sites show it dynamically)""}},
    {{""action"": ""type"", ""target"": ""input[type='password'], input[name*='pass' i], input[id*='pass' i], input[placeholder*='pass' i], #password"", ""value"": ""{{password}}"", ""description"": ""Enter password""}},
    {{""action"": ""wait"", ""value"": ""500"", ""description"": ""Brief wait before clicking submit""}},
    {{""action"": ""click"", ""target"": ""button:has-text('Log in'), button:has-text('Sign in'), button:has-text('Login'), button[type='submit'], input[type='submit'], .login-btn"", ""description"": ""Click login button""}},
    {{""action"": ""wait"", ""value"": ""3000"", ""description"": ""Wait for login to complete""}},
    {{""action"": ""verify"", ""target"": ""text=/dashboard|home|welcome/i, .user-menu, .logout-btn"", ""description"": ""Verify successful login"", ""isOptional"": true}}
  ]
}}

IMPORTANT: Use the EXACT selector patterns shown above for email, password, and button fields. These patterns cover 95% of login forms.

Generate the test plan now for the request above. Return ONLY the JSON object.";

        try
        {
            var response = await CallOpenAIAsync(prompt, cancellationToken, maxTokens: 2000);
            
            _logger.LogInformation("OpenAI GenerateTestPlan Response: {Response}", response);

            response = response.Trim()
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var parsed = JsonSerializer.Deserialize<TestPlan>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed == null || !parsed.Steps.Any())
            {
                throw new InvalidOperationException("Generated test plan is empty");
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating test plan with AI");
            
            // Fallback to basic plan
            return new TestPlan
            {
                Description = $"Basic test for: {request.TestIntent}",
                Steps = new List<TestStep>
                {
                    new TestStep 
                    { 
                        Action = "navigate", 
                        Target = request.Url, 
                        Description = "Navigate to URL",
                        Value = string.Empty,
                        IsOptional = false
                    },
                    new TestStep 
                    { 
                        Action = "screenshot", 
                        Target = "final", 
                        Description = "Capture screenshot",
                        Value = string.Empty,
                        IsOptional = true
                    }
                },
                Selectors = new Dictionary<string, string>()
            };
        }
    }

    public async Task<string> AnalyzeResultsAsync(
        TestExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        var stepsInfo = string.Join("\n", result.ExecutedSteps.Select(s => 
            $"- [{(s.Success ? "✓" : "✗")}] {s.Description}" + 
            (s.Success ? "" : $" (Error: {s.Error})")));

        var prompt = $@"You are a QA analyst. Analyze this test execution result and provide a brief, actionable summary.

Test Execution:
- URL: {result.Url}
- Intent: {result.TestIntent}
- Overall Success: {result.Success}
- Duration: {result.Duration.TotalSeconds:F1}s
- Steps Executed: {result.ExecutedSteps.Count}

Step Results:
{stepsInfo}

{(string.IsNullOrEmpty(result.ErrorMessage) ? "" : $"Error Message: {result.ErrorMessage}")}

Provide a brief analysis (2-3 sentences max) that:
1. Summarizes what worked and what failed
2. Identifies the root cause if test failed
3. Suggests next steps if applicable

Keep it concise and user-friendly. No markdown formatting.";

        try
        {
            var response = await CallOpenAIAsync(prompt, cancellationToken, maxTokens: 300);
            return response.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing results with AI");
            
            if (result.Success)
            {
                return "All test steps executed successfully! The website functionality works as expected.";
            }
            else
            {
                var failedStep = result.ExecutedSteps.FirstOrDefault(s => !s.Success);
                return failedStep != null 
                    ? $"Test failed at: {failedStep.Description}. Error: {failedStep.Error}"
                    : $"Test failed. {result.ErrorMessage}";
            }
        }
    }

    private async Task<string> CallOpenAIAsync(
        string prompt, 
        CancellationToken cancellationToken,
        int maxTokens = 1000)
    {
        var request = new
        {
            model = _modelName,
            messages = new[]
            {
                new 
                { 
                    role = "system", 
                    content = "You are a helpful QA automation assistant. Always respond with valid JSON when requested." 
                },
                new 
                { 
                    role = "user", 
                    content = prompt 
                }
            },
            max_tokens = maxTokens,
            temperature = 0.3 // Lower temperature for more consistent JSON output
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(OpenAIApiUrl, content, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI API error: {StatusCode} - {Error}", 
                response.StatusCode, error);
            throw new HttpRequestException($"OpenAI API failed: {response.StatusCode}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var openAIResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseJson);

        if (openAIResponse?.Choices == null || !openAIResponse.Choices.Any())
        {
            throw new InvalidOperationException("Empty response from OpenAI API");
        }

        return openAIResponse.Choices[0].Message.Content;
    }

    // DTOs for JSON serialization
    private class ParsedIntent
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        
        [JsonPropertyName("testIntent")]
        public string? TestIntent { get; set; }
        
        [JsonPropertyName("testType")]
        public string? TestType { get; set; }
        
        [JsonPropertyName("parameters")]
        public Dictionary<string, string>? Parameters { get; set; }
    }

    private class OpenAIResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public Message Message { get; set; } = new();
    }

    private class Message
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}