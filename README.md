# Smart QA ChatOps Assistant

A sample .NET 9 Web API that integrates Playwright, Twilio (WhatsApp), and OpenAI to: accept a natural-language QA request over WhatsApp, generate a Playwright-based test plan using an LLM, execute the plan, collect artifacts (screenshots, video, Playwright trace), analyze results with the LLM, and send a final report back to WhatsApp along with artifact links.

This repository contains three projects:
- `QAChatOps.Api` — ASP.NET Core Web API and webhook endpoints
- `QAChatOps.Core` — domain models and the Playwright test orchestrator
- `QAChatOps.Infrastructure` — integrations for Twilio (WhatsApp) and OpenAI

Why this project
- Demonstrates automation-driven QA workflows wired to conversational interfaces
- Shows integration patterns for Playwright, Twilio, and LLMs
- Serves artifacts from the API so results are accessible via ngrok or localhost

---

## Quick features
- Accepts WhatsApp webhook requests and acknowledges immediately
- Uses OpenAI to parse intent and generate a test plan (steps)
- Runs Playwright to perform the test plan and captures screenshots, video and trace
- Sends a textual analysis (from the LLM) to WhatsApp and a follow-up with screenshots
- Serves a neat report page at `/api/artifacts/report/{jobId}` that includes screenshots, embedded video, and trace download

---

## Prerequisites
- .NET 9 SDK installed: https://dotnet.microsoft.com/en-us/download
- Node.js (optional for Playwright CLI tools) — not strictly required on Windows if you use the .NET Playwright CLI
- Git and GitHub account (to push to your repo)
- A Twilio account with WhatsApp enabled (or Twilio Sandbox for WhatsApp)
- An OpenAI API key (or whichever LLM endpoint you configure)
- ngrok (optional) if you want to expose `localhost` to the internet for Twilio webhooks

---

## Configuration

The app reads configuration from `appsettings.json` / `appsettings.Development.json` and environment variables. Do NOT commit secrets.

Key configuration options (example placeholders):
```json
{
  "ArtifactsPath": "wwwroot/artifacts",
  "PublicBaseUrl": "https://YOUR_NGROK_SUBDOMAIN.ngrok.io/api/webhook/whatsapp",

  "Twilio": {
    "AccountSid": "<YOUR_TWILIO_ACCOUNT_SID>",
    "AuthToken": "<YOUR_TWILIO_AUTH_TOKEN>",
    "WhatsAppNumber": "+14155238886" // Twilio WhatsApp-enabled number (sandbox or production)
  },

  "OpenAI": {
    "ApiKey": "<YOUR_OPENAI_API_KEY>",
    "Model": "gpt-4o-mini" // or another model you prefer
  }
}
```

Security note: Prefer using environment variables, user-secrets or an `appsettings.Development.json` (ignored) for the above. For example, in PowerShell:
```powershell
$Env:OpenAI__ApiKey = "sk-..."
$Env:Twilio__AccountSid = "AC..."
$Env:Twilio__AuthToken = "..."
$Env:Twilio__WhatsAppNumber = "+1415..."
```

### Twilio WhatsApp setup
- If using Twilio Sandbox for WhatsApp, follow Twilio docs to enable the sandbox and add the sandbox number as `Twilio:WhatsAppNumber` in config (format: `+14155238886` or the value Twilio provides). When sending messages, the app prefixes numbers as `whatsapp:+{phone}`.
- If you get Twilio errors like `Invalid From and To pair`, confirm:
  - The `From` is the Twilio WhatsApp-enabled number (sandbox or business-approved), and
  - The `To` number uses the E.164 format and is allowed in the Twilio sandbox (if sandbox is used).

### OpenAI configuration
- Provide `OpenAI:ApiKey` via environment variable or development secrets. Do not commit this key.
- Choose an LLM model configured in `OpenAI:Model`.

---

## Install Playwright browsers
Playwright requires downloading browser binaries. Run the CLI once before running tests.

Option A — using Playwright CLI (recommended):
```powershell
# Install the Playwright CLI if not already present
dotnet tool install --global Microsoft.Playwright.CLI
# Install browsers
playwright install
```

Option B — run the helper that may be provided by the project (if available):
```powershell
# After a build, some MSBuild targets expose the playwright tool, try:
playwright install
```

If installation succeeds you'll see the browser list download progress.

---

## Build & run (Windows PowerShell)
1. Restore and build the solution
```powershell
cd C:\Users\jacob\Desktop\QAChatOpsAssistant
dotnet restore
dotnet build
```

2. Start the API on port 5000 (recommended if you plan to use ngrok)
```powershell
cd QAChatOps.Api
dotnet run --no-launch-profile --urls "http://localhost:5000"
```

3. (Optional) Expose local port to the internet with ngrok so Twilio webhooks can reach your machine:
```powershell
ngrok http 5000
# Copy the forwarded https://... URL and set it as PublicBaseUrl (see configuration above)
```

4. Configure Twilio webhook to point to:
```
https://<your-ngrok-subdomain>/api/webhook/whatsapp
```

---

## Webhook & artifact endpoints
- WhatsApp webhook endpoint (POST): `/api/webhook/whatsapp` — configured in Twilio
- Report page (rendered HTML): `/api/artifacts/report/{jobId}`
- Video stream/download: `/api/artifacts/video/{fileName}`
- Trace download: `/api/artifacts/trace/{fileName}`

Example: open `http://localhost:5000/api/artifacts/report/928179f7` to view screenshots/video for job `928179f7`.

---

## Recommended workflow for testing end-to-end
1. Ensure Playwright browsers are installed.
2. Start API locally: `dotnet run --no-launch-profile --urls "http://localhost:5000"`.
3. Run ngrok and update `PublicBaseUrl` to the forwarded ngrok URL (or set Twilio webhook directly to localhost with a tunnelling utility).
4. Send a test message to your Twilio WhatsApp sandbox number (following Twilio sandbox instructions) with a natural language QA request (e.g., "Test the login page of https://example.com using username bob and password x123").
5. Watch the API logs for each step: Parse -> Plan -> Execute -> Analyze -> Send
6. Open the report page to view screenshots, video, and trace when the job completes.

---

## Troubleshooting
- Missing Playwright methods / runtime errors:
  - Ensure Microsoft.Playwright package versions are consistent across projects. Run `dotnet restore` after any package change.
  - Make sure browsers are installed with `playwright install`.
- Twilio errors when sending messages:
  - Confirm your Twilio WhatsApp sender is enabled and you are using the correct `From` (the Twilio number) and `To` in the `whatsapp:+{number}` format.
- Artifacts not accessible:
  - Confirm `ArtifactsPath` (default `wwwroot/artifacts`) exists and is writable by the app. The project will attempt to create these directories on startup, but permissions can block it.

---

## Development tips
- Keep secrets out of the repo. Add local overrides in `appsettings.Development.json` (committed only to your machine) or export environment variables.
- Add persistent storage for `TestExecutionResult` if you want historical job pages.
- For production, replace the quick-fire background `Task.Run` pattern with a durable queue (e.g., Azure Queue or background worker) to reliably process long-running tests and retry on transient failures.

---

## Contributing
Feel free to open issues or PRs. Suggested improvements:
- Persist job results in a small database and render richer reports
- Add retry/backoff for Twilio media uploads
- Add authentication and an admin UI for viewing past runs

---
