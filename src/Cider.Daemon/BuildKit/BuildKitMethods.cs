namespace Cider.Daemon.BuildKit;

/// <summary>
/// gRPC method URLs and metadata (header) key constants for the BuildKit protocols the proxy
/// speaks with Apple's builder VM, gathered in one place so route matching and header rewriting
/// never hand-roll a method string.
///
/// <see cref="Control"/> and <see cref="FileSend"/> name the two services this project vendors
/// and generates message/service code for (see ../../protos and Protos/README.md) because the
/// proxy decodes and rewrites their payloads: <c>Control/Solve</c> (exporter swap),
/// <c>Control/ListWorkers</c> (label strip), <c>Control/Session</c> (the bidi tunnel that carries
/// every other session-attached service), and <c>FileSend/DiffCopy</c> (BytesMessage framing).
///
/// Every other constant here names a method that BuildKit routes over the same <c>/grpc</c> or
/// <c>/session</c> connection but that the proxy forwards byte-for-byte without decoding its
/// payload — gateway (LLBBridge), auth, secrets, ssh-forward, upload, and the standard gRPC health
/// check. Their .proto files are deliberately not vendored; only the wire method path is needed to
/// route the raw frames.
/// </summary>
public static class BuildKitMethods
{
    /// <summary>
    /// <c>moby.buildkit.v1.Control</c> methods (vendored: control.proto). The proxy decodes and
    /// rewrites <see cref="Solve"/> requests and <see cref="ListWorkers"/> responses, and tunnels
    /// <see cref="Session"/> as an opaque bidi <c>BytesMessage</c> stream.
    /// </summary>
    public static class Control
    {
        public const string Solve = "/moby.buildkit.v1.Control/Solve";
        public const string Session = "/moby.buildkit.v1.Control/Session";
        public const string ListWorkers = "/moby.buildkit.v1.Control/ListWorkers";
    }

    /// <summary>
    /// <c>moby.buildkit.v1.frontend.LLBBridge</c> -- the gateway service a frontend build (e.g.
    /// dockerfile.v0) uses to talk back to the solver over the <c>Session</c> tunnel.
    /// gateway.proto is not vendored: every LLBBridge method is forwarded unparsed.
    /// </summary>
    public static class LlbBridge
    {
        public const string MethodPrefix = "/moby.buildkit.v1.frontend.LLBBridge/";
    }

    /// <summary>
    /// <c>moby.filesync.v1.FileSync</c> -- local files streamed from the client into the builder.
    /// Forwarded unparsed; only <see cref="FileSend"/> below is vendored/decoded.
    /// </summary>
    public static class FileSync
    {
        public const string DiffCopy = "/moby.filesync.v1.FileSync/DiffCopy";
        public const string TarStream = "/moby.filesync.v1.FileSync/TarStream";
    }

    /// <summary>
    /// <c>moby.filesync.v1.FileSend</c> (vendored: filesync.proto) -- files streamed from the
    /// builder back to the client as <c>BytesMessage</c> frames.
    /// </summary>
    public static class FileSend
    {
        public const string DiffCopy = "/moby.filesync.v1.FileSend/DiffCopy";
    }

    /// <summary>
    /// <c>moby.filesync.v1.Auth</c> -- registry credential exchange over the session tunnel.
    /// Forwarded unparsed.
    /// </summary>
    public static class Auth
    {
        public const string Credentials = "/moby.filesync.v1.Auth/Credentials";
        public const string FetchToken = "/moby.filesync.v1.Auth/FetchToken";
        public const string GetTokenAuthority = "/moby.filesync.v1.Auth/GetTokenAuthority";
        public const string VerifyTokenAuthority = "/moby.filesync.v1.Auth/VerifyTokenAuthority";
    }

    /// <summary>
    /// <c>moby.buildkit.secrets.v1.Secrets</c> -- build secret retrieval over the session tunnel.
    /// Forwarded unparsed.
    /// </summary>
    public static class Secrets
    {
        public const string GetSecret = "/moby.buildkit.secrets.v1.Secrets/GetSecret";
    }

    /// <summary>
    /// <c>moby.sshforward.v1.SSH</c> -- SSH agent forwarding over the session tunnel. Forwarded
    /// unparsed.
    /// </summary>
    public static class Ssh
    {
        public const string CheckAgent = "/moby.sshforward.v1.SSH/CheckAgent";
        public const string ForwardAgent = "/moby.sshforward.v1.SSH/ForwardAgent";
    }

    /// <summary>
    /// <c>moby.upload.v1.Upload</c> -- HTTP(S) upload source pulled through the session tunnel.
    /// Forwarded unparsed.
    /// </summary>
    public static class Upload
    {
        public const string Pull = "/moby.upload.v1.Upload/Pull";
    }

    /// <summary>
    /// The standard gRPC health check, answered directly by the <c>Grpc.HealthCheck</c> package
    /// (<c>Grpc.Health.V1</c>) rather than a vendored proto.
    /// </summary>
    public static class Health
    {
        public const string Check = "/grpc.health.v1.Health/Check";
    }

    /// <summary>
    /// Metadata (header) and attribute keys BuildKit attaches around <see cref="Control.Solve"/>
    /// and its attached session, needed to correlate a build id with its session-attachable
    /// exporter and to demultiplex the session tunnel's own sub-services.
    /// </summary>
    public static class MetadataKeys
    {
        /// <summary>gRPC metadata key on <see cref="Control.Solve"/> (client/buildid/metadata.go).</summary>
        public const string ControlApiBuildId = "buildkit-controlapi-buildid";

        /// <summary>Session metadata key correlating a Solve's exporter with its attached FileSend session (session/filesync/filesync.go).</summary>
        public const string AttachableExporterId = "buildkit-attachable-exporter-id";

        /// <summary>Prefix for exporter attribute entries carried as session metadata (session/filesync/filesync.go).</summary>
        public const string ExporterMetadataPrefix = "exporter-md-";

        /// <summary>
        /// gRPC metadata keys on the <c>Session</c>/<c>/session</c> upgrade request BuildKit uses to
        /// identify and demultiplex a client session (session/session.go: headerSessionID,
        /// headerSessionSharedKey, headerSessionMethod -- HTTP header names lowercased, as gRPC
        /// metadata keys are).
        /// </summary>
        public const string SessionUuid = "x-docker-expose-session-uuid";
        public const string SessionSharedKey = "x-docker-expose-session-sharedkey";
        public const string SessionGrpcMethod = "x-docker-expose-session-grpc-method";

        /// <summary>
        /// Prefix for <c>SolveRequest.FrontendAttrs</c> keys mapping a named local mount (e.g.
        /// dockerfile, context, a build context named in FROM) to the id of the session serving it
        /// (frontend/dockerui/config.go: localSessionIDPrefix). Not a gRPC metadata key.
        /// </summary>
        public const string LocalSessionIdPrefix = "local-sessionid:";
    }
}
