using System.Text;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

internal static class MailTextEncoding
{
    static MailTextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static Encoding Get(string? charset)
    {
        try { return Encoding.GetEncoding(charset ?? "utf-8"); }
        catch (ArgumentException) { throw new ConnectorException(ErrorCodes.ProviderUnavailable); }
        catch (NotSupportedException) { throw new ConnectorException(ErrorCodes.ProviderUnavailable); }
    }
}
