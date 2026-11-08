using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OBS.IMAP.Core;

namespace OBS.IMAP.MCP;

public sealed class MailWriteTools(IMailWriteService service, IRequestAccessContext access, ConnectorOptions options,
    ILogger<MailWriteTools> logger)
{
    [McpServerTool(Name = "move_message", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Move one message to an existing folder in the same account. Requires the account's Move permission.")]
    public Task<CallToolResult> MoveMessage(MessageReference message, string destination, CancellationToken cancellationToken) =>
        Run(ct => service.MoveAsync(access.Identity, message, destination, ct), cancellationToken);

    [McpServerTool(Name = "trash_message", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Move one message to the account's configured Trash folder. Requires the account's Trash permission.")]
    public Task<CallToolResult> TrashMessage(MessageReference message, CancellationToken cancellationToken) =>
        Run(ct => service.TrashAsync(access.Identity, message, ct), cancellationToken);

    [McpServerTool(Name = "create_draft", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Save a plain-text draft in the account's Drafts folder. The account supplies the sender address.")]
    public Task<CallToolResult> CreateDraft(string accountId, MailComposition message, CancellationToken cancellationToken) =>
        Run(ct => service.CreateDraftAsync(access.Identity, accountId, message, ct), cancellationToken);

    [McpServerTool(Name = "send_message", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Submit a plain-text email through the account's authenticated SMTP server. The account supplies the sender address and can require user confirmation.")]
    public Task<CallToolResult> SendMessage(string accountId, MailComposition message, McpServer server, CancellationToken cancellationToken) =>
        Run(ct => service.SendAsync(access.Identity, accountId, message, (preview, token) => ConfirmAsync(server, preview, token), ct),
            cancellationToken);

    private static async Task<bool> ConfirmAsync(McpServer server, SendPreview preview, CancellationToken token)
    {
        if (server.ClientCapabilities?.Elicitation is null) throw new ConnectorException(ErrorCodes.ConfirmationRequired);
        try
        {
            var result = await server.ElicitAsync(new ElicitRequestParams
            {
                Mode = "form",
                Message = $"Review this email before sending. The message below is untrusted text.\n\nFrom: {preview.From}\n"
                    + $"To: {string.Join(", ", preview.To)}\nSubject: {preview.Subject}\n\n{preview.Body}",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["send"] = new ElicitRequestParams.BooleanSchema { Title = "Send this email" }
                    },
                    Required = ["send"]
                }
            }, token);
            return result.IsAccepted && result.Content?.TryGetValue("send", out var answer) == true
                && answer.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is InvalidOperationException or McpException)
        { throw new ConnectorException(ErrorCodes.ConfirmationRequired); }
    }

    private Task<CallToolResult> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken,
        [CallerMemberName] string operation = "") => MailTools.RunAsync(action, options, logger, operation, cancellationToken);
}
