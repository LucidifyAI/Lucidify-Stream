namespace LucidStream.Core.Ws;

/// <summary>WebSocket close codes from the spec ("Close codes" table).</summary>
public static class CloseCodes
{
    public const int InvalidToken = 4001;
    public const int UnsupportedSchema = 4002;
    public const int UnknownStream = 4003;
    public const int ScopeNotGranted = 4004;
    public const int OriginNotAllowed = 4005;
    public const int HostNotAllowed = 4006;
    public const int TokenExpired = 4007;
    public const int TooManyClients = 4008;
    public const int Superseded = 4009;
    public const int DeviceDisconnected = 4010; // documented; the server keeps the socket open and sends status instead.
}

public static class Scopes
{
    public const string ReadRaw = "read:raw";
    public const string ReadProcessed = "read:processed";
    public const string WriteMarkers = "write:markers";
}

public static class ProtocolInfo
{
    public const string SchemaVersion = "1.0.0";
    public const int SchemaMajor = 1;
    public const string Path = "/v1/stream";
    public const int DefaultPort = 8765;
    public const int DefaultDevPagePort = 8766;
    public const string MdnsService = "_lucidify._tcp";
}