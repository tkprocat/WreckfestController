using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class ConsoleLogWebhookSenderTests
{
    [Fact]
    public void Dispose_SendsTheBufferedLinesItAdvertises()
    {
        var handler = new RecordingHandler();
        var sender = MakeSender(handler);

        sender.AddLog("last line before shutdown");
        sender.Dispose();

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("last line before shutdown", body);
    }

    [Fact]
    public void AddLog_IsIgnoredOnceDisposalHasStarted()
    {
        var handler = new RecordingHandler();
        var sender = MakeSender(handler);

        sender.Dispose();
        handler.Bodies.Clear();
        sender.AddLog("arrived too late");
        sender.Dispose();

        Assert.Empty(handler.Bodies);
    }

    private static ConsoleLogWebhookSender MakeSender(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:Enabled"] = "true",
                ["Webhooks:ApiKey"] = "test-key",
                ["Webhooks:BaseUrl"] = "https://example.test/webhooks"
            })
            .Build();

        return new ConsoleLogWebhookSender(
            new HttpClient(handler),
            configuration,
            Mock.Of<ILogger<ConsoleLogWebhookSender>>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (Bodies)
            {
                Bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
