using System.Text.Json;
using System.Text.Json.Serialization;
using Cider.Core.DockerApi.Models;

namespace Cider.Core.DockerApi.Json;

/// <summary>Reads any JSON value into an <see cref="EmptyStruct"/> and always writes <c>{}</c>.</summary>
public sealed class EmptyStructConverter : JsonConverter<EmptyStruct>
{
    public override EmptyStruct Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return EmptyStruct.Instance;
    }

    public override void Write(Utf8JsonWriter writer, EmptyStruct value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}
