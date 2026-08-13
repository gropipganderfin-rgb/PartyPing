using System.Net.Http.Headers;
using System.Text;

namespace PartyPing;

internal sealed class SmsSender : IDisposable
{
    private readonly HttpClient http = new();

    public async Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        Validate(config);

        var endpoint = $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(config.TwilioAccountSid.Trim())}/Messages.json";
        var authBytes = Encoding.ASCII.GetBytes($"{config.TwilioAccountSid.Trim()}:{config.TwilioAuthToken.Trim()}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = config.ToNumber.Trim(),
            ["From"] = config.TwilioFromNumber.Trim(),
            ["Body"] = body,
        });

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Twilio returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");

        return "SMS queued by Twilio";
    }

    private static void Validate(Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.TwilioAccountSid))
            throw new InvalidOperationException("Twilio Account SID is missing.");
        if (string.IsNullOrWhiteSpace(config.TwilioAuthToken))
            throw new InvalidOperationException("Twilio Auth Token is missing.");
        if (string.IsNullOrWhiteSpace(config.TwilioFromNumber))
            throw new InvalidOperationException("Twilio From number is missing.");
        if (string.IsNullOrWhiteSpace(config.ToNumber))
            throw new InvalidOperationException("Destination phone number is missing.");
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    public void Dispose() => http.Dispose();
}
