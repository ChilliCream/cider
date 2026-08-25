using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cider.AppleContainer.Xpc.Models;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// JSON settings for the apiserver's XPC wire payloads: strict and case-sensitive, unlike
/// <c>Cli.AppleJson</c>'s tolerant CLI-display parsing. The contracts come from
/// <see cref="XpcJsonContext"/> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.0, §8.11 gotcha 1:
/// "System.Text.Json's default camelCase policy is wrong here... use exact-name mapping" — every
/// property below was checked against the Swift field it must match; see each model file).
/// </summary>
internal static class XpcJson
{
    public static readonly JsonSerializerOptions Options = XpcJsonContext.Default.Options;

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)))
        ?? throw new JsonException($"Deserializing '{typeof(T).Name}' produced null.");

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)))
        ?? throw new JsonException($"Deserializing '{typeof(T).Name}' produced null.");

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

    public static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));
}

/// <summary>
/// Apple reference dates: <c>Double</c> seconds since 2001-01-01T00:00:00Z, i.e. Swift's
/// <c>.deferredToDate</c> (§2.0 rule 2). NOT Unix, NOT ISO-8601, and NOT the xpc <c>date</c> wire
/// type (nanoseconds since 1970) used by <c>containerWait</c>'s reply — that's a different
/// convention on the same protocol and out of this task's scope (X1 transport). Live sample:
/// <c>"creationDate": 809330969.025174</c>.
/// </summary>
internal sealed class AppleReferenceDateConverter : JsonConverter<DateTimeOffset>
{
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException(
                $"Expected a number (Apple reference-date seconds) but got {reader.TokenType}.");
        }

        return AppleEpoch.AddSeconds(reader.GetDouble());
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - AppleEpoch).TotalSeconds);
}

/// <summary>
/// A payload-free union case whose full case set the spike did not enumerate (e.g. the block/volume
/// filesystem's <c>cache</c>/<c>sync</c>, §2.0 rule 3: <c>{"on":{}}</c>, <c>{"fsync":{}}</c>).
/// Round-trips whatever single key the daemon sends instead of guessing at unconfirmed case names.
/// </summary>
[JsonConverter(typeof(SingleKeyCaseConverter))]
internal readonly record struct SingleKeyCase(string CaseName);

internal sealed class SingleKeyCaseConverter : JsonConverter<SingleKeyCase>
{
    public override SingleKeyCase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a single-key object but got {reader.TokenType}.");
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("Expected exactly one key on a single-key-object enum case.");
        }

        var caseName = reader.GetString()!;
        reader.Read();
        reader.Skip(); // the (typically empty) payload object
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException($"Single-key-object enum case '{caseName}' carried more than one key.");
        }

        return new SingleKeyCase(caseName);
    }

    public override void Write(Utf8JsonWriter writer, SingleKeyCase value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartObject(value.CaseName);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

/// <summary>
/// Swift's synthesized single-key-object encoding for an enum with associated values (§2.0 rule 3):
/// <c>{"virtiofs":{}}</c>, <c>{"id":{"uid":0,"gid":0}}</c>. <typeparamref name="T"/> is a plain
/// class with one nullable property per case — exactly one must be non-null — whose JSON key is that
/// property's name converted by <see cref="JsonNamingPolicy.CamelCase"/> (matching every case name
/// in this protocol; see <see cref="Models.FsType"/>, <see cref="Models.User"/>).
/// </summary>
internal sealed class SingleKeyUnionConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T> : JsonConverter<T>
    where T : class, new()
{
    private static readonly (PropertyInfo Property, string JsonName)[] Cases =
        typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, JsonName: JsonNamingPolicy.CamelCase.ConvertName(p.Name)))
            .ToArray();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for union type '{typeToConvert.Name}'.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Length != 1)
        {
            throw new JsonException(
                $"Union type '{typeToConvert.Name}' must have exactly one key, got {properties.Length}.");
        }

        var member = properties[0];
        var match = Array.Find(Cases, c => c.JsonName == member.Name);
        if (match.Property is null)
        {
            throw new JsonException($"Unknown case '{member.Name}' for union type '{typeToConvert.Name}'.");
        }

        var value = member.Value.Deserialize(options.GetTypeInfo(match.Property.PropertyType));
        var result = new T();
        match.Property.SetValue(result, value);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        foreach (var (property, jsonName) in Cases)
        {
            var caseValue = property.GetValue(value);
            if (caseValue is null)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WritePropertyName(jsonName);
            JsonSerializer.Serialize(writer, caseValue, options.GetTypeInfo(property.PropertyType));
            writer.WriteEndObject();
            return;
        }

        throw new JsonException($"Union type '{typeof(T).Name}' has no case set (every property was null).");
    }
}

/// <summary>
/// <see cref="Models.Attachment"/>'s custom `init(from:)`: tolerates missing keys and accepts the
/// legacy <c>address</c>/<c>gateway</c> keys as aliases for <c>ipv4Address</c>/<c>ipv4Gateway</c>
/// (`Attachment.swift:66-75`, docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2). Encode always
/// writes the canonical keys.
/// </summary>
internal sealed class AttachmentConverter : JsonConverter<Attachment>
{
    public override Attachment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var network = RequireString(root, "network");
        var hostname = RequireString(root, "hostname");
        var ipv4Address = OptionalString(root, "ipv4Address") ?? OptionalString(root, "address");
        var ipv4Gateway = OptionalString(root, "ipv4Gateway") ?? OptionalString(root, "gateway");

        return new Attachment
        {
            Network = network,
            Hostname = hostname,
            Ipv4Address = ipv4Address,
            Ipv4Gateway = ipv4Gateway,
            Ipv6Address = OptionalString(root, "ipv6Address"),
            MacAddress = OptionalString(root, "macAddress"),
            Mtu = root.TryGetProperty("mtu", out var mtu) && mtu.ValueKind != JsonValueKind.Null
                ? mtu.GetUInt32()
                : null,
            Variant = OptionalString(root, "variant"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Attachment value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("network", value.Network);
        writer.WriteString("hostname", value.Hostname);
        WriteOptionalString(writer, "ipv4Address", value.Ipv4Address);
        WriteOptionalString(writer, "ipv4Gateway", value.Ipv4Gateway);
        WriteOptionalString(writer, "ipv6Address", value.Ipv6Address);
        WriteOptionalString(writer, "macAddress", value.MacAddress);
        if (value.Mtu is { } mtu)
        {
            writer.WriteNumber("mtu", mtu);
        }

        WriteOptionalString(writer, "variant", value.Variant);
        writer.WriteEndObject();
    }

    private static string RequireString(JsonElement root, string name) =>
        OptionalString(root, name)
        ?? throw new JsonException($"'{typeof(Attachment).Name}' is missing required field '{name}'.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>
/// <see cref="Models.NetworkConfiguration"/>'s custom `init(from:)`: accepts <c>id</c> as an alias
/// for <c>name</c> and <c>subnet</c> as an alias for <c>ipv4Subnet</c>
/// (`NetworkConfiguration.swift:74-105`, docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2). Encode
/// always writes the canonical keys.
/// </summary>
internal sealed class NetworkConfigurationConverter : JsonConverter<NetworkConfiguration>
{
    public override NetworkConfiguration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var name = OptionalString(root, "name") ?? OptionalString(root, "id")
            ?? throw new JsonException($"'{typeof(NetworkConfiguration).Name}' is missing required field 'name'.");
        var mode = OptionalString(root, "mode")
            ?? throw new JsonException($"'{typeof(NetworkConfiguration).Name}' is missing required field 'mode'.");
        var creationDate = root.TryGetProperty("creationDate", out var created) && created.ValueKind != JsonValueKind.Null
            ? AppleEpoch.AddSeconds(created.GetDouble())
            : DateTimeOffset.UnixEpoch;

        return new NetworkConfiguration
        {
            Name = name,
            CreationDate = creationDate,
            Mode = mode,
            Ipv4Subnet = OptionalString(root, "ipv4Subnet") ?? OptionalString(root, "subnet"),
            Ipv6Subnet = OptionalString(root, "ipv6Subnet"),
            Labels = ReadStringMap(root, "labels"),
            Plugin = OptionalString(root, "plugin"),
            Options = ReadStringMap(root, "options"),
        };
    }

    public override void Write(Utf8JsonWriter writer, NetworkConfiguration value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteNumber("creationDate", (value.CreationDate - AppleEpoch).TotalSeconds);
        writer.WriteString("mode", value.Mode);
        if (value.Ipv4Subnet is not null)
        {
            writer.WriteString("ipv4Subnet", value.Ipv4Subnet);
        }

        if (value.Ipv6Subnet is not null)
        {
            writer.WriteString("ipv6Subnet", value.Ipv6Subnet);
        }

        writer.WritePropertyName("labels");
        JsonSerializer.Serialize(writer, value.Labels, XpcJsonContext.Default.DictionaryStringString);
        if (value.Plugin is not null)
        {
            writer.WriteString("plugin", value.Plugin);
        }

        writer.WritePropertyName("options");
        JsonSerializer.Serialize(writer, value.Options, XpcJsonContext.Default.DictionaryStringString);
        writer.WriteEndObject();
    }

    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var map = new Dictionary<string, string>();
        foreach (var property in value.EnumerateObject())
        {
            map[property.Name] = property.Value.GetString() ?? "";
        }

        return map;
    }
}
