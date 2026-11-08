using System.Security.Cryptography;
using System.Text.Json;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public sealed record CursorState(string Principal, string Account, string Folder, string Filter, uint Validity, uint Upper, uint Next, long Expires, string Kind = "search");

public sealed class CursorCodec : IDisposable
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public string Encode(CursorState state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state);
        return Convert.ToBase64String(payload) + "." + Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }
    public CursorState Decode(string token, string principal, string account, string folder, string filter, string kind = "search")
    {
        try
        {
            if (token.Length > 16384) throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), signature)) throw new FormatException();
            var state = JsonSerializer.Deserialize<CursorState>(payload) ?? throw new FormatException();
            if (state.Principal != principal || state.Account != account || state.Folder != folder || state.Filter != filter
                || state.Kind != kind || state.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || state.Next > state.Upper)
                throw new FormatException();
            return state;
        }
        catch { throw new ConnectorException(ErrorCodes.InvalidCursor); }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
