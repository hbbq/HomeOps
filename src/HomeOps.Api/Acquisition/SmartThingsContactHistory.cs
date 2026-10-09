using System.Globalization;
using System.Text.Json;

namespace HomeOps.Api.Acquisition;

// A bounded, resumable history read for contact timestamps, not a stream of events.
internal static class SmartThingsContactHistory
{
    internal const int MaxPagesPerPoll = 4;
    private const int PageSize = 100;
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    internal static async Task<IReadOnlyDictionary<string, ContactHistoryUpdate>> ReadAsync(
        HttpClient client, string deviceId, string locationId, IEnumerable<string> pointKeys,
        ContactHistoryProgress previous, DateTimeOffset now, CancellationToken cancellationToken,
        int maxPages = MaxPagesPerPoll)
    {
        var values = pointKeys.ToDictionary(key => key,
            _ => (Opened: (DateTimeOffset?)null, Closed: (DateTimeOffset?)null), StringComparer.Ordinal);
        var scanStartedAt = previous.ScanStartedAt ?? now;
        var cutoff = previous.CheckedAt?.Subtract(Overlap);
        var pageUri = previous.NextPage ??
            $"history/devices?deviceId={Uri.EscapeDataString(deviceId)}&locationId={Uri.EscapeDataString(locationId)}&limit={PageSize}&oldestFirst=false";
        var visited = new HashSet<Uri>();
        var complete = false;
        for (var count = 0; count < maxPages; count++)
        {
            var uri = ValidatePageUri(client.BaseAddress!, pageUri);
            if (!visited.Add(uri)) throw new InvalidOperationException("Repeated contact-history page.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var page = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = page.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Contact history has no items array.");

            // Only stop at the time boundary when this page is ordered newest first.
            // Unknown timestamps prevent the optimization, but never become poll time.
            DateTimeOffset? lastTime = null;
            var ordered = true;
            var reachedCutoff = false;
            foreach (var item in items.EnumerateArray())
            {
                var timestamp = Timestamp(item);
                if (timestamp is null || (lastTime is not null && timestamp > lastTime)) ordered = false;
                lastTime = timestamp;
                if (Text(item, "deviceId") != deviceId) continue;
                if (timestamp is not null && cutoff is not null && timestamp < cutoff)
                {
                    reachedCutoff = true;
                    continue;
                }
                if (Text(item, "capability") != "contactSensor" || Text(item, "attribute") != "contact" ||
                    Text(item, "component") is not { } component || timestamp is null)
                    continue;
                var key = $"{component}/contactSensor/contact";
                if (!values.TryGetValue(key, out var value)) continue;
                switch (Text(item, "value"))
                {
                    case "open" when value.Opened is null || timestamp > value.Opened:
                        value.Opened = timestamp;
                        break;
                    case "closed" when value.Closed is null || timestamp > value.Closed:
                        value.Closed = timestamp;
                        break;
                }
                values[key] = value;
            }

            var next = NextPage(root);
            if (next is not null) next = ValidatePageUri(client.BaseAddress!, next).AbsoluteUri;
            complete = next is null || (ordered && reachedCutoff);
            if (complete) break;
            if (visited.Contains(new Uri(next!))) throw new InvalidOperationException("Repeated contact-history page.");
            pageUri = next!;
        }
        var progress = complete
            ? new ContactHistoryProgress(scanStartedAt)
            : new ContactHistoryProgress(previous.CheckedAt, pageUri, scanStartedAt);
        return values.ToDictionary(pair => pair.Key,
            pair => new ContactHistoryUpdate(pair.Value.Opened, pair.Value.Closed, progress), StringComparer.Ordinal);
    }

    private static Uri ValidatePageUri(Uri baseUri, string address)
    {
        var uri = new Uri(baseUri, address);
        if (uri.Scheme != baseUri.Scheme || uri.Host != baseUri.Host || uri.Port != baseUri.Port ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != new Uri(baseUri, "history/devices").AbsolutePath &&
             uri.AbsolutePath != "/history/devices"))
            throw new InvalidOperationException("Contact-history page is outside the configured API.");
        return uri;
    }

    private static string? NextPage(JsonElement root)
    {
        if (!root.TryGetProperty("_links", out var links)) return null;
        if (links.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid history links.");
        if (!links.TryGetProperty("next", out var next) || next.ValueKind == JsonValueKind.Null) return null;
        return Text(next, "href") ?? throw new InvalidOperationException("Invalid next history page.");
    }

    internal static DateTimeOffset? Timestamp(JsonElement item)
    {
        // The live API's time field can omit milliseconds. Prefer its original epoch.
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("epoch", out var epoch) &&
            epoch.ValueKind == JsonValueKind.Number && epoch.TryGetInt64(out var milliseconds))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        return DateTimeOffset.TryParse(Text(item, "time"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var time) ? time.ToUniversalTime() : null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
