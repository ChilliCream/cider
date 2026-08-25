using System.Diagnostics;
using System.Net;
using Cider.Core.Runtime;
using Grpc.Core;
using Grpc.Net.Client;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// One live dial of <c>buildctl dial-stdio</c> into the Apple builder VM, wrapped in an HTTP/2 gRPC
/// channel (<see cref="Tunnel.StreamHttp2Client"/>) — everything a caller needs to talk to buildkitd
/// and everything <see cref="BuilderConnection"/> needs to notice it has died. Built only by
/// <see cref="BuilderConnection"/>; disposing it kills the CLI child (the builder VM itself keeps
/// running).
/// </summary>
public sealed class BuilderLink : IAsyncDisposable
{
    private readonly IContainerProcess _process;
    private readonly SocketsHttpHandler _handler;
    private int _disposed;

    internal BuilderLink(
        GrpcChannel channel,
        HttpMessageInvoker invoker,
        CallInvoker callInvoker,
        ForwardTarget target,
        BuilderLinkTracker tracker,
        IContainerProcess process,
        SocketsHttpHandler handler)
    {
        Channel = channel;
        Invoker = invoker;
        CallInvoker = callInvoker;
        Target = target;
        Tracker = tracker;
        Exited = process.Exited;
        _process = process;
        _handler = handler;
    }

    /// <summary>The gRPC channel dialed over the exec pipe.</summary>
    public GrpcChannel Channel { get; }

    /// <summary>
    /// The raw invoker backing <see cref="Channel"/>. <see cref="Target"/>'s
    /// <see cref="ForwardTarget.Invoker"/> wraps this one in <see cref="ActivityTrackingHttpInvoker"/>
    /// rather than exposing it directly, so <see cref="GrpcForwarder"/> always calls through the
    /// tracked wrapper; this property remains the plain invoker for anything else that needs it.
    /// </summary>
    public HttpMessageInvoker Invoker { get; }

    /// <summary>
    /// A <see cref="Grpc.Core.CallInvoker"/> over the same channel whose calls bump
    /// <see cref="Tracker"/>'s progress clock as they send and receive messages — for callers that use
    /// generated client stubs directly (the liveness probe here, a typed session bridge elsewhere)
    /// rather than <see cref="GrpcForwarder"/>'s raw byte forwarding.
    /// </summary>
    public CallInvoker CallInvoker { get; }

    /// <summary>
    /// <see cref="Invoker"/> wrapped in <see cref="ActivityTrackingHttpInvoker"/> (so forwarded calls
    /// bump <see cref="Tracker"/> just like <see cref="CallInvoker"/> ones do) plus <c>"buildkit"</c>
    /// plus a <see cref="TokenBucketPacer"/> plus <see cref="ForwardTarget.OnFailure"/> wired to
    /// invalidate this link — ready to hand to <see cref="GrpcForwarder.ForwardAsync"/> or
    /// <see cref="GrpcForwarder.MapGrpcForwarder"/>.
    /// </summary>
    public ForwardTarget Target { get; }

    /// <summary>Completes with the dial process's exit code; a completed task means this link is dead.</summary>
    public Task Exited { get; }

    /// <summary>Tracks open calls and last-progress time for <see cref="BuilderConnection"/>'s stall watchdog.</summary>
    internal BuilderLinkTracker Tracker { get; }

    /// <summary>Kills the CLI child (<c>container exec ... buildctl dial-stdio</c>) and tears down the
    /// channel/handler. The builder VM itself is untouched — matching classic Docker/buildx behaviour,
    /// where stopping a client connection never stops the daemon it talked to.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Channel.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        try
        {
            Invoker.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _handler.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        await _process.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The activity clock a <see cref="BuilderLink"/> carries: how many calls are currently open on it
/// and when one of them last made progress (a byte written or a message read). A stall is "at least
/// one call open, but no progress for the configured threshold" — a link nothing is using is never
/// considered stalled no matter how old <see cref="LastProgress"/> is.
/// </summary>
internal sealed class BuilderLinkTracker
{
    private long _lastProgressTicks;
    private int _activeCalls;

    public BuilderLinkTracker() => RecordProgress();

    /// <summary>The last time this link moved a byte, in <see cref="Stopwatch.GetTimestamp"/> units.</summary>
    public long LastProgress => Interlocked.Read(ref _lastProgressTicks);

    /// <summary>How many calls are currently open on this link.</summary>
    public int ActiveCalls => Volatile.Read(ref _activeCalls);

    /// <summary>Bumps the progress clock to now. Called for every forwarded/bridged write and read.</summary>
    public void RecordProgress() => Interlocked.Exchange(ref _lastProgressTicks, Stopwatch.GetTimestamp());

    /// <summary>
    /// Marks one call as open (and immediately makes progress, so a call that never gets to write or
    /// read anything before it is cancelled does not read as instantly stalled). Dispose the result
    /// when the call ends.
    /// </summary>
    public IDisposable BeginCall()
    {
        Interlocked.Increment(ref _activeCalls);
        RecordProgress();
        return new CallScope(this);
    }

    /// <summary><see langword="true"/> when a call is open and nothing has moved for <paramref name="threshold"/>.</summary>
    public bool IsStalled(TimeSpan threshold) =>
        ActiveCalls >= 1 && Stopwatch.GetElapsedTime(LastProgress) > threshold;

    private sealed class CallScope(BuilderLinkTracker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._activeCalls);
            }
        }
    }
}

/// <summary>
/// Wraps a channel's own <see cref="CallInvoker"/> so that every call it hands out reports into a
/// <see cref="BuilderLinkTracker"/>: open for the call's lifetime (unary: until the response task
/// completes and it is disposed; streaming: until disposed), progress recorded on every message sent
/// or received. This is what makes <see cref="BuilderLink.CallInvoker"/> — not raw byte forwarding,
/// which reports progress through <see cref="TokenBucketPacer"/> instead — visible to the stall
/// watchdog.
/// </summary>
internal sealed class ActivityTrackingCallInvoker(CallInvoker inner, BuilderLinkTracker tracker) : CallInvoker
{
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        using var scope = tracker.BeginCall();
        var response = inner.BlockingUnaryCall(method, host, options, request);
        tracker.RecordProgress();
        return response;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var call = inner.AsyncUnaryCall(method, host, options, request);
        var scope = tracker.BeginCall();
        return new AsyncUnaryCall<TResponse>(
            TrackAsync(call.ResponseAsync, scope),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => Finish(scope, call));
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        var call = inner.AsyncClientStreamingCall(method, host, options);
        var scope = tracker.BeginCall();
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            new TrackingStreamWriter<TRequest>(call.RequestStream, tracker),
            TrackAsync(call.ResponseAsync, scope),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => Finish(scope, call));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var call = inner.AsyncServerStreamingCall(method, host, options, request);
        var scope = tracker.BeginCall();
        return new AsyncServerStreamingCall<TResponse>(
            new TrackingStreamReader<TResponse>(call.ResponseStream, tracker),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => Finish(scope, call));
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        var call = inner.AsyncDuplexStreamingCall(method, host, options);
        var scope = tracker.BeginCall();
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            new TrackingStreamWriter<TRequest>(call.RequestStream, tracker),
            new TrackingStreamReader<TResponse>(call.ResponseStream, tracker),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () => Finish(scope, call));
    }

    private static void Finish(IDisposable scope, IDisposable call)
    {
        scope.Dispose();
        call.Dispose();
    }

    private static async Task<TResponse> TrackAsync<TResponse>(Task<TResponse> response, IDisposable scope)
    {
        try
        {
            return await response.ConfigureAwait(false);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private sealed class TrackingStreamReader<T>(IAsyncStreamReader<T> inner, BuilderLinkTracker tracker) : IAsyncStreamReader<T>
    {
        public T Current => inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var hasNext = await inner.MoveNext(cancellationToken).ConfigureAwait(false);
            if (hasNext)
            {
                tracker.RecordProgress();
            }

            return hasNext;
        }
    }

    private sealed class TrackingStreamWriter<T>(IClientStreamWriter<T> inner, BuilderLinkTracker tracker) : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions
        {
            get => inner.WriteOptions;
            set => inner.WriteOptions = value;
        }

        public async Task WriteAsync(T message)
        {
            await inner.WriteAsync(message).ConfigureAwait(false);
            tracker.RecordProgress();
        }

        public async Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            tracker.RecordProgress();
        }

        public Task CompleteAsync() => inner.CompleteAsync();
    }
}

/// <summary>
/// Wraps a raw <see cref="HttpMessageInvoker"/> (a <see cref="BuilderLink"/>'s <see cref="BuilderLink.Invoker"/>,
/// straight off <see cref="Tunnel.StreamHttp2Client.Create"/>) so every call reports into a
/// <see cref="BuilderLinkTracker"/> the same way <see cref="ActivityTrackingCallInvoker"/> does for
/// <see cref="BuilderLink.CallInvoker"/> -- except here the caller is <see cref="GrpcForwarder"/>'s raw
/// byte forwarding rather than a generated client stub, so there are no messages to hook, only a call
/// scope open for the request's lifetime and a response body to watch. This is what
/// <see cref="BuilderLink.Target"/>'s <see cref="ForwardTarget.Invoker"/> is set to (see
/// <see cref="BuilderConnection.EstablishLinkAsync"/>) so a stalled forwarded call -- upload or
/// download -- is visible to the stall watchdog exactly like a stalled <see cref="BuilderLink.CallInvoker"/>
/// call already is.
/// </summary>
internal static class ActivityTrackingHttpInvoker
{
    /// <summary>
    /// Wraps <paramref name="inner"/> in a new <see cref="HttpMessageInvoker"/> that tracks calls
    /// against <paramref name="tracker"/>. The returned invoker owns none of <paramref name="inner"/>'s
    /// resources -- disposing it never disposes <paramref name="inner"/> or the handler backing it.
    /// </summary>
    public static HttpMessageInvoker Wrap(HttpMessageInvoker inner, BuilderLinkTracker tracker) =>
        new(new TrackingHandler(inner, tracker), disposeHandler: false);

    private sealed class TrackingHandler(HttpMessageInvoker inner, BuilderLinkTracker tracker) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scope = tracker.BeginCall();
            try
            {
                var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.Content = new TrackingContent(response.Content, tracker, scope);
                return response;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Stands in for the upstream response's own <see cref="HttpContent"/>, so that whatever reads it
    /// -- <see cref="GrpcForwarder"/>'s <c>CopyResponseBodyAsync</c>, in practice -- reads through
    /// <see cref="TrackingReadStream"/> instead, and so that the call scope opened in
    /// <see cref="TrackingHandler"/> is disposed exactly once the response (this content) is disposed,
    /// matching what happens to the request/response pair's own <c>using</c> block in
    /// <see cref="GrpcForwarder.ForwardAsync"/>.
    /// </summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly BuilderLinkTracker _tracker;
        private readonly IDisposable _scope;
        private int _scopeDisposed;

        public TrackingContent(HttpContent inner, BuilderLinkTracker tracker, IDisposable scope)
        {
            _inner = inner;
            _tracker = tracker;
            _scope = scope;

            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            var stream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new TrackingReadStream(stream, _tracker);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _inner.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                if (Interlocked.Exchange(ref _scopeDisposed, 1) == 0)
                {
                    _scope.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The upstream response body stream, wrapped so every successful read (a positive byte count --
    /// EOF and zero-length reads make no progress) bumps <see cref="BuilderLinkTracker.RecordProgress"/>.
    /// Read-only: <see cref="GrpcForwarder"/> only ever reads a response body.
    /// </summary>
    private sealed class TrackingReadStream(Stream inner, BuilderLinkTracker tracker) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                tracker.RecordProgress();
            }

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                tracker.RecordProgress();
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                tracker.RecordProgress();
            }

            return read;
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
