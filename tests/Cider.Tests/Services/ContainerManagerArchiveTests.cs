using System.Formats.Tar;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>
/// <c>HEAD/GET/PUT /containers/{id}/archive</c>, i.e. what <c>docker cp</c> talks to. The cases here
/// are the ones Aspire/DCP walks into (the empirical gap report, gaps 4-6): it stats the
/// container root, its tar carries absolute entry names, and it copies its development certificates
/// in between <c>create</c> and <c>start</c> — which Apple <c>container cp</c> refuses outright.
/// </summary>
public sealed class ContainerManagerArchiveTests
{
    /// <summary>An entry name straight out of DCP's certificate tar.</summary>
    private const string CertificateEntry = "/usr/lib/ssl/aspire/private/2f3ab1c4.crt";

    [Fact]
    public async Task StatPath_OfTheContainerRoot_IsAnsweredWithoutTouchingTheEngine()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        // `docker cp <src> <id>:/` stats "/" first. Apple `container cp <name>:/ …` fails with
        // "source path has no last component: /" (and refuses a container that is not running at
        // all), so serving this by copying the path out answers 500 and DCP gives up.
        var stat = await harness.Containers.StatPathAsync(record.Id, "/", CancellationToken.None);

        Assert.Equal("/", stat.Name);
        Assert.Equal(0, stat.Size);
        Assert.Equal(0x800001EDu, stat.Mode); // Go's os.ModeDir|0755, as the docker CLI expects.
        Assert.DoesNotContain(harness.Runtime.Calls, call => call.StartsWith("CopyFromContainerAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PutArchive_IntoARunningContainer_StripsTheLeadingSlashOffEntryNames()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");

        // TarFile.ExtractToDirectoryAsync refuses an absolute entry name outright; real dockerd
        // strips the leading '/', and DCP's certificate tar is written exactly this way.
        await harness.Containers.PutArchiveAsync(
            record.Id,
            "/",
            Tar((CertificateEntry, "cert")),
            noOverwriteDirNonDir: false,
            CancellationToken.None);

        Assert.Equal("cert", FileInContainer(harness, record, CertificateEntry));
        Assert.False(Directory.Exists(StagingRoot(harness, record)), "a running container must not stage anything");
    }

    [Theory]
    [InlineData("./relative/file.txt", "/relative/file.txt")]
    [InlineData("plain.txt", "/plain.txt")]
    public async Task PutArchive_NormalizesEntryNames(string entryName, string expectedPath)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((entryName, "body")), noOverwriteDirNonDir: false, CancellationToken.None);

        Assert.Equal("body", FileInContainer(harness, record, expectedPath));
    }

    [Fact]
    public async Task PutArchive_WithAGoWrittenDirectoryEntry_ExtractsInsteadOfOverflowing()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");

        // Go's archive/tar writers put os.FileMode straight into the header, so a directory's mode
        // is os.ModeDir|0755 = 0x800001ED, base-256 encoded because it no longer fits the octal
        // field. TarReader parses the mode as an int and throws OverflowException over the whole
        // archive. This is byte for byte what Aspire/DCP sends for /usr/lib/ssl/aspire/certs.
        var archive = new MemoryStream();
        archive.Write(GoDirectoryHeader("/usr/lib/ssl/aspire/certs"));
        WriteInto(archive, Tar(("/usr/lib/ssl/aspire/certs/ce275665.pem", "pem")));

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", archive, noOverwriteDirNonDir: false, CancellationToken.None);

        Assert.Equal("pem", FileInContainer(harness, record, "/usr/lib/ssl/aspire/certs/ce275665.pem"));
    }

    [Fact]
    public async Task PutArchive_WithASymbolicLinkEntry_CreatesTheLink()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");

        // TarEntry.ExtractToFile refuses link entries ("Entry type 'SymbolicLink' not supported for
        // extraction"); dockerd creates them, and DCP's certificate tar carries the c_rehash-style
        // <hash>.0 link next to the PEM it names.
        var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "/certs/real.pem")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("pem")),
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "/certs/ce275665.0")
            {
                LinkName = "real.pem",
            });
        }

        archive.Position = 0;
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", archive, noOverwriteDirNonDir: false, CancellationToken.None);

        // The fake engine copies a directory by walking it, so the link shows up with the content it
        // points at — which is what `container cp` of the extracted tree does too.
        Assert.Equal("pem", FileInContainer(harness, record, "/certs/real.pem"));
        Assert.Equal("pem", FileInContainer(harness, record, "/certs/ce275665.0"));
    }

    [Fact]
    public async Task PutArchive_WithAnEntryEscapingTheDestination_Is400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("../escaped.txt", "nope")), noOverwriteDirNonDir: false, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("invalid tar archive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutArchive_IntoACreatedContainer_IsMountedInPlaceBeforeItStarts()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        // Apple `container cp` refuses a container that is not running, and this is precisely how
        // Aspire injects its certificates: create, cp, start.
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((CertificateEntry, "cert")), noOverwriteDirNonDir: false, CancellationToken.None);

        Assert.Null(FileInContainer(harness, record, CertificateEntry));
        Assert.True(
            Directory.Exists(StagingRoot(harness, record)),
            "the archive must be staged under the data dir so it survives a daemon restart");
        Assert.StartsWith(harness.Options.DataDir, StagingRoot(harness, record), StringComparison.Ordinal);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        // The file is a bind mount of the staged copy, so it is there the moment the entrypoint runs
        // — copying it in after the start is too late for an image that reads it at once.
        var mount = Assert.Single(
            harness.Runtime.GetSpec(record.RuntimeId)!.Mounts,
            candidate => string.Equals(candidate.Target, CertificateEntry, StringComparison.Ordinal));
        Assert.Equal(MountKind.Bind, mount.Kind);
        Assert.StartsWith(StagingRoot(harness, record), mount.Source, StringComparison.Ordinal);
        Assert.Equal("cert", FileInContainer(harness, record, CertificateEntry));

        // ... and because the engine mounts them from there, the staged files stay put.
        Assert.True(File.Exists(mount.Source), "the mount source must survive the start");
        Assert.DoesNotContain(
            harness.Runtime.Calls,
            call => call.StartsWith("CopyToContainerAsync", StringComparison.Ordinal));

        // Nothing the client asked for changed: the mount is engine-side only.
        Assert.Empty(record.Mounts);
    }

    [Fact]
    public async Task PutArchive_IntoAContainerThatAlreadyRan_IsCopiedInOnTheNextStart()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, CancellationToken.None);

        // A container that has already run cannot be re-created without losing its filesystem, so
        // this one is copied in after the start instead of mounted.
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((CertificateEntry, "cert")), noOverwriteDirNonDir: false, CancellationToken.None);

        // Apple `container cp` keeps answering "is not running" for a moment after `container start`
        // has already handed the init process over — the same race `container exec` has.
        harness.Runtime.CopyToNotRunningFailures = 3;
        harness.Containers.StagedArchiveFlushBackoff = TimeSpan.FromMilliseconds(1);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        Assert.Equal("cert", FileInContainer(harness, record, CertificateEntry));
        Assert.Equal(0, harness.Runtime.CopyToNotRunningFailures);
        Assert.False(Directory.Exists(StagingRoot(harness, record)), "a copied batch must be dropped");
    }

    [Fact]
    public async Task PutArchive_IntoACreatedContainer_ReplaysInOrder()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/etc/order.txt", "first")), noOverwriteDirNonDir: false, CancellationToken.None);
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/etc/order.txt", "second")), noOverwriteDirNonDir: false, CancellationToken.None);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        Assert.Equal("second", FileInContainer(harness, record, "/etc/order.txt"));
    }

    [Fact]
    public async Task StagedArchives_SurviveAFailedStart_AndAreStillThereAfterTheNextOne()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/etc/first.txt", "one")), noOverwriteDirNonDir: false, CancellationToken.None);

        // The first start mounts the batch and then fails, so the engine container it was mounted on
        // is gone: the batch must NOT count as delivered. DCP retries a resource whose start failed.
        harness.Runtime.StartFailure = RuntimeException.Conflict("boom");
        await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Containers.StartAsync(record.Id, CancellationToken.None));

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/etc/second.txt", "two")), noOverwriteDirNonDir: false, CancellationToken.None);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        Assert.Equal("one", FileInContainer(harness, record, "/etc/first.txt"));
        Assert.Equal("two", FileInContainer(harness, record, "/etc/second.txt"));
    }

    [Fact]
    public async Task StagedArchives_SurviveAFailedStart_FollowedByANetworkConnect()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var record = await harness.CreateShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((CertificateEntry, "cert")), noOverwriteDirNonDir: false, CancellationToken.None);

        harness.Runtime.StartFailure = RuntimeException.Conflict("boom");
        await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Containers.StartAsync(record.Id, CancellationToken.None));

        // A failed start leaves the container never-started, so `docker network connect` is still
        // accepted — and it re-creates the engine container from the record, which carries no staged
        // mounts. The batch has to be re-delivered on the next start all the same.
        await harness.Containers.AttachToNetworkAsync(record.Id, "extra", null, CancellationToken.None);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        Assert.Equal("cert", FileInContainer(harness, record, CertificateEntry));
    }

    [Fact]
    public async Task StagedArchives_SurviveADaemonRestart()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((CertificateEntry, "cert")), noOverwriteDirNonDir: false, CancellationToken.None);

        // A second manager over the same data dir, record store and engine: what the daemon looks
        // like after a restart. The staged tree lives next to the records, so it comes back with them.
        var restarted = new ContainerManager(
            harness.Runtime, harness.Store, harness.Logs, harness.Events, harness.Ports, harness.Publisher,
            harness.NameRegistry, harness.Dns, harness.Images, harness.Networks, harness.Volumes, harness.Options,
            NullLogger<ContainerManager>.Instance);

        await restarted.StartAsync(record.Id, CancellationToken.None);

        Assert.Equal("cert", FileInContainer(harness, record, CertificateEntry));
    }

    [Fact]
    public async Task RemovingAContainer_DropsWhatWasStagedForIt()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar((CertificateEntry, "cert")), noOverwriteDirNonDir: false, CancellationToken.None);
        Assert.True(Directory.Exists(StagingRoot(harness, record)));

        await harness.Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, CancellationToken.None);

        Assert.False(Directory.Exists(StagingRoot(harness, record)));
    }

    [Fact]
    public async Task GetArchive_OutOfAStoppedContainer_IsServedFromItsExport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/out.txt", "artefact")), noOverwriteDirNonDir: false, CancellationToken.None);
        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, CancellationToken.None);

        // `docker cp exited-container:/out.txt .` is how people retrieve build output and post-mortem
        // files; Apple `container cp` refuses a container that is not running, so this 409'd.
        // The container's own rootfs export still has the file.
        var before = harness.Runtime.Calls.Count(call => call.StartsWith("StartContainerAsync", StringComparison.Ordinal));

        var tar = new MemoryStream();
        await harness.Containers.GetArchiveAsync(record.Id, "/out.txt", tar, CancellationToken.None);

        Assert.Equal("artefact", Assert.Contains("out.txt", ReadTar(tar)));
        Assert.Contains(harness.Runtime.Calls, call => call.StartsWith("ExportContainerAsync", StringComparison.Ordinal));

        // Reading is all it does: nothing is started behind the client's back, and the exited
        // container keeps the status and exit code it stopped with.
        Assert.Equal(before, harness.Runtime.Calls.Count(call => call.StartsWith("StartContainerAsync", StringComparison.Ordinal)));
        var after = await harness.Containers.ResolveAsync(record.Id, CancellationToken.None);
        Assert.Equal("exited", after.State.Status);
        Assert.False(after.State.Running);
    }

    [Fact]
    public async Task GetArchive_OutOfAStoppedContainer_TakesTheWholeSubtree()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.PutArchiveAsync(
            record.Id,
            "/",
            Tar(("/data/a.txt", "one"), ("/data/nested/b.txt", "two"), ("/elsewhere.txt", "no")),
            noOverwriteDirNonDir: false,
            CancellationToken.None);
        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, CancellationToken.None);

        var tar = new MemoryStream();
        await harness.Containers.GetArchiveAsync(record.Id, "/data", tar, CancellationToken.None);

        var entries = ReadTar(tar);
        Assert.Equal("one", Assert.Contains("data/a.txt", entries));
        Assert.Equal("two", Assert.Contains("data/nested/b.txt", entries));
        Assert.DoesNotContain(entries, entry => entry.Key.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatPath_InAStoppedContainer_IsAnsweredFromItsExport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/out.txt", "artefact")), noOverwriteDirNonDir: false, CancellationToken.None);
        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, CancellationToken.None);

        // `docker cp` stats the source before it fetches it, so HEAD has to answer too.
        var stat = await harness.Containers.StatPathAsync(record.Id, "/out.txt", CancellationToken.None);

        Assert.Equal("out.txt", stat.Name);
        Assert.Equal("artefact".Length, stat.Size);
    }

    [Fact]
    public async Task GetArchive_OutOfAStoppedContainer_ForAPathThatIsNotThere_Is404()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.GetArchiveAsync(
            record.Id, "/nope.txt", new MemoryStream(), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task GetArchive_OutOfAContainerThatIsGone_IsStill404()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, CancellationToken.None);

        // Serving a stopped container from its export must not turn a deleted one into anything else.
        var ex = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.GetArchiveAsync(
            record.Id, "/out.txt", new MemoryStream(), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.DoesNotContain(harness.Runtime.Calls, call => call.StartsWith("ExportContainerAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetArchive_OutOfARunningContainer_StillCopiesDirectly()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30");
        await harness.Containers.PutArchiveAsync(
            record.Id, "/", Tar(("/out.txt", "artefact")), noOverwriteDirNonDir: false, CancellationToken.None);

        var tar = new MemoryStream();
        await harness.Containers.GetArchiveAsync(record.Id, "/out.txt", tar, CancellationToken.None);

        Assert.Equal("artefact", Assert.Contains("out.txt", ReadTar(tar)));
        Assert.DoesNotContain(harness.Runtime.Calls, call => call.StartsWith("ExportContainerAsync", StringComparison.Ordinal));
    }

    /// <summary>The regular-file entries of a <c>docker cp</c> tar, keyed by entry name.</summary>
    private static Dictionary<string, string> ReadTar(MemoryStream tar)
    {
        tar.Position = 0;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new TarReader(tar, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is { } data)
            {
                using var text = new StreamReader(data, Encoding.UTF8);
                entries[entry.Name] = text.ReadToEnd();
            }
            else
            {
                entries[entry.Name] = string.Empty;
            }
        }

        return entries;
    }

    private static string StagingRoot(ContainerTestHarness harness, ContainerRecord record) =>
        Path.Combine(harness.Options.DataDir, "archive-staging", record.Id);

    private static string? FileInContainer(ContainerTestHarness harness, ContainerRecord record, string path)
    {
        var files = harness.Runtime.GetContainer(record.RuntimeId)!.Files;
        return files.TryGetValue(path, out var content) ? Encoding.UTF8.GetString(content) : null;
    }

    /// <summary>
    /// One raw 512-byte tar header for a directory, written the way Go's <c>archive/tar</c> does when
    /// the caller passes an unmasked <c>os.FileMode</c>: the mode field carries <c>os.ModeDir|0755</c>
    /// base-256 encoded. Built by hand because no .NET writer will produce it.
    /// </summary>
    private static byte[] GoDirectoryHeader(string name)
    {
        var block = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(block, 0);

        // mode: 0x800001ED, base-256 (high bit of the first byte marks the encoding).
        byte[] mode = [0x80, 0x00, 0x00, 0x00, 0x80, 0x00, 0x01, 0xED];
        mode.CopyTo(block, 100);

        WriteOctal(block, 108, 8, 0); // uid
        WriteOctal(block, 116, 8, 0); // gid
        WriteOctal(block, 124, 12, 0); // size
        WriteOctal(block, 136, 12, 1787384386); // mtime
        block[156] = (byte)'5'; // directory
        Encoding.ASCII.GetBytes("ustar  ").CopyTo(block, 257); // GNU magic + version

        for (var i = 148; i < 156; i++)
        {
            block[i] = (byte)' ';
        }

        var checksum = block.Aggregate(0, (sum, value) => sum + value);
        WriteOctal(block, 148, 7, checksum);
        block[155] = (byte)' ';
        return block;
    }

    private static void WriteOctal(byte[] block, int offset, int length, long value)
    {
        var digits = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(digits).CopyTo(block, offset);
        block[offset + length - 1] = 0;
    }

    private static void WriteInto(MemoryStream destination, MemoryStream source)
    {
        source.CopyTo(destination);
        destination.Position = 0;
    }

    [Fact]
    public async Task PutArchive_IntoACreatedContainer_KeepsDirectoriesThatHoldNoFiles()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30");

        // A `docker cp` of a tree that includes an empty directory. Into a *running* container the
        // extraction creates it; on this path the replay used to enumerate files only, so a directory
        // with nothing in it had nothing to carry it in and vanished silently while every sibling
        // arrived.
        await harness.Containers.PutArchiveAsync(
            record.Id,
            "/data",
            TarWithDirectories(
                directories: ["empty", "outer", "outer/alsoempty"],
                files: [("outer/kept.txt", "kept")]),
            noOverwriteDirNonDir: false,
            CancellationToken.None);

        await harness.Containers.StartAsync(record.Id, CancellationToken.None);

        var mounts = harness.Runtime.GetSpec(record.RuntimeId)!.Mounts;
        Assert.Equal("kept", FileInContainer(harness, record, "/data/outer/kept.txt"));

        var empty = Assert.Single(mounts, m => string.Equals(m.Target, "/data/empty", StringComparison.Ordinal));
        Assert.Equal(MountKind.Bind, empty.Kind);
        Assert.True(Directory.Exists(empty.Source), "the empty directory must be mounted from the staged copy");

        // `outer` holds a file, so its own file mount already implies it — binding it too would
        // shadow that file. Only the file-less directory below it gets one.
        Assert.DoesNotContain(mounts, m => string.Equals(m.Target, "/data/outer", StringComparison.Ordinal));
        Assert.Contains(mounts, m => string.Equals(m.Target, "/data/outer/alsoempty", StringComparison.Ordinal));
    }

    /// <summary>A tar carrying explicit directory entries alongside its files.</summary>
    private static MemoryStream TarWithDirectories(string[] directories, (string Name, string Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var directory in directories)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory));
            }

            foreach (var (name, content) in files)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>A <c>docker cp</c> tar with these entries, exactly as a client would send it.</summary>
    private static MemoryStream Tar(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
