using Cider.AppleContainer.Native;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// <c>OpenPty</c> collapses six distinct native failure modes into one <c>-1</c> return; these
/// tests exercise the errno-naming mechanism it now uses to keep a pty allocation failure
/// diagnosable. <c>OpenPty</c> itself is not behind the <c>IPtySyscalls</c> seam
/// (that only covers the post-launch syscalls a held process still makes — see
/// <c>PtySyscalls.cs</c>), and driving each of its six steps to fail in turn would need either root
/// or process-wide state (e.g. lowering the file descriptor limit) that risks starving concurrently
/// running tests. What *is* testable without either is the exact mechanism <c>OpenPty</c> uses to
/// name a failure: <c>grantpt</c> on any fd that is not a freshly cloned ptmx master fails with a
/// real, deterministic, root-free errno (ENOTTY on Darwin), so driving that one real failure through
/// <c>Libc.Describe</c> is a genuine (not mocked) check that the message names the call and carries
/// the errno detail.
/// </summary>
public class LibcTests
{
    [Fact]
    public void Describe_NamesTheFailingCall_AndCarriesTheRealErrno()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ad-libc-{Guid.NewGuid():N}");
        var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var fd = (int)file.SafeFileHandle.DangerousGetHandle();

            // A regular file is not a ptmx-cloned master, so `grantpt` fails deterministically —
            // no root, no shared process state, nothing else touched.
            var result = Libc.GrantPt(fd);
            Assert.NotEqual(0, result);

            // Must run immediately after the failing call: any other P/Invoke (even one from this
            // test process) would overwrite the thread's last errno first, exactly the trap the
            // production code avoids by calling `Describe(...)` before its own `Close`.
            var message = Libc.Describe("grantpt");

            Assert.StartsWith("grantpt failed (errno ", message, StringComparison.Ordinal);
            Assert.NotEqual("grantpt failed (errno 0)", message);
        }
        finally
        {
            file.Dispose();
            File.Delete(path);
        }
    }
}
