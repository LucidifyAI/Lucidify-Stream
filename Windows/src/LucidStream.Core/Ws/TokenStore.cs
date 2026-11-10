using System.Security.Cryptography;
using LucidStream.Core.Common;

namespace LucidStream.Core.Ws;

public enum TokenKind { Paired }

public sealed class TokenInfo
{
    public required string Token { get; init; }
    public required TokenKind Kind { get; init; }
    public required IReadOnlySet<string> Scopes { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required string SessionId { get; init; }
    public DateTimeOffset? RedeemBy { get; init; }
    public bool Redeemed { get; set; }
    public string ClientLabel { get; set; } = "";
}

public sealed record PairingCode(string Code, IReadOnlySet<string> Scopes, DateTimeOffset Expires);

public enum TokenCheck { Ok, Invalid }

/// <summary>
/// Client access: the operator shows a 6-digit pairing code (valid 5 minutes, single use) with chosen scopes; the app
/// sends it in hello and gets a token back in welcome, which it reuses to reconnect and resume. Tokens end when
/// streaming stops. Every client, browser or not, connects this way.
/// </summary>
public sealed class TokenStore
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, TokenInfo> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PairingCode> _codes = new(StringComparer.Ordinal);
    private readonly ITimeSource _time;

    public TokenStore(ITimeSource? time = null) => _time = time ?? SystemTimeSource.Instance;

    /// <summary>Raised when tokens are revoked so open connections can be closed with 4007.</summary>
    public event Action<IReadOnlyList<string>>? Revoked;

    public PairingCode CreatePairingCode(IEnumerable<string> scopes)
    {
        lock (_gate)
        {
            string code;
            do { code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"); } while (_codes.ContainsKey(code));
            var pc = new PairingCode(code, new HashSet<string>(scopes), _time.UtcNow + PairingCodeLifetime);
            _codes[code] = pc;
            return pc;
        }
    }

    /// <summary>Exchange a pairing code for a paired token. Single use.</summary>
    public TokenInfo? RedeemPairingCode(string code, string clientLabel)
    {
        lock (_gate)
        {
            if (!_codes.Remove(code, out var pc) || pc.Expires < _time.UtcNow) return null;
            var info = new TokenInfo
            {
                Token = NewToken(32), Kind = TokenKind.Paired, Created = _time.UtcNow, Scopes = pc.Scopes,
                SessionId = NewToken(8), Redeemed = true, ClientLabel = clientLabel
            };
            _tokens[info.Token] = info;
            return info;
        }
    }

    /// <summary>Validate a token presented in hello (a client reconnecting with the token it was given).</summary>
    public TokenCheck Check(string? token, out TokenInfo? info)
    {
        lock (_gate)
        {
            info = null;
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var t)) return TokenCheck.Invalid;
            info = t;
            return TokenCheck.Ok;
        }
    }

    /// <summary>Streaming stopped: every token ends.</summary>
    public void RevokeAll()
    {
        lock (_gate) _codes.Clear();
        Revoke(_ => true);
    }

    public void Revoke(string token) => Revoke(t => t.Token == token);

    private void Revoke(Func<TokenInfo, bool> predicate)
    {
        List<string> gone;
        lock (_gate)
        {
            gone = _tokens.Values.Where(predicate).Select(t => t.Token).ToList();
            foreach (var t in gone) _tokens.Remove(t);
        }
        if (gone.Count > 0) Revoked?.Invoke(gone);
    }

    private static string NewToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}