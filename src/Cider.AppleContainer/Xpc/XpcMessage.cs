namespace Cider.AppleContainer.Xpc;

/// <summary>
/// One request or reply on the wire: a flat <see cref="XpcDictionary"/> plus the route key every
/// apiserver message carries (<c>XPCMessage.routeKey</c>,
/// docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.2). Typed accessors forward straight to the
/// underlying dictionary — this type only adds the route and the error-envelope key.
/// </summary>
internal sealed class XpcMessage : IDisposable
{
    /// <summary><c>XPCMessage.routeKey</c> — every request and its reply carry this.</summary>
    public const string RouteKey = "com.apple.container.xpc.route";

    /// <summary><c>XPCMessage.errorKey</c> — present on a reply only when the call failed
    /// (§1.3: errors ride inside an ordinary reply dictionary, never as a separate XPC error object).</summary>
    public const string ErrorKey = "com.apple.container.xpc.error";

    private readonly XpcDictionary _dict;

    /// <summary>Builds a new outbound request for <paramref name="route"/> (an <c>XPCRoute</c>
    /// raw value, e.g. <c>"ping"</c>, <c>"containerList"</c>).</summary>
    public XpcMessage(string route)
    {
        _dict = new XpcDictionary();
        _dict.SetString(RouteKey, route);
        Route = route;
    }

    /// <summary>Wraps a reply dictionary this instance now owns.</summary>
    public XpcMessage(XpcDictionary dict)
    {
        _dict = dict;
        Route = dict.GetString(RouteKey) ?? string.Empty;
    }

    public string Route { get; }

    public XpcDictionary Dictionary => _dict;

    public void SetString(string key, string value) => _dict.SetString(key, value);

    public string? GetString(string key) => _dict.GetString(key);

    public void SetData(string key, ReadOnlySpan<byte> value) => _dict.SetData(key, value);

    public byte[]? GetData(string key) => _dict.GetData(key);

    public void SetBool(string key, bool value) => _dict.SetBool(key, value);

    public bool GetBool(string key) => _dict.GetBool(key);

    public void SetUInt64(string key, ulong value) => _dict.SetUInt64(key, value);

    public ulong GetUInt64(string key) => _dict.GetUInt64(key);

    public void SetInt64(string key, long value) => _dict.SetInt64(key, value);

    public long GetInt64(string key) => _dict.GetInt64(key);

    public void SetDate(string key, DateTimeOffset value) => _dict.SetDate(key, value);

    public DateTimeOffset GetDate(string key) => _dict.GetDate(key);

    public void SetFd(string key, int fd) => _dict.SetFd(key, fd);

    public int DupFd(string key) => _dict.DupFd(key);

    public void SetValue(string key, XpcObject value) => _dict.SetValue(key, value);

    public bool ContainsKey(string key) => _dict.ContainsKey(key);

    /// <summary>The raw <see cref="ErrorKey"/> JSON bytes, or <c>null</c> when the reply carries
    /// no error (a successful call).</summary>
    public byte[]? GetErrorEnvelope() => _dict.GetData(ErrorKey);

    public void Dispose() => _dict.Dispose();
}
