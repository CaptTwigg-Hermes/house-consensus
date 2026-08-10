using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using HouseConsensus.Client.Services;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Diagnostics;
using HouseConsensus.Server.Hubs;
using HouseConsensus.Server.Learning;
using HouseConsensus.Server.Listings;
using HouseConsensus.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class StructuredDiagnosticsTests
{
    [Fact]
    public void Production_logging_suppresses_HttpClient_request_URI_events()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Server", "appsettings.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName, "src", "Server", "appsettings.json")));

        Assert.Equal("Warning", settings?["Serilog"]?["MinimumLevel"]?["Override"]?["System.Net.Http.HttpClient"]?.GetValue<string>());
        Assert.Equal("Warning", settings?["Logging"]?["LogLevel"]?["System.Net.Http.HttpClient"]?.GetValue<string>());
    }

    [Fact]
    public async Task SignalR_diagnostics_preserve_the_existing_hub_contract()
    {
        var listingId = Guid.Parse("d35fd9ac-709e-4af4-868e-d3a45e8c45b7");
        var groups = new RecordingGroupManager();
        var hub = new ConsensusHub(NullLogger<ConsensusHub>.Instance)
        {
            Context = new TestHubCallerContext("connection-42"),
            Groups = groups,
        };

        await hub.WatchListing(listingId);
        await hub.LeaveListing(listingId);

        Assert.Equal($"listing:{listingId}", ConsensusHub.Group(listingId));
        Assert.Equal(("connection-42", $"listing:{listingId}"), groups.Added);
        Assert.Equal(("connection-42", $"listing:{listingId}"), groups.Removed);
    }

    [Fact]
    public void Diagnostic_text_redacts_tokens_and_url_queries()
    {
        var sanitized = DiagnosticText.Sanitize(
            "Failed https://house.test/api/auth/consume?token=super-secret and password=hunter2");

        Assert.DoesNotContain("super-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_text_removes_control_characters_used_for_log_forging()
    {
        var sanitized = DiagnosticText.Sanitize("first\r\nsecond\tthird");

        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\t', sanitized);
        Assert.Equal("first  second third", sanitized);
    }

    [Fact]
    public async Task Listing_image_rejection_logs_safe_structured_context()
    {
        var listingId = Guid.NewGuid();
        var source = "https://images.boligsiden.dk/private-name.webp?token=image-secret";
        var logger = new RecordingLogger<ListingImageService>();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        var service = new ListingImageService(
            new HttpClient(new StaticResponseHandler(HttpStatusCode.BadGateway, "text/plain", "failure")),
            cache,
            logger);

        Assert.Null(await service.GetAsync(listingId, source, TestContext.Current.CancellationToken));

        var entry = Assert.Single(logger.Entries, x => x.EventId.Id == DiagnosticEventIds.ListingImageRejected);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(listingId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-name", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("image-secret", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_image_transport_failure_is_logged_without_exception_payload()
    {
        var listingId = Guid.NewGuid();
        var logger = new RecordingLogger<ListingImageService>();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        var service = new ListingImageService(
            new HttpClient(new ThrowingHandler(new HttpRequestException("https://images.boligsiden.dk/a?token=secret"))),
            cache,
            logger);

        Assert.Null(await service.GetAsync(
            listingId,
            "https://images.boligsiden.dk/a?token=secret",
            TestContext.Current.CancellationToken));

        var entry = Assert.Single(logger.Entries, x => x.EventId.Id == DiagnosticEventIds.ListingImageRejected);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloudflare_JWKS_failure_is_logged_without_assertion_content()
    {
        const string assertion = "eyJhbGciOiJSUzI1NiIsImtpZCI6IngifQ.e30.AA";
        var logger = new RecordingLogger<CloudflareJwtValidator>();
        var validator = new CloudflareJwtValidator(
            new HttpClient(new ThrowingHandler(new HttpRequestException("jwks unavailable"))),
            new CloudflareAccessOptions(true, "team.cloudflareaccess.com", "audience"),
            TimeProvider.System,
            logger);

        Assert.Null(await validator.ValidateAsync(assertion, TestContext.Current.CancellationToken));

        var entry = Assert.Single(logger.Entries, x => x.EventId.Id == DiagnosticEventIds.CloudflareValidationFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(assertion, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AI_upstream_failure_logs_model_and_status_without_prompt_or_key()
    {
        var logger = new RecordingLogger<OllamaAiRuleGenerator>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiLearning:BaseUrl"] = "https://ollama.test",
            ["AiLearning:Model"] = "diagnostic-model",
            ["AiLearning:ApiKey"] = "api-secret",
        }).Build();
        var generator = new OllamaAiRuleGenerator(
            new HttpClient(new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "application/json", "{}")),
            config,
            logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(
            [new VoteNoteInput(1, Guid.NewGuid(), Guid.NewGuid(), VoteChoice.Like, [], "private-note")],
            TestContext.Current.CancellationToken));

        var entry = Assert.Single(logger.Entries, x => x.EventId.Id == DiagnosticEventIds.AiGenerationFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("diagnostic-model", entry.Message, StringComparison.Ordinal);
        Assert.Contains("503", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-note", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_diagnostic_report_redacts_sensitive_exception_text()
    {
        var handler = new CaptureHandler();
        var diagnostics = new ClientDiagnostics(new HttpClient(handler) { BaseAddress = new Uri("https://house.test/") });

        await diagnostics.ReportAsync(
            "signalr.start",
            new InvalidOperationException("Failed /hubs/consensus?access_token=client-secret for private@example.test; Cookie=session-secret; Bearer bearer-secret; feedback private-note"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.Body);
        Assert.Contains("signalr.start", handler.Body, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", handler.Body, StringComparison.Ordinal);
        Assert.Contains("fingerprint", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-note", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_client_diagnostic_sink_logs_member_and_trace_without_secrets()
    {
        var logger = new RecordingLogger<ClientDiagnosticSink>();
        var sink = new ClientDiagnosticSink(logger);
        var memberId = Guid.NewGuid();

        sink.Record(
            new ClientErrorReport("render-private@example.test", "InvalidOperationException-private-note", new string('A', 64)),
            memberId,
            "trace-42");

        var entry = Assert.Single(logger.Entries, x => x.EventId.Id == DiagnosticEventIds.ClientApplicationError);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(memberId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("trace-42", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-note", entry.Message, StringComparison.Ordinal);
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class StaticResponseHandler(HttpStatusCode status, string contentType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
                RequestMessage = request,
            });
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request };
        }
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public (string ConnectionId, string GroupName)? Added { get; private set; }
        public (string ConnectionId, string GroupName)? Removed { get; private set; }
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added = (connectionId, groupName);
            return Task.CompletedTask;
        }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed = (connectionId, groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHubCallerContext(string connectionId) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => "member-42";
        public override ClaimsPrincipal? User => null;
        private readonly Dictionary<object, object?> _items = [];
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
