using System.Reflection;
using MailKit;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class FolderStatusTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_reads_all_counts_only_after_deselecting_target(bool initiallySelected)
    {
        var folder = DispatchProxy.Create<IMailFolder, FolderProxy>();
        var proxy = (FolderProxy)(object)folder;
        proxy.Selected = initiallySelected;
        var result = await MailReadService.ReadFolderInfoAsync(folder, default);
        Assert.Equal(initiallySelected ? ["close", "status"] : ["status"], proxy.Operations);
        Assert.Equal(3, result.MessageCount);
        Assert.Equal(2, result.UnreadCount);
        Assert.Equal(42u, result.UidValidity);
    }

    public class FolderProxy : DispatchProxy
    {
        public bool Selected;
        public List<string> Operations { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_IsOpen": return Selected;
                case "CloseAsync":
                    Assert.Equal(false, args![0]);
                    Selected = false;
                    Operations.Add("close");
                    return Task.CompletedTask;
                case "StatusAsync":
                    Assert.False(Selected);
                    Assert.Equal(StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity, args![0]);
                    Operations.Add("status");
                    return Task.CompletedTask;
                case "get_FullName": return "INBOX";
                case "get_DirectorySeparator": return '/';
                case "get_Attributes": return FolderAttributes.Inbox;
                case "get_Count": return 3;
                case "get_Unread": return 2;
                case "get_UidValidity": return 42u;
                default: throw new InvalidOperationException(targetMethod.Name);
            }
        }
    }
}
