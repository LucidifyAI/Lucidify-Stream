using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LucidStream.Core.Common;

namespace LucidStream.Core.Ws;

/// <summary>The allowlist file format (config/allowlist.json).</summary>
public sealed class AllowlistDocument
{
    public int Version { get; set; }
    public List<string> Origins { get; set; } = new();
    /// <summary>Accepted only when developer mode is on (for example http://localhost:8766 for the bundled dev page).</summary>
    public List<string> DevOrigins { get; set; } = new();
}

/// <summary>
/// Security-critical Origin and Host checks for the WebSocket server (spec, "Security").
/// Origins are compared exactly on scheme, host and port after normalization. Production origins must be https.
/// </summary>
public sealed class OriginPolicy
{
    private readonly HashSet<string> _origins;
    private readonly HashSet<string> _devOrigins;
    private volatile HashSet<string> _userOrigins = new();

    public int AllowlistVersion { get; }
    public bool DeveloperMode { get; }

    /// <param name="userOrigins">Origins the user allowed in settings: https, or http on localhost for local development.</param>
    public OriginPolicy(AllowlistDocument doc, bool developerMode, IEnumerable<string>? userOrigins = null)
    {
        AllowlistVersion = doc.Version;
        DeveloperMode = developerMode;
        _origins = new HashSet<string>(doc.Origins.Select(Normalize).Where(o => o != null && o.StartsWith("https://")).Select(o => o!));
        SetUserOrigins(userOrigins ?? Array.Empty<string>());
        _devOrigins = new HashSet<string>(doc.DevOrigins.Select(Normalize).Where(o => o != null).Select(o => o!));
    }

    /// <summary>Replace the user's list; takes effect for the next connection.</summary>
    public void SetUserOrigins(IEnumerable<string> entries) =>
        _userOrigins = new HashSet<string>(entries.SelectMany(UserEntryToOrigins));

    /// <summary>
    /// What a user typed, as origins: a full page address is cut to its origin (browsers send only scheme, host and
    /// port), and an https site is accepted with and without "www." since people type either. https, or http on localhost.
    /// </summary>
    public static IEnumerable<string> UserEntryToOrigins(string entry)
    {
        var text = (entry ?? "").Trim();
        if (text.Length == 0) yield break;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) yield break;
        var o = Normalize(u.GetLeftPart(UriPartial.Authority));
        if (o == null || !(o.StartsWith("https://") || IsLoopbackOrigin(o))) yield break;
        yield return o;
        if (o.StartsWith("https://") && u.HostNameType == UriHostNameType.Dns && u.Host.Contains('.'))
        {
            var host = u.Host.ToLowerInvariant();
            var other = host.StartsWith("www.") ? host[4..] : "www." + host;
            if (other.Contains('.')) yield return u.IsDefaultPort ? $"https://{other}" : $"https://{other}:{u.Port}";
        }
    }

    private static bool IsLoopbackOrigin(string normalized)
    {
        var rest = normalized.StartsWith("http://") ? normalized["http://".Length..] : "";
        return rest == "localhost" || rest.StartsWith("localhost:") || rest == "127.0.0.1" || rest.StartsWith("127.0.0.1:") || rest.StartsWith("[::1]");
    }

    public IReadOnlyCollection<string> Origins => _origins;

    public bool IsAllowed(string? origin)
    {
        var n = Normalize(origin);
        if (n == null) return false;
        return _origins.Contains(n) || _userOrigins.Contains(n) || (DeveloperMode && _devOrigins.Contains(n));
    }

    /// <summary>Lower-case scheme://host[:port] with default ports removed. Returns null for anything unparseable or "null".</summary>
    public static string? Normalize(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin == "null") return null;
        if (!Uri.TryCreate(origin.Trim().TrimEnd('/'), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != "https" && u.Scheme != "http") return null;
        if (u.AbsolutePath != "/" || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        var host = u.IdnHost.ToLowerInvariant();
        if (u.HostNameType == UriHostNameType.IPv6) host = $"[{host.Trim('[', ']')}]";
        return u.IsDefaultPort ? $"{u.Scheme}://{host}" : $"{u.Scheme}://{host}:{u.Port}";
    }

    /// <summary>
    /// DNS-rebinding protection for localhost mode: the Host header must name the loopback interface and our port.
    /// </summary>
    public static bool IsLoopbackHostAllowed(string? hostHeader, int port)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        var h = hostHeader.Trim().ToLowerInvariant();
        foreach (var name in new[] { "localhost", "127.0.0.1", "[::1]" })
        {
            if (h == $"{name}:{port}") return true;
        }
        return false;
    }

    /// <summary>LAN mode: Host must be the bound LAN address with our port.</summary>
    public static bool IsLanHostAllowed(string? hostHeader, string lanAddress, int port)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        return string.Equals(hostHeader.Trim(), $"{lanAddress}:{port}", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Loads the allowlist. Order: a downloaded update in %LOCALAPPDATA%\LucidStream (accepted only if its
/// ECDSA P-256 / SHA-256 signature verifies against Lucidify's public key), else the list shipped with the app.
/// </summary>
public static class AllowlistLoader
{
    /// <summary>
    /// TODO(security): replace with Lucidify's allowlist-signing public key (PEM, ECDSA P-256).
    /// While this is a placeholder, downloaded updates are ignored and the shipped list is used.
    /// </summary>
    public const string LucidifyPublicKeyPem = "REPLACE_WITH_LUCIDIFY_ALLOWLIST_PUBLIC_KEY_PEM";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public static string UpdateFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LucidStream");

    public static AllowlistDocument Load(string appDirectory, string? publicKeyPem = null)
    {
        publicKeyPem ??= LucidifyPublicKeyPem;
        var updateJson = Path.Combine(UpdateFolder, "allowlist.json");
        var updateSig = Path.Combine(UpdateFolder, "allowlist.sig");
        if (File.Exists(updateJson) && File.Exists(updateSig) && publicKeyPem.Contains("BEGIN PUBLIC KEY"))
        {
            var bytes = File.ReadAllBytes(updateJson);
            var sig = File.ReadAllText(updateSig).Trim();
            if (Verify(bytes, sig, publicKeyPem) && TryParse(bytes, out var updated))
            {
                Log.Info($"Allowlist v{updated!.Version} loaded from signed update.");
                return updated;
            }
            Log.Warn("Allowlist update failed signature check; using the list shipped with the app.");
        }

        var shipped = Path.Combine(appDirectory, "config", "allowlist.json");
        if (File.Exists(shipped) && TryParse(File.ReadAllBytes(shipped), out var doc))
        {
            Log.Info($"Allowlist v{doc!.Version} loaded ({doc.Origins.Count} origins).");
            return doc;
        }
        Log.Warn("No allowlist found; only developer origins (if enabled) can connect.");
        return new AllowlistDocument { Version = 0 };
    }

    /// <summary>Verify a signed update and save it as the new allowlist. Returns false (and keeps the old list) if it fails.</summary>
    public static bool TryInstallUpdate(byte[] json, string signatureBase64, string? publicKeyPem = null)
    {
        publicKeyPem ??= LucidifyPublicKeyPem;
        if (!publicKeyPem.Contains("BEGIN PUBLIC KEY") || !Verify(json, signatureBase64, publicKeyPem) || !TryParse(json, out _))
            return false;
        Directory.CreateDirectory(UpdateFolder);
        File.WriteAllBytes(Path.Combine(UpdateFolder, "allowlist.json"), json);
        File.WriteAllText(Path.Combine(UpdateFolder, "allowlist.sig"), signatureBase64);
        return true;
    }

    public static bool Verify(byte[] data, string signatureBase64, string publicKeyPem)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (Exception) { return false; }
    }

    public static bool TryParse(byte[] bytes, out AllowlistDocument? doc)
    {
        try
        {
            doc = JsonSerializer.Deserialize<AllowlistDocument>(Encoding.UTF8.GetString(bytes), Json);
            return doc != null;
        }
        catch (JsonException) { doc = null; return false; }
    }
}