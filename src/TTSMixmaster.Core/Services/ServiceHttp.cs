using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TTSMixmaster.Core.Services;

internal static class ServiceHttp
{
    internal static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request, string service, CancellationToken ct)
    {
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Authentication failed. Check your credentials or sign in again.",
                    HttpStatusCode.Forbidden => "Access was denied. Check permissions, API access and your service quota.",
                    HttpStatusCode.NotFound => "The collection was not found or is not accessible to this account.",
                    HttpStatusCode.TooManyRequests => "The service quota or rate limit was reached. Please try again later.",
                    HttpStatusCode.BadRequest => "The request was rejected. Check your credentials and collection options.",
                    _ => "The service is unavailable. Please try again later."
                };
                throw new ServiceException($"{service}: {reason}", response.StatusCode);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ServiceException($"{service} returned an invalid response.");
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new ServiceException($"Could not reach {service}. Check your connection and try again."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ServiceException($"{service} timed out. Please try again."); }
        catch (JsonException) { throw new ServiceException($"{service} returned an invalid response."); }
    }

    internal static string Query(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return values;
    }

    internal static bool IsId(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    internal static JsonElement Get(this JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    internal static string Text(this JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ValueKind == JsonValueKind.Number ? element.GetRawText() : "";
    internal static string Text(this JsonElement element, string name) => element.Get(name).Text();
    internal static int Number(this JsonElement element, string name) => int.TryParse(element.Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    internal static IEnumerable<JsonElement> Items(this JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    internal static IEnumerable<JsonElement> RequiredItems(this JsonElement element, string name, string service)
    {
        var items = element.Get(name);
        if (items.ValueKind != JsonValueKind.Array) throw new ServiceException($"{service} returned an incomplete response.");
        return items.EnumerateArray();
    }
}
