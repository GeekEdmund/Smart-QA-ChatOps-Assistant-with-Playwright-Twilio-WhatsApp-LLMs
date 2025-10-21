using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QAChatOps.Core.Services;
using System.Linq;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace QAChatOps.Infrastructure.Twilio;

public interface IWhatsAppService
{
    Task SendMessageAsync(string to, string message);
    Task SendMessageWithImagesAsync(string to, string message, List<string> imagePaths);
}

public class WhatsAppService : IWhatsAppService
{
    private readonly ILogger<WhatsAppService> _logger;
    private readonly string _twilioNumber;
    private readonly string _publicBaseUrl;

    public WhatsAppService(
        IConfiguration configuration,
        ILogger<WhatsAppService> logger)
    {
        _logger = logger;
        
        var accountSid = configuration["Twilio:AccountSid"];
        var authToken = configuration["Twilio:AuthToken"];
        _twilioNumber = configuration["Twilio:WhatsAppNumber"] 
            ?? throw new InvalidOperationException("Twilio WhatsApp number not configured");
        _publicBaseUrl = configuration["PublicBaseUrl"] ?? "https://your-domain.com";

        TwilioClient.Init(accountSid, authToken);
    }

    public async Task SendMessageAsync(string to, string message)
    {
        try
        {
            string Normalize(string num)
            {
                if (string.IsNullOrEmpty(num)) return num;
                if (num.StartsWith("whatsapp:")) return num;
                var n = num.StartsWith("+") ? num : "+" + num.TrimStart('0');
                return $"whatsapp:{n}";
            }

            var fromNum = Normalize(_twilioNumber);
            var toNum = Normalize(to);

            var messageResource = await MessageResource.CreateAsync(
                body: message,
                from: new PhoneNumber(fromNum),
                to: new PhoneNumber(toNum)
            );

            _logger.LogInformation("Sent WhatsApp message to {To}. SID: {Sid}", 
                to, messageResource.Sid);
        }
    catch (ApiException apiEx)
        {
            // Common case: incorrect channel pairing (SMS vs WhatsApp). Log and continue so
            // the controller can proceed without crashing the whole request processing.
            _logger.LogError(apiEx, "Failed to send WhatsApp message to {To}: {Message}", to, apiEx.Message);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {To}", to);
            // For unexpected errors, rethrow so callers can handle as needed
            throw;
        }
    }

    public async Task SendMessageWithImagesAsync(
        string to, 
        string message, 
        List<string> imagePaths)
    {
        try
        {
            // Convert local paths to public URLs
            var mediaUrls = imagePaths
                .Select(path => {
                    var relativePath = path.Replace("wwwroot/", "").Replace("\\", "/");
                    return $"{_publicBaseUrl}/{relativePath}";
                })
                .Take(3) // WhatsApp media limit
                .Select(url => new Uri(url))
                .ToList();

            string Normalize(string num)
            {
                if (string.IsNullOrEmpty(num)) return num;
                if (num.StartsWith("whatsapp:")) return num;
                var n = num.StartsWith("+") ? num : "+" + num.TrimStart('0');
                return $"whatsapp:{n}";
            }

            var fromNum = Normalize(_twilioNumber);
            var toNum = Normalize(to);

            var messageResource = await MessageResource.CreateAsync(
                body: message,
                from: new PhoneNumber(fromNum),
                to: new PhoneNumber(toNum),
                mediaUrl: mediaUrls
            );

            _logger.LogInformation(
                "Sent WhatsApp message with {Count} images to {To}. SID: {Sid}",
                mediaUrls.Count, to, messageResource.Sid);
        }
    catch (ApiException apiEx)
        {
            _logger.LogError(apiEx, "Failed to send WhatsApp message with images to {To}: {Message}", to, apiEx.Message);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message with images to {To}", to);
            throw;
        }
    }
}