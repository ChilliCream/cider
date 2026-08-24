using Cider.Core.DockerApi.Models;
using Cider.Core.State;

namespace Cider.Core.Events;

/// <summary>Factories for the <see cref="EventMessage"/> shapes Docker emits on <c>/events</c>.</summary>
public static class DockerEvents
{
    /// <summary>A <c>container</c> event; attributes carry image, name, the container's labels and any extras.</summary>
    public static EventMessage Container(
        string action,
        ContainerRecord record,
        IReadOnlyDictionary<string, string>? extraAttributes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);
        ArgumentNullException.ThrowIfNull(record);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in record.Request.Labels)
        {
            attributes[key] = value;
        }

        attributes["image"] = record.ImageRef.Length > 0 ? record.ImageRef : record.Request.Image;
        attributes["name"] = record.Name;

        if (extraAttributes is not null)
        {
            foreach (var (key, value) in extraAttributes)
            {
                attributes[key] = value;
            }
        }

        return new EventMessage
        {
            Type = "container",
            Action = action,
            Actor = new EventActor { ID = record.Id, Attributes = attributes },
        };
    }

    /// <summary>An <c>image</c> event; Docker's actor id is the reference when there is one.</summary>
    public static EventMessage Image(string action, string imageId, string? reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        var name = string.IsNullOrEmpty(reference) ? imageId : reference;
        return new EventMessage
        {
            Type = "image",
            Action = action,
            Actor = new EventActor
            {
                ID = name ?? "",
                Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = name ?? "" },
            },
        };
    }

    /// <summary>A <c>network</c> event.</summary>
    public static EventMessage Network(string action, string networkId, string name, string? containerId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["type"] = "bridge",
        };

        if (!string.IsNullOrEmpty(containerId))
        {
            attributes["container"] = containerId;
        }

        return new EventMessage
        {
            Type = "network",
            Action = action,
            Actor = new EventActor { ID = networkId, Attributes = attributes },
        };
    }

    /// <summary>A <c>volume</c> event.</summary>
    public static EventMessage Volume(string action, string name, string? containerId = null, string? destination = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["driver"] = "local",
        };

        if (!string.IsNullOrEmpty(containerId))
        {
            attributes["container"] = containerId;
        }

        if (!string.IsNullOrEmpty(destination))
        {
            attributes["destination"] = destination;
        }

        return new EventMessage
        {
            Type = "volume",
            Action = action,
            Actor = new EventActor { ID = name, Attributes = attributes },
        };
    }
}
