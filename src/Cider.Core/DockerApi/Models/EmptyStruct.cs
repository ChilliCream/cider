using System.Text.Json.Serialization;
using Cider.Core.DockerApi.Json;

namespace Cider.Core.DockerApi.Models;

/// <summary>Docker's Go <c>struct{}</c> map value (e.g. <c>ExposedPorts</c>, <c>Volumes</c>); serializes as <c>{}</c>.</summary>
[JsonConverter(typeof(EmptyStructConverter))]
public sealed class EmptyStruct
{
    /// <summary>Shared instance — the value carries no information.</summary>
    public static readonly EmptyStruct Instance = new();
}
