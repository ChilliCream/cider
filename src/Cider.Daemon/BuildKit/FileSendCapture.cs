using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using FilesyncBytesMessage = Moby.Filesync.V1.BytesMessage;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Terminates one <c>moby.filesync.v1.FileSend/DiffCopy</c> call server-side instead of forwarding
/// it to the CLI — the moby exporter's own output channel (see cider-ger.9's problem statement:
/// buildx registers no FileSend target on its own session for that exporter, so the daemon must
/// answer this one itself). Plugs into <see cref="GrpcForwarder.ForwardAsync"/> exactly like a real
/// upstream would: wrapped as an <see cref="HttpMessageInvoker"/> and handed to it as a
/// <see cref="ForwardTarget.Invoker"/>, so header/body/trailer plumbing is reused rather than
/// duplicated.
/// <para>
/// The request body carries raw HTTP/2 gRPC frames (1-byte compression flag, 4-byte big-endian
/// length, then a serialized <see cref="FilesyncBytesMessage"/>) exactly as buildkitd writes them for
/// this streaming call; <see cref="GrpcFrameSink"/> decodes them as they arrive and appends each
/// message's <c>Data</c> straight to the tar file — never buffering a whole chunk, let alone the
/// whole export, in memory. On EOF (buildkitd's <c>CloseSend</c>) this answers <c>grpc-status: 0</c>
/// trailers-only, which is all <c>filesync.CopyToCaller</c> checks for success (OK or AlreadyExists;
/// export.go:271-278) — no response <see cref="FilesyncBytesMessage"/> is ever needed.
/// </para>
/// </summary>
internal sealed class FileSendCapture : HttpMessageHandler
{
    private const int FileBufferBytes = 1024 * 1024;

    private readonly string _tarPath;
    private readonly SessionBridgeHandle _handle;
    private readonly int _exporterId;
    private readonly ILogger _logger;

    public FileSendCapture(string tarPath, SessionBridgeHandle handle, int exporterId, ILogger logger)
    {
        _tarPath = tarPath ?? throw new ArgumentNullException(nameof(tarPath));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _exporterId = exporterId;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = ReadExporterMetadata(request.Headers);

        try
        {
            var directory = Path.GetDirectoryName(_tarPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var file = new FileStream(
                _tarPath, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferBytes, useAsync: true))
            {
                var sink = new GrpcFrameSink(file);

                if (request.Content is not null)
                {
                    await request.Content.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _handle.FailExport(_exporterId, ex);
            throw;
        }

        var result = new ExportResult { TarPath = _tarPath, Metadata = metadata };
        _handle.CompleteExport(_exporterId, result);
        _logger.LogDebug(
            "captured FileSend/DiffCopy for session {SessionId} exporter {ExporterId} -> {TarPath}",
            _handle.Cli.Id, _exporterId, _tarPath);

        return BuildOkResponse();
    }

    private static Dictionary<string, string> ReadExporterMetadata(HttpRequestHeaders headers)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = BuildKitMethods.MetadataKeys.ExporterMetadataPrefix;

        foreach (var header in headers)
        {
            if (!header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value.FirstOrDefault();
            if (value is not null)
            {
                metadata[header.Key[prefix.Length..]] = value;
            }
        }

        return metadata;
    }

    /// <summary>
    /// A gRPC trailers-only <c>grpc-status: 0</c> response, built the same shape
    /// <see cref="GrpcForwarder"/> expects an upstream <see cref="HttpResponseMessage"/> to have: the
    /// status lives on <see cref="HttpResponseMessage.TrailingHeaders"/>, which
    /// <c>GrpcForwarder.CopyTrailers</c> copies onto the client-facing response's own trailers.
    /// </summary>
    private static HttpResponseMessage BuildOkResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent([]),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
        response.TrailingHeaders.Add("grpc-status", "0");
        return response;
    }

    /// <summary>
    /// A write-only <see cref="Stream"/> that decodes the standard gRPC length-prefixed frame format
    /// (1-byte compression flag, 4-byte big-endian payload length, payload) off whatever chunk sizes
    /// arrive, parses each frame's payload as a <see cref="FilesyncBytesMessage"/>, and appends its
    /// <c>Data</c> to <paramref name="destination"/> — buffering only the current frame's header and
    /// payload, never a whole message across writes.
    /// </summary>
    private sealed class GrpcFrameSink(Stream destination) : Stream
    {
        private readonly byte[] _header = new byte[5];
        private int _headerFilled;
        private byte[]? _payload;
        private int _payloadFilled;
        private int _payloadLength = -1;

        public override bool CanRead => false;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException("cider: a gRPC frame sink has no length");

        public override long Position
        {
            get => throw new NotSupportedException("cider: a gRPC frame sink has no position");
            set => throw new NotSupportedException("cider: a gRPC frame sink has no position");
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                if (_payloadLength < 0)
                {
                    var need = 5 - _headerFilled;
                    var take = Math.Min(need, remaining.Length);
                    remaining.Span[..take].CopyTo(_header.AsSpan(_headerFilled));
                    _headerFilled += take;
                    remaining = remaining[take..];

                    if (_headerFilled < 5)
                    {
                        break;
                    }

                    _payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(1, 4)));
                    _payload = _payloadLength == 0 ? [] : new byte[_payloadLength];
                    _payloadFilled = 0;

                    if (_payloadLength == 0)
                    {
                        await EmitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var payloadRemaining = _payloadLength - _payloadFilled;
                var chunk = Math.Min(payloadRemaining, remaining.Length);
                remaining.Span[..chunk].CopyTo(_payload!.AsSpan(_payloadFilled));
                _payloadFilled += chunk;
                remaining = remaining[chunk..];

                if (_payloadFilled == _payloadLength)
                {
                    await EmitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task EmitAsync(CancellationToken cancellationToken)
        {
            var message = FilesyncBytesMessage.Parser.ParseFrom(_payload);
            if (!message.Data.IsEmpty)
            {
                await destination.WriteAsync(message.Data.Memory, cancellationToken).ConfigureAwait(false);
            }

            _headerFilled = 0;
            _payloadLength = -1;
            _payload = null;
            _payloadFilled = 0;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("cider: a gRPC frame sink is write-only");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("cider: a gRPC frame sink cannot seek");

        public override void SetLength(long value) =>
            throw new NotSupportedException("cider: a gRPC frame sink cannot be resized");
    }
}
