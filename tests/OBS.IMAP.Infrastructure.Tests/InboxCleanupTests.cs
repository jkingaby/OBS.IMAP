using System.Reflection;
using MailKit;
using MimeKit;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class InboxCleanupTests
{
    [Fact]
    public void Copy_expunge_fallback_is_refused_even_with_UIDPLUS()
    {
        Assert.Throws<ConnectorException>(() => MailReadService.RequireNativeCleanupMove(MailKit.Net.Imap.ImapCapabilities.UidPlus));
        MailReadService.RequireNativeCleanupMove(MailKit.Net.Imap.ImapCapabilities.Move);
    }
    [Theory]
    [InlineData("Ovoko <saas@sender.example.test>", true)]
    [InlineData("SAAS@SENDER.EXAMPLE.TEST", true)]
    [InlineData("fake-saas@sender.example.test", false)]
    [InlineData("saas@sender.example.test.evil.test", false)]
    [InlineData("\"saas@sender.example.test\" <other@example.test>", false)]
    [InlineData("saas@sender.example.test, other@example.test", false)]
    [InlineData("Group:saas@sender.example.test;", false)]
    public void Sender_rule_checks_one_exact_mailbox_not_display_name_or_substring(string from, bool expected)
    {
        Assert.Equal(expected, MailReadService.IsExactCleanupSender(InternetAddressList.Parse(from), "saas@sender.example.test"));
        Assert.False(MailReadService.IsExactCleanupSender(null, "saas@sender.example.test"));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Cleanup_denied_before_connect_unless_global_account_and_identity_grants_allow_it(bool global, bool account, bool identity)
    {
        var options = new ConnectorOptions
        {
            EnableInboxCleanup = global, Accounts = [new AccountOptions
            {
                Id = "fixture", Host = "imap.example.test", Username = "synthetic", CredentialReference = "env:UNUSED",
                InboxCleanup = new() { Enabled = account, Sender = "saas@sender.example.test" }
            }]
        };
        await using var service = new MailReadService(options);
        var principal = new RequestIdentity("local", "operator", identity ? ["fixture"] : []);
        var error = await Assert.ThrowsAsync<ConnectorException>(() => service.CleanupInboxAsync(principal, "fixture", true));
        Assert.Equal(ErrorCodes.AccessDenied, error.Code);
    }

    [Theory]
    [InlineData("Trash", FolderAttributes.Trash, 42u, true)]
    [InlineData("Trash", FolderAttributes.Trash, 99u, false)]
    [InlineData("Archive", FolderAttributes.Archive, 42u, false)]
    [InlineData("INBOX", FolderAttributes.Trash | FolderAttributes.Inbox, 42u, false)]
    public async Task Only_validated_UIDs_can_move_to_designated_trash(string name, FolderAttributes attributes, uint validity, bool allowed)
    {
        var inbox = DispatchProxy.Create<IMailFolder, FolderProxy>();
        var trash = DispatchProxy.Create<IMailFolder, FolderProxy>();
        ((FolderProxy)(object)inbox).Validity = validity;
        var destination = (FolderProxy)(object)trash;
        destination.Name = name;
        destination.Attributes = attributes;
        if (allowed)
        {
            await MailReadService.MoveCleanupMatchesAsync(inbox, trash, [new UniqueId(7)], 42, default);
            Assert.Equal(1, ((FolderProxy)(object)inbox).MoveCalls);
        }
        else
        {
            await Assert.ThrowsAsync<ConnectorException>(() => MailReadService.MoveCleanupMatchesAsync(inbox, trash, [new UniqueId(7)], 42, default));
            Assert.Equal(0, ((FolderProxy)(object)inbox).MoveCalls);
        }
    }

    public class FolderProxy : DispatchProxy
    {
        public uint Validity = 42;
        public string Name = "INBOX";
        public FolderAttributes Attributes = FolderAttributes.Inbox;
        public int MoveCalls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_UidValidity": return Validity;
                case "get_FullName": return Name;
                case "get_Attributes": return Attributes;
                case "MoveToAsync":
                    Assert.Equal(7u, Assert.Single((IList<UniqueId>)args![0]!).Id);
                    MoveCalls++;
                    return Task.FromResult(UniqueIdMap.Empty);
                default: throw new InvalidOperationException("Unexpected operation: " + method.Name);
            }
        }
    }
}
