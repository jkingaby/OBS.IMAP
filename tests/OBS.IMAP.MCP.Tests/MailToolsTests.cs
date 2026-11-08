using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OBS.IMAP.Core;
using OBS.IMAP.MCP;
using Xunit;

namespace OBS.IMAP.MCP.Tests;

public sealed class MailToolsTests
{
    [Fact]
    public async Task Caller_identity_is_forwarded_and_sensitive_service_error_is_not_returned()
    {
        var identity = new RequestIdentity("issuer", "subject", ["a"]);
        var service = new TestMailService { Fail = true };
        var tools = new MailTools(service, new TestAccess(identity), new ConnectorOptions());
        var result = await tools.ListAccounts(CancellationToken.None);
        Assert.True(result.IsError);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret-sentinel", serialized);
        Assert.Contains("provider_unavailable", serialized);
        Assert.Same(identity, service.LastIdentity);
    }

    [Fact]
    public async Task Successful_data_is_structured_without_duplicate_text_body()
    {
        var service = new TestMailService();
        var tools = new MailTools(service, new TestAccess(new RequestIdentity("issuer", "subject", ["a"])), new ConnectorOptions());
        var result = await tools.ListAccounts(CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("Mailbox A", result.StructuredContent!.Value.ToString());
        Assert.DoesNotContain("Mailbox A", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Oversized_structured_response_is_replaced_by_safe_limit_error()
    {
        var tools = new MailTools(new TestMailService { Large = true },
            new TestAccess(new RequestIdentity("issuer", "subject", ["a"])),
            new ConnectorOptions { Limits = new ConnectorLimits { MaxResponseBytes = 1000 } });
        var result = await tools.ListAccounts(CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("limit_exceeded", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Core_error_code_and_correlation_are_preserved()
    {
        var tools = new MailTools(new TestMailService(), new TestAccess(new RequestIdentity("issuer", "subject", [])), new ConnectorOptions());
        var result = await tools.GetFolderInfo("a", "INBOX", CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("access_denied", JsonSerializer.Serialize(result));
        Assert.Contains("correlationId", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Operation_logs_are_structured_and_exclude_content_arguments_and_exception_details()
    {
        var logger = new CapturingLogger();
        var identity = new RequestIdentity("issuer-secret-sentinel", "subject-secret-sentinel", ["account-secret-sentinel"]);
        var failingTools = new MailTools(new TestMailService { Fail = true }, new TestAccess(identity), new ConnectorOptions(), logger);
        await failingTools.ListAccounts(CancellationToken.None);
        var deniedTools = new MailTools(new TestMailService(), new TestAccess(identity), new ConnectorOptions(), logger);
        await deniedTools.GetFolderInfo("argument-secret-sentinel", "folder-secret-sentinel", CancellationToken.None);
        await deniedTools.ListAccounts(CancellationToken.None);
        var rendered = string.Join('\n', logger.Messages);
        Assert.Contains("ListAccounts", rendered);
        Assert.Contains("GetFolderInfo", rendered);
        Assert.Contains("provider_unavailable", rendered);
        Assert.Contains("access_denied", rendered);
        Assert.Contains("success", rendered);
        Assert.DoesNotContain("sentinel", rendered);
        Assert.DoesNotContain("Mailbox A", rendered);
        Assert.All(logger.Exceptions, error => Assert.Null(error));
        Assert.All(logger.States, state => Assert.Contains(state, field => field.Key == "Operation"));
    }
}

internal sealed class CapturingLogger : ILogger<MailTools>
{
    public List<string> Messages { get; } = [];
    public List<Exception?> Exceptions { get; } = [];
    public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        Exceptions.Add(exception);
        States.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToArray());
    }
}

internal sealed class TestAccess(RequestIdentity identity) : IRequestAccessContext
{
    public RequestIdentity Identity => identity;
}

internal sealed class TestMailService : IMailReadService
{
    public bool Fail { get; init; }
    public bool Large { get; init; }
    public RequestIdentity? LastIdentity { get; private set; }
    public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(RequestIdentity identity, CancellationToken cancellationToken = default)
    {
        LastIdentity = identity;
        if (Fail) throw new IOException("secret-sentinel");
        return Task.FromResult<IReadOnlyList<AccountInfo>>(identity.AllowedAccounts.Select(id => new AccountInfo(id, Large ? new string('x', 10_000) : "Mailbox A")).ToArray());
    }
    public Task<Page<FolderInfo>> ListFoldersAsync(RequestIdentity identity, FolderListRequest request, CancellationToken cancellationToken = default) => throw Denied();
    public Task<FolderInfo> GetFolderInfoAsync(RequestIdentity identity, string accountId, string folder, CancellationToken cancellationToken = default) => throw Denied();
    public Task<Page<MessageSummary>> SearchMessagesAsync(RequestIdentity identity, SearchRequest request, CancellationToken cancellationToken = default) => throw Denied();
    public Task<MessageHeaders> GetMessageHeadersAsync(RequestIdentity identity, MessageReference message, CancellationToken cancellationToken = default) => throw Denied();
    public Task<MessageContent> GetMessageAsync(RequestIdentity identity, MessageReference message, bool includeHtml = false, CancellationToken cancellationToken = default) => throw Denied();
    public Task<MessageStructure> GetMessageStructureAsync(RequestIdentity identity, MessageReference message, CancellationToken cancellationToken = default) => throw Denied();
    public Task<AttachmentContent> GetAttachmentAsync(RequestIdentity identity, MessageReference message, string partId, CancellationToken cancellationToken = default) => throw Denied();
    private static ConnectorException Denied() => new(ErrorCodes.AccessDenied);
}
