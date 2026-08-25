// XpcProbe: talk to Apple's container-apiserver (container 1.3.0) over XPC from .NET 10.
//
// usage: dotnet run -c Release -- [all|ping|list|create|experiments|cli]
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XpcProbe;

string mode = args.Length > 0 ? args[0] : "all";
bool Want(string m) => mode == "all" || mode == m;

const string ContainerCli = "/usr/local/bin/container";
const string ProbeName = "xpcprobe-1";
const string MinimalName = "xpcprobe-min";
const string CliName = "xpcprobe-cli";

Console.WriteLine($"XpcProbe on .NET {Environment.Version}, pid {Environment.ProcessId}, {RuntimeInfo()}");
Console.WriteLine($"block layout: {ObjCBlock.DescribeLayout()}");

using var client = new ApiServerClient();
Console.WriteLine($"connection created + activated; remote pid before first message = {client.RemotePid}");

byte[]? kernelJson = null;
int exitCode = 0;

try
{
    // ------------------------------------------------------------------ 1. ping
    if (Want("ping"))
    {
        Header("ping");
        nint reply = client.Send(ApiServerClient.NewMessage("ping"));
        Console.WriteLine("xpc_copy_description(reply):");
        Console.WriteLine(Xpc.Describe(reply));
        Console.WriteLine($"xpc_dictionary_get_count = {Xpc.xpc_dictionary_get_count(reply)}");
        Console.WriteLine("keys via xpc_dictionary_apply: " +
            string.Join(", ", ApiServerClient.Keys(reply).Select(k => $"{k.key}:{k.type}")));
        Console.WriteLine($"apiServerVersion = \"{Xpc.GetString(reply, "apiServerVersion")}\"");
        Console.WriteLine($"apiServerCommit  = \"{Xpc.GetString(reply, "apiServerCommit")}\"");
        Console.WriteLine($"appRoot          = \"{Xpc.GetString(reply, "appRoot")}\"");
        Xpc.xpc_release(reply);
        Console.WriteLine($"remote pid after first message = {client.RemotePid}");

        Measure("ping (warm connection)", warmup: 20, n: 100, () =>
        {
            Xpc.xpc_release(client.Send(ApiServerClient.NewMessage("ping")));
        });

        Measure("connect + ping (fresh connection each time)", warmup: 2, n: 10, () =>
        {
            using var c = new ApiServerClient();
            Xpc.xpc_release(c.Send(ApiServerClient.NewMessage("ping")));
        });

        // Prove the resume fallback path also works.
        using (var c = new ApiServerClient(useActivate: false))
        {
            nint r = c.Send(ApiServerClient.NewMessage("ping"));
            Console.WriteLine($"xpc_connection_resume path OK: apiServerVersion = \"{Xpc.GetString(r, "apiServerVersion")}\"");
            Xpc.xpc_release(r);
        }
    }

    // ------------------------------------------------------------------ 2. containerList
    if (Want("list"))
    {
        Header("containerList");
        // Without listFilters (server defaults to ContainerListFilters.all)
        nint reply = client.Send(ApiServerClient.NewMessage("containerList"));
        Console.WriteLine("reply keys: " + string.Join(", ", ApiServerClient.Keys(reply).Select(k => $"{k.key}:{k.type}")));
        byte[] data = Xpc.GetData(reply, "containers") ?? [];
        Xpc.xpc_release(reply);
        PrintContainers("no listFilters", data);

        // With listFilters = ContainerListFilters.all encoded by Swift JSONEncoder: {"ids":[],"labels":{}} (status nil -> omitted)
        nint m = ApiServerClient.NewMessage("containerList");
        Xpc.SetJson(m, "listFilters", """{"ids":[],"labels":{}}""");
        reply = client.Send(m);
        data = Xpc.GetData(reply, "containers") ?? [];
        Xpc.xpc_release(reply);
        PrintContainers("listFilters={\"ids\":[],\"labels\":{}}", data);

        Measure("containerList (no filters)", warmup: 10, n: 100, () =>
        {
            nint r = client.Send(ApiServerClient.NewMessage("containerList"));
            _ = Xpc.GetData(r, "containers");
            Xpc.xpc_release(r);
        });
    }

    // ------------------------------------------------------------------ 3. getDefaultKernel
    if (Want("create") || Want("experiments"))
    {
        Header("getDefaultKernel");
        nint m = ApiServerClient.NewMessage("getDefaultKernel");
        Xpc.SetJson(m, "systemPlatform", """{"os":"linux","architecture":"arm64"}""");
        nint reply = client.Send(m);
        kernelJson = Xpc.GetData(reply, "kernel") ?? throw new Exception("no kernel data in reply");
        Xpc.xpc_release(reply);
        Console.WriteLine($"kernel JSON ({kernelJson.Length} bytes): {Encoding.UTF8.GetString(kernelJson)}");
    }

    // ------------------------------------------------------------------ 4. containerCreate / containerDelete cycles
    if (Want("create"))
    {
        Header("containerCreate + containerDelete x10 (full config copied from `container inspect xpcprobe-ref`)");
        var createMs = new List<double>();
        var deleteMs = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Create(client, ProbeName, BuildConfig(ProbeName, minimal: false), kernelJson!, options: """{"autoRemove":false}""");
            createMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

            if (i == 0)
            {
                string ls = Cli("ls", "-a");
                Console.WriteLine($"  verify after create: `container ls -a` {(ls.Contains(ProbeName) ? "CONTAINS" : "DOES NOT CONTAIN")} {ProbeName}");
                Console.WriteLine($"  verify via XPC containerList: ids = [{string.Join(", ", ListIds(client))}]");
            }

            t0 = Stopwatch.GetTimestamp();
            Delete(client, ProbeName, force: true);
            deleteMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

            if (i == 0)
            {
                string ls = Cli("ls", "-a");
                Console.WriteLine($"  verify after delete: `container ls -a` {(ls.Contains(ProbeName) ? "STILL CONTAINS" : "no longer contains")} {ProbeName}");
            }
        }
        Report("containerCreate (XPC)", createMs);
        Report("containerDelete (XPC, forceDelete=true)", deleteMs);
        Report("create+delete cycle (XPC)", createMs.Zip(deleteMs, (a, b) => a + b).ToList());
    }

    // ------------------------------------------------------------------ 5. validation experiments
    if (Want("experiments"))
    {
        Header("server-side validation experiments");

        Try("minimal config {id,image,initProcess} + kernel, no containerOptions", () =>
        {
            string cfg = BuildConfig(MinimalName, minimal: true);
            Console.WriteLine("  sending containerConfig = " + cfg);
            Create(client, MinimalName, cfg, kernelJson!, options: null);
            Console.WriteLine("  accepted; `container ls -a` " + (Cli("ls", "-a").Contains(MinimalName) ? "shows it" : "does NOT show it"));
            string inspect = Cli("inspect", MinimalName);
            var node = JsonNode.Parse(inspect)![0]!["configuration"]!;
            Console.WriteLine($"  server-filled defaults: networks={node["networks"]!.ToJsonString()}, resources={node["resources"]!.ToJsonString()}, platform={node["platform"]!.ToJsonString()}, creationDate={node["creationDate"]}");
            Delete(client, MinimalName, force: true);
        });

        Try("missing kernel key", () => Create(client, "xpcprobe-x1", BuildConfig("xpcprobe-x1", minimal: true), kernel: null, options: null));

        Try("creationDate as ISO-8601 string (as printed by `container inspect`)", () =>
        {
            var node = JsonNode.Parse(BuildConfig("xpcprobe-x2", minimal: false))!;
            node["creationDate"] = "2026-08-25T09:54:03Z";
            Create(client, "xpcprobe-x2", node.ToJsonString(), kernelJson!, options: null);
            Delete(client, "xpcprobe-x2", force: true);
        });

        Try("invalid container id \"xpc probe!\"", () => Create(client, "xpc probe!", BuildConfig("xpc probe!", minimal: true), kernelJson!, options: null));

        Try("memory below 200 MiB", () =>
        {
            var node = JsonNode.Parse(BuildConfig("xpcprobe-x3", minimal: false))!;
            node["resources"]!["memoryInBytes"] = 100L * 1024 * 1024;
            Create(client, "xpcprobe-x3", node.ToJsonString(), kernelJson!, options: null);
            Delete(client, "xpcprobe-x3", force: true);
        });

        Try("unknown image reference (not in local store)", () =>
        {
            var node = JsonNode.Parse(BuildConfig("xpcprobe-x4", minimal: true))!;
            node["image"]!["reference"] = "docker.io/library/alpine:9.99";
            node["image"]!["descriptor"]!["digest"] = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
            Create(client, "xpcprobe-x4", node.ToJsonString(), kernelJson!, options: null);
            Delete(client, "xpcprobe-x4", force: true);
        });

        Try("duplicate create of an existing id (xpcprobe-dup created first, then created again)", () =>
        {
            Create(client, "xpcprobe-dup", BuildConfig("xpcprobe-dup", minimal: true), kernelJson!, options: null);
            try { Create(client, "xpcprobe-dup", BuildConfig("xpcprobe-dup", minimal: true), kernelJson!, options: null); }
            finally { Delete(client, "xpcprobe-dup", force: true); }
        });

        Try("duplicate hostname (xpcprobe-h1 with hostname xpcprobe-h1, then xpcprobe-h2 with the same hostname)", () =>
        {
            var n1 = JsonNode.Parse(BuildConfig("xpcprobe-h1", minimal: false))!;
            Create(client, "xpcprobe-h1", n1.ToJsonString(), kernelJson!, options: null);
            try
            {
                var n2 = JsonNode.Parse(BuildConfig("xpcprobe-h2", minimal: false))!;
                n2["networks"]![0]!["options"]!["hostname"] = "xpcprobe-h1";
                Create(client, "xpcprobe-h2", n2.ToJsonString(), kernelJson!, options: null);
                Delete(client, "xpcprobe-h2", force: true);
            }
            finally { Delete(client, "xpcprobe-h1", force: true); }
        });

        Try("delete of a non-existent id", () => Delete(client, "xpcprobe-nope", force: true));

        Try("unknown route \"bogusRoute\" (server drops the message without replying; timed)", () =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try { Xpc.xpc_release(client.Send(ApiServerClient.NewMessage("bogusRoute"))); }
            finally { Console.WriteLine($"  returned after {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F3} ms; events so far={ApiServerClient.EventCount} last={ApiServerClient.LastEvent ?? "none"}"); }
        });

        Try("ping on the same connection right after the unknown route", () =>
        {
            nint r = client.Send(ApiServerClient.NewMessage("ping"));
            Console.WriteLine($"  apiServerVersion = \"{Xpc.GetString(r, "apiServerVersion")}\"; remote pid = {client.RemotePid}");
            Xpc.xpc_release(r);
        });

        Try("message without a route key", () => Xpc.xpc_release(client.Send(Xpc.xpc_dictionary_create(0, 0, 0))));
    }

    // ------------------------------------------------------------------ 6. CLI comparison
    if (Want("cli"))
    {
        Header("CLI comparison: `container create --name xpcprobe-cli alpine:3.20 sleep 60` + `container delete xpcprobe-cli` x10");
        var createMs = new List<double>();
        var deleteMs = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Cli("create", "--name", CliName, "alpine:3.20", "sleep", "60");
            createMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            t0 = Stopwatch.GetTimestamp();
            Cli("delete", CliName);
            deleteMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        Report("container create (CLI)", createMs);
        Report("container delete (CLI)", deleteMs);
        Report("create+delete cycle (CLI)", createMs.Zip(deleteMs, (a, b) => a + b).ToList());

        Measure("container ls -a (CLI)", warmup: 2, n: 10, () => Cli("ls", "-a"));
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex}");
    exitCode = 1;
}
finally
{
    Header("cleanup");
    foreach (var name in new[] { ProbeName, MinimalName, "xpcprobe-x2", "xpcprobe-x3", "xpcprobe-x4", "xpcprobe-dup", "xpcprobe-h1", "xpcprobe-h2" })
    {
        try { Delete(client, name, force: true); Console.WriteLine($"  deleted leftover {name}"); }
        catch (ApiServerException e) when (e.Code == "notFound") { }
        catch (Exception e) { Console.WriteLine($"  cleanup {name}: {e.Message}"); }
    }
    try { Cli("delete", CliName); Console.WriteLine($"  deleted leftover {CliName}"); } catch { }
    string ls = Cli("ls", "-a");
    var leftovers = ls.Split('\n').Where(l => l.StartsWith("xpcprobe-") && !l.StartsWith("xpcprobe-ref")).ToList();
    Console.WriteLine(leftovers.Count == 0 ? "  no xpcprobe-* leftovers (xpcprobe-ref is the CLI-made reference, removed by the shell)" : "  LEFTOVERS: " + string.Join(" | ", leftovers));
    Console.WriteLine($"  xpc connection events seen: {ApiServerClient.EventCount} (last: {ApiServerClient.LastEvent ?? "none"})");
}
return exitCode;

// ===================================================================== helpers

static void Header(string s) { Console.WriteLine(); Console.WriteLine($"==== {s} ===="); }

static string RuntimeInfo() => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";

static void Measure(string label, int warmup, int n, Action op)
{
    for (int i = 0; i < warmup; i++) op();
    var samples = new List<double>(n);
    for (int i = 0; i < n; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        op();
        samples.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    Report(label, samples);
}

static void Report(string label, List<double> ms)
{
    var s = ms.OrderBy(x => x).ToList();
    double P(double p) => s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p * s.Count) - 1))];
    Console.WriteLine($"  {label}: n={s.Count} min={s[0]:F3} median={P(0.5):F3} p99={P(0.99):F3} max={s[^1]:F3} mean={s.Average():F3} ms");
}

static void Try(string label, Action a)
{
    Console.WriteLine($"- {label}");
    try { a(); Console.WriteLine("  -> OK (accepted)"); }
    catch (ApiServerException e) { Console.WriteLine($"  -> error JSON: {e.RawJson}"); }
    catch (Exception e) { Console.WriteLine($"  -> {e.GetType().Name}: {e.Message}"); }
}

static void Create(ApiServerClient client, string id, string configJson, byte[]? kernel, string? options)
{
    nint m = ApiServerClient.NewMessage("containerCreate");
    Xpc.SetJson(m, "containerConfig", configJson);
    if (kernel != null) Xpc.SetData(m, "kernel", kernel);
    if (options != null) Xpc.SetJson(m, "containerOptions", options);
    Xpc.xpc_release(client.Send(m));
}

static void Delete(ApiServerClient client, string id, bool force)
{
    nint m = ApiServerClient.NewMessage("containerDelete");
    Xpc.xpc_dictionary_set_string(m, "id", id);
    Xpc.xpc_dictionary_set_bool(m, "forceDelete", force);
    Xpc.xpc_release(client.Send(m));
}

static List<string> ListIds(ApiServerClient client)
{
    nint r = client.Send(ApiServerClient.NewMessage("containerList"));
    byte[] data = Xpc.GetData(r, "containers") ?? [];
    Xpc.xpc_release(r);
    return JsonNode.Parse(data)!.AsArray().Select(IdOf).ToList();
}

static string IdOf(JsonNode? n) => n?["id"]?.GetValue<string>() ?? n?["configuration"]?["id"]?.GetValue<string>() ?? "?";

static void PrintContainers(string label, byte[] data)
{
    string json = Encoding.UTF8.GetString(data);
    var arr = JsonNode.Parse(data)!.AsArray();
    Console.WriteLine($"[{label}] containers payload: {data.Length} bytes, {arr.Count} containers");
    Console.WriteLine($"  ids: {string.Join(", ", arr.Select(IdOf))}");
    Console.WriteLine($"  first 500 chars: {json[..Math.Min(500, json.Length)]}");
}

// ContainerConfiguration JSON. The "full" variant is the `configuration` object printed by
// `container inspect xpcprobe-ref` (container 1.3.0) with id/hostname adjusted and creationDate
// converted to Swift's default Date encoding (seconds since 2001-01-01, a JSON number).
static string BuildConfig(string id, bool minimal)
{
    var image = new JsonObject
    {
        ["reference"] = "docker.io/library/alpine:3.20",
        ["descriptor"] = new JsonObject
        {
            ["digest"] = "sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc",
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["size"] = 9226,
        },
    };
    var initProcess = new JsonObject
    {
        ["executable"] = "sleep",
        ["arguments"] = new JsonArray("60"),
        ["environment"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"),
        ["workingDirectory"] = "/",
        ["terminal"] = false,
        ["user"] = new JsonObject { ["id"] = new JsonObject { ["uid"] = 0, ["gid"] = 0 } },
        ["supplementalGroups"] = new JsonArray(),
        ["rlimits"] = new JsonArray(),
    };

    if (minimal)
    {
        return new JsonObject { ["id"] = id, ["image"] = image, ["initProcess"] = initProcess }.ToJsonString();
    }

    double creationDate = (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    return new JsonObject
    {
        ["capAdd"] = new JsonArray(),
        ["capDrop"] = new JsonArray(),
        ["creationDate"] = creationDate,
        ["dns"] = new JsonObject { ["nameservers"] = new JsonArray(), ["options"] = new JsonArray(), ["searchDomains"] = new JsonArray() },
        ["id"] = id,
        ["image"] = image,
        ["initProcess"] = initProcess,
        ["labels"] = new JsonObject(),
        ["mounts"] = new JsonArray(),
        ["networks"] = new JsonArray(new JsonObject
        {
            ["network"] = "default",
            ["options"] = new JsonObject { ["hostname"] = id, ["mtu"] = 1280 },
        }),
        ["platform"] = new JsonObject { ["architecture"] = "arm64", ["os"] = "linux" },
        ["publishedPorts"] = new JsonArray(),
        ["publishedSockets"] = new JsonArray(),
        ["readOnly"] = false,
        ["resources"] = new JsonObject { ["cpuOverhead"] = 1, ["cpus"] = 4, ["memoryInBytes"] = 1073741824L },
        ["rosetta"] = false,
        ["runtimeHandler"] = "container-runtime-linux",
        ["ssh"] = false,
        ["sysctls"] = new JsonObject(),
        ["useInit"] = false,
        ["virtualization"] = false,
    }.ToJsonString();
}

static string Cli(params string[] cliArgs)
{
    var psi = new ProcessStartInfo(ContainerCli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in cliArgs) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"container {string.Join(' ', cliArgs)} exited {p.ExitCode}: {stderr.Trim()}");
    return stdout;
}
