using System.Net;
using System.Text;
using System.Text.Json;
using HomeOps.Api.Acquisition;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class SmartThingsContactHistoryTests
{
    private const string PointKey = "main/contactSensor/contact";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T10:00:00Z");

    [Fact]
    public async Task ReadAsync_FollowsUnversionedPagingPastUnrelatedEventsAndMatchesIdentity()
    {
        var handler = new Handler(
            Page([Event("battery", 1791498886791)], "/history/devices?pagingBeforeEpoch=1791498886791&pagingBeforeHash=2959498826"),
            Page([
                Event("closed", 1791480076322, time: "2026-10-08T17:21:16.000Z"),
                Event("open", 1791480071463, time: "2026-10-08T17:21:11Z"),
                Event("open", 1791480079999, device: "other"),
                Event("open", 1791480079999, component: "other"),
                Event("open", 1791480079999, capability: "motionSensor"),
                Event("open", 1791480079999, attribute: "somethingElse"),
                Event("unknown", 1791480079999),
                Event("closed", null)
            ]));
        var updates = await Read(handler, new());
        var update = updates[PointKey];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791480071463), update.LastOpened);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791480076322), update.LastClosed);
        Assert.Equal(new ContactHistoryProgress(Now), update.Progress);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/history/devices", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task ReadAsync_PageBudgetSavesContinuationWithoutAdvancingCompletedCoverage()
    {
        var previous = new ContactHistoryProgress(Now.AddHours(-1));
        var first = new Handler(Page([Event("open", Now.ToUnixTimeMilliseconds())], "/history/devices?page=2"));
        var update = (await Read(first, previous, maxPages: 1))[PointKey];
        Assert.Equal(previous.CheckedAt, update.Progress.CheckedAt);
        Assert.Equal(Now, update.Progress.ScanStartedAt);
        Assert.EndsWith("/history/devices?page=2", update.Progress.NextPage);

        // Simulates restarting the reader and loading its durable progress.
        var second = new Handler(Page([Event("closed", Now.AddMinutes(-2).ToUnixTimeMilliseconds())]));
        var resumed = (await Read(second, update.Progress, now: Now.AddMinutes(1)))[PointKey];
        Assert.Equal("?page=2", Assert.Single(second.Requests).Query);
        Assert.Equal(new ContactHistoryProgress(Now), resumed.Progress);
        Assert.NotNull(resumed.LastClosed);
    }

    [Fact]
    public async Task ReadAsync_IncrementalOverlapStopsLocallyAtBoundaryAndDoesNotScanOlderPages()
    {
        var handler = new Handler(Page([
            Event("open", Now.AddMinutes(-2).ToUnixTimeMilliseconds()),
            Event("battery", Now.AddMinutes(-6).ToUnixTimeMilliseconds())
        ], "/history/devices?page=2"));
        var update = (await Read(handler, new(Now)))[PointKey];
        Assert.Single(handler.Requests);
        Assert.Equal(new ContactHistoryProgress(Now), update.Progress);
        Assert.Equal(Now.AddMinutes(-2), update.LastOpened);
    }

    [Fact]
    public async Task ReadAsync_SameEpochOpenAndClosedBothContributeAndEmptyHistoryStaysUnknown()
    {
        var epoch = Now.ToUnixTimeMilliseconds();
        var both = (await Read(new Handler(Page([Event("open", epoch), Event("closed", epoch)])), new()))[PointKey];
        Assert.Equal(Now, both.LastOpened);
        Assert.Equal(Now, both.LastClosed);
        var empty = (await Read(new Handler(Page([])), new()))[PointKey];
        Assert.Null(empty.LastOpened);
        Assert.Null(empty.LastClosed);
    }

    [Fact]
    public async Task ReadAsync_KeepsComponentsSeparateAndUsesOnlyValidHistoryTimestamps()
    {
        var handler = new Handler(Page([
            Event("open", Now.ToUnixTimeMilliseconds(), component: "main"),
            Event("closed", null, component: "secondary", time: "2026-10-09T09:00:00Z"),
            Event("open", null, component: "secondary", time: "invalid")
        ]));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.smartthings.com/v1/") };
        var values = await SmartThingsContactHistory.ReadAsync(client, "device", "location",
            [PointKey, "secondary/contactSensor/contact"], new(), Now, CancellationToken.None);
        Assert.Equal(Now, values[PointKey].LastOpened);
        Assert.Null(values[PointKey].LastClosed);
        Assert.Null(values["secondary/contactSensor/contact"].LastOpened);
        Assert.Equal(Now.AddHours(-1), values["secondary/contactSensor/contact"].LastClosed);
    }

    [Fact]
    public async Task ReadAsync_FailedLaterPageDoesNotReturnPartialProgress()
    {
        var handler = new Handler(Page([Event("open", Now.ToUnixTimeMilliseconds())], "/history/devices?page=2"),
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await Assert.ThrowsAsync<HttpRequestException>(() => Read(handler, new()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://other.example/history/devices?page=2")]
    [InlineData("https://api.smartthings.com/oauth/token")]
    [InlineData("https://api.smartthings.com/history/devices")]
    public async Task ReadAsync_RejectsUnsafeOrRepeatedNextPage(string next)
    {
        // The last case repeats the current page after starting from that alias.
        var progress = next.EndsWith("/history/devices") ? new ContactHistoryProgress(null, next, Now) : new();
        var handler = new Handler(Page([], next));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(handler, progress));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReadAsync_MissingItemsIsFailureRatherThanCompletedHistory()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(new Handler(Json("{}")), new()));
    }

    private static async Task<IReadOnlyDictionary<string, ContactHistoryUpdate>> Read(
        Handler handler, ContactHistoryProgress progress, int maxPages = 4, DateTimeOffset? now = null)
    {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.smartthings.com/v1/") };
        return await SmartThingsContactHistory.ReadAsync(client, "device", "location", [PointKey],
            progress, now ?? Now, CancellationToken.None, maxPages);
    }

    private static object Event(string value, long? epoch, string device = "device", string component = "main",
        string capability = "contactSensor", string attribute = "contact", string? time = null) =>
        new { deviceId = device, component, capability, attribute, value, epoch, time };
    private static HttpResponseMessage Page(object[] items, string? next = null) => Json(JsonSerializer.Serialize(new
    {
        items, _links = next is null ? new Dictionary<string, object>() : new() { ["next"] = new { href = next } }
    }));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    private sealed class Handler(params HttpResponseMessage[] pages) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _pages = new(pages);
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_pages.Dequeue());
        }
    }
}
