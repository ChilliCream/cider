using System.Threading.Channels;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Logs;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    /// <summary>
    /// <c>POST /containers/{id}/attach</c>. Works on a container that has not started yet: the
    /// attachment is bound to the process when <see cref="StartAsync"/> runs.
    /// </summary>
    public async Task<ContainerAttachment> AttachAsync(string idOrName, AttachOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        var channel = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var attachment = new ContainerAttachment(
            record.Request.Tty,
            options,
            channel,
            () => handle.Process,
            Detach);

        if (options.Logs)
        {
            var replay = new LogReadOptions
            {
                Follow = false,
                Stdout = options.Stdout,
                Stderr = options.Stderr,
            };

            await foreach (var entry in _logs.ReadAsync(record.Id, replay, ct))
            {
                channel.Writer.TryWrite(new OutputChunk(entry.Stream, entry.Data));
            }
        }

        if (!options.Stream)
        {
            attachment.SignalExit();
            return attachment;
        }

        var exited = false;
        var running = false;
        lock (handle.AttachGate)
        {
            if (handle.Process is null && !record.State.Running &&
                !string.Equals(record.State.Status, "created", StringComparison.Ordinal))
            {
                exited = true;
            }
            else
            {
                handle.Attachments.Add(attachment);
                attachment.Tty = handle.Process?.HasTty ?? record.Request.Tty;

                // Taken under the same gate that BindAttachmentsAsync holds, so an attachment made
                // while a start is in flight is bound exactly once: either it sees the process
                // here, or it is in the list the start is about to walk.
                running = handle.Process is not null;
            }
        }

        if (exited)
        {
            attachment.SignalExit();
            return attachment;
        }

        if (running)
        {
            await attachment.BindStdinAsync();
        }

        Publish(record, "attach");
        return attachment;

        void Detach(ContainerAttachment target)
        {
            lock (handle.AttachGate)
            {
                handle.Attachments.Remove(target);
            }
        }
    }

    private void Broadcast(ContainerHandle handle, StdStream stream, ReadOnlyMemory<byte> data)
    {
        ContainerAttachment[] attachments;
        lock (handle.AttachGate)
        {
            if (handle.Attachments.Count == 0)
            {
                return;
            }

            attachments = [.. handle.Attachments];
        }

        foreach (var attachment in attachments)
        {
            if (stream == StdStream.Stdout && !attachment.Options.Stdout)
            {
                continue;
            }

            if (stream == StdStream.Stderr && !attachment.Options.Stderr)
            {
                continue;
            }

            attachment.Writer.TryWrite(new OutputChunk(stream, data));
        }
    }

    /// <summary>
    /// Binds every attachment made before the start to the new process: it learns whether the
    /// session is a pty, and the stdin the client already wrote (and any half-close it already
    /// asked for) is replayed into the process now that one exists.
    /// </summary>
    private static async Task BindAttachmentsAsync(ContainerHandle handle)
    {
        var tty = handle.Process?.HasTty ?? false;
        ContainerAttachment[] attachments;
        lock (handle.AttachGate)
        {
            foreach (var attachment in handle.Attachments)
            {
                attachment.Tty = tty;
            }

            attachments = [.. handle.Attachments];
        }

        foreach (var attachment in attachments)
        {
            await attachment.BindStdinAsync();
        }
    }

    private static void CompleteAttachments(ContainerHandle handle)
    {
        ContainerAttachment[] attachments;
        lock (handle.AttachGate)
        {
            attachments = [.. handle.Attachments];
            handle.Attachments.Clear();
        }

        foreach (var attachment in attachments)
        {
            attachment.SignalExit();
        }
    }
}
