using System.Text;
using MimeKit;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class MimeLimitTests
{
    [Theory]
    [InlineData("aGVsbG8gd29ybGQ=", ContentEncoding.Base64)]
    [InlineData("hello=20world", ContentEncoding.QuotedPrintable)]
    public async Task Transfer_decoding_preserves_data_and_marks_text_truncation(string wire, ContentEncoding encoding)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(wire));
        using var content = new MimeContent(source, encoding);
        using var output = new CappedMemoryStream(5, true);
        await content.DecodeToAsync(output);
        Assert.Equal("hello", Encoding.ASCII.GetString(output.ToArray()));
        Assert.True(output.Truncated);
        Assert.Equal(5, output.Length);
    }

    [Fact]
    public async Task Oversized_decoded_binary_is_rejected_without_partial_result()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("aGVsbG8gd29ybGQ="));
        using var content = new MimeContent(source, ContentEncoding.Base64);
        using var output = new CappedMemoryStream(5, false);
        Assert.Equal(ErrorCodes.LimitExceeded, (await Assert.ThrowsAsync<ConnectorException>(() => content.DecodeToAsync(output))).Code);
    }

    [Fact]
    public void Single_byte_and_async_write_paths_obey_the_same_cap()
    {
        using var output = new CappedMemoryStream(1, false);
        output.WriteByte(1);
        Assert.Throws<ConnectorException>(() => output.WriteByte(2));
        Assert.Throws<ConnectorException>(() => output.Write([3], 0, 1));
    }

    [Fact]
    public void Legacy_declared_charsets_decode_and_unknown_charset_fails_safely()
    {
        Assert.Equal("café", MailTextEncoding.Get("windows-1252").GetString([0x63, 0x61, 0x66, 0xe9]));
        Assert.Equal("あ", MailTextEncoding.Get("shift_jis").GetString([0x82, 0xa0]));
        Assert.Equal(ErrorCodes.ProviderUnavailable, Assert.Throws<ConnectorException>(() => MailTextEncoding.Get("unknown-sender-charset")).Code);
        Assert.Equal("UTF-8", MailTextEncoding.Get(null).WebName.ToUpperInvariant());
    }
}
