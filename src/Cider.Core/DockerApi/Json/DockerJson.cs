// Null / empty-collection policy for the Docker wire format
// -----------------------------------------------------------------------------
// Docker clients send `null` for empty Go slices and maps all the time
// ("Binds": null, "CapAdd": null, "Ulimits": null ...). We deliberately do NOT
// install a global converter that turns JSON `null` into an empty collection,
// because for some Docker fields `null` and `[]` mean different things --
// most importantly `Entrypoint`: `null` means "inherit the image entrypoint"
// while `[]` means "clear the image entrypoint". A blanket converter would
// destroy that distinction and break the Entrypoint/Cmd merge rules.
//
// The rule therefore is:
//   * fields where Docker distinguishes null from empty are declared nullable
//     (`List<string>? Binds`) and callers handle `null`;
//   * fields where Docker never means anything by null are declared
//     non-nullable and initialized to an empty collection, so an *omitted*
//     field deserializes to empty rather than null;
//   * for the handful of non-nullable collections that real clients are known
//     to send as explicit `null` (Labels, ExposedPorts, Volumes, PortBindings)
//     the property setter coalesces `null` to an empty collection.
// Serialization always writes what the object holds (DefaultIgnoreCondition =
// Never), which is what Docker does for these types.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cider.Core.DockerApi.Json;

/// <summary>System.Text.Json configuration and helpers for the Docker Engine API wire format.</summary>
/// <remarks>
/// This is the only door onto the wire format: every setting lives on
/// <c>DockerJsonContext</c>'s <c>[JsonSourceGenerationOptions]</c> and every method below resolves
/// its contract from that source-generated context, so no call site has to know the context exists
/// and nothing here needs runtime reflection.
/// </remarks>
public static class DockerJson
{
    /// <summary>The one and only serializer configuration used on the Docker wire.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // Everything except the encoder is declared on DockerJsonContext; copying its options
        // carries those settings *and* the context itself as the type-info resolver, so resolution
        // stays source-generated. Encoder has no [JsonSourceGenerationOptions] counterpart, so it
        // is the one knob that has to be set here: Go's encoding/json emits raw UTF-8, while the
        // default STJ encoder would escape non-ASCII and characters like '+' which shows up in
        // log/progress payloads.
        var options = new JsonSerializerOptions(DockerJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// The source-generated contract for <typeparamref name="T"/>, for the few callers that have to
    /// hand a <see cref="JsonTypeInfo{T}"/> to someone else (ASP.NET's <c>Results.Json</c>).
    /// Throws <see cref="NotSupportedException"/> when <typeparamref name="T"/> is not one of the
    /// types <c>DockerJsonContext</c> declares -- a build-time omission, not a runtime input.
    /// </summary>
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>Serializes <paramref name="value"/> with <see cref="Options"/>.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

    /// <summary>Serializes <paramref name="value"/> straight to UTF-8 bytes.</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    /// <summary>Deserializes <paramref name="json"/>; returns <c>default</c> for a JSON <c>null</c>.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    /// <summary>Deserializes UTF-8 <paramref name="utf8Json"/>; returns <c>default</c> for a JSON <c>null</c>.</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize(utf8Json, TypeInfo<T>());

    /// <summary>Deserializes a UTF-8 JSON document from <paramref name="stream"/>.</summary>
    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct = default) =>
        JsonSerializer.DeserializeAsync(stream, TypeInfo<T>(), ct);

    /// <summary>Writes <paramref name="value"/> as UTF-8 JSON to <paramref name="stream"/>.</summary>
    public static Task SerializeAsync<T>(Stream stream, T value, CancellationToken ct = default) =>
        JsonSerializer.SerializeAsync(stream, value, TypeInfo<T>(), ct);
}
