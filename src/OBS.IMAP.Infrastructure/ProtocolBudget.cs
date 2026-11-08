using System.Text;
using MailKit;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

// MailKit ImapStream calls LogServer before advancing its input buffer to the parser.
// This logger retains no protocol trace. It rejects oversized tokens/literals/depth before parsing.
public sealed class ProtocolBudget(int maximumBytes, int maximumLiteralBytes, int maximumLineBytes = 65536, int maximumDepth = 64) : IProtocolLogger
{
    private readonly byte[] line = new byte[maximumLineBytes];
    private int lineLength, depth, received;
    private long literalTotal;
    public int LiteralLimit { get; set; } = Math.Min(maximumLiteralBytes, maximumLineBytes);
    private long literalRemaining;
    private bool quoted, escaped;
    public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }
    public void LogConnect(Uri uri) { }
    public void LogClient(byte[] buffer, int offset, int count) { }
    public void Reset()
    {
        received = lineLength = depth = 0;
        literalRemaining = 0;
        literalTotal = 0;
        LiteralLimit = Math.Min(maximumLiteralBytes, maximumLineBytes);
        quoted = escaped = false;
    }
    public void LogServer(byte[] buffer, int offset, int count)
    {
        if (count > maximumBytes - received) throw new ConnectorException(ErrorCodes.LimitExceeded);
        received += count;
        foreach (var value in buffer.AsSpan(offset, count))
        {
            if (literalRemaining > 0) { literalRemaining--; continue; }
            if (lineLength == line.Length) throw new ConnectorException(ErrorCodes.LimitExceeded);
            line[lineLength++] = value;
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') quoted = false;
            }
            else if (value == '"') quoted = true;
            else if (value == '(' && ++depth > maximumDepth) throw new ConnectorException(ErrorCodes.LimitExceeded);
            else if (value == ')') depth = Math.Max(0, depth - 1);
            if (value != '\n') continue;
            var end = lineLength - 1;
            if (end > 0 && line[end - 1] == '\r') end--;
            if (!quoted && end > 1 && line[end - 1] == '}')
            {
                var start = end - 2;
                if (line[start] == '+') start--;
                while (start >= 0 && line[start] is >= (byte)'0' and <= (byte)'9') start--;
                if (start >= 0 && line[start] == '{')
                {
                    var number = Encoding.ASCII.GetString(line, start + 1, end - start - 2).TrimEnd('+');
                    if (!long.TryParse(number, out var length) || length > LiteralLimit || length > maximumBytes - received
                        || (literalTotal += length) > maximumLiteralBytes)
                        throw new ConnectorException(ErrorCodes.LimitExceeded);
                    literalRemaining = length;
                }
            }
            lineLength = 0;
            // A literal header, including {0}, may split a still-open nested response.
            // Never reset lexical depth at a newline: only actual closing tokens reduce it.
        }
    }
    public void Dispose() => Array.Clear(line);
}
