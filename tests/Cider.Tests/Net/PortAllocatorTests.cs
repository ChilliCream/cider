using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cider.Core.DockerApi;
using Cider.Core.Net;
using Xunit;

namespace Cider.Tests.Net;

public class PortAllocatorTests
{
    [Fact]
    public void Reserve_without_a_request_picks_a_port_from_dockers_ephemeral_range()
    {
        var allocator = new PortAllocator();

        var port = allocator.Reserve("tcp", "", null);

        Assert.InRange(port, PortAllocator.EphemeralMin, PortAllocator.EphemeralMax);
        Assert.True(allocator.IsReserved("tcp", "", port));
        Assert.Equal(1, allocator.ReservationCount);
    }

    [Fact]
    public void Reserve_never_hands_out_the_same_port_twice()
    {
        var allocator = new PortAllocator();
        var ports = new HashSet<int>();

        for (var i = 0; i < 25; i++)
        {
            Assert.True(ports.Add(allocator.Reserve("tcp", "", null)));
        }

        Assert.Equal(25, allocator.ReservationCount);
    }

    [Fact]
    public void Release_makes_a_port_available_again()
    {
        var allocator = new PortAllocator();

        var port = allocator.Reserve("tcp", "", null);
        allocator.Release("tcp", "", port);

        Assert.False(allocator.IsReserved("tcp", "", port));
        Assert.Equal(port, allocator.Reserve("tcp", "", port));
    }

    [Fact]
    public void A_requested_port_that_is_already_reserved_is_a_conflict()
    {
        var allocator = new PortAllocator();
        var port = allocator.Reserve("tcp", "", null);

        var error = Assert.Throws<DockerApiException>(() => allocator.Reserve("tcp", "", port));

        Assert.Equal(HttpStatusCode.InternalServerError, error.Status);
        Assert.Contains("port is already allocated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_requested_port_that_the_os_holds_is_a_conflict()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var taken = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var allocator = new PortAllocator();

        Assert.Throws<DockerApiException>(() => allocator.Reserve("tcp", "127.0.0.1", taken));
    }

    [Fact]
    public void Tcp_and_udp_reservations_are_independent()
    {
        var allocator = new PortAllocator();

        // Pick a port that is free for both protocols (some UDP ports are held by the OS).
        var port = 0;
        var shared = false;
        for (var attempt = 0; attempt < 20 && !shared; attempt++)
        {
            port = allocator.Reserve("tcp", "", null);
            try
            {
                allocator.Reserve("udp", "", port);
                shared = true;
            }
            catch (DockerApiException)
            {
                // try another port
            }
        }

        Assert.True(shared, "no port was free for both tcp and udp");
        Assert.True(allocator.IsReserved("tcp", "", port));
        Assert.True(allocator.IsReserved("udp", "", port));
    }

    [Fact]
    public void A_wildcard_reservation_also_blocks_a_specific_address()
    {
        var allocator = new PortAllocator();
        var port = allocator.Reserve("tcp", "0.0.0.0", null);

        Assert.True(allocator.IsReserved("tcp", "127.0.0.1", port));
        Assert.Throws<DockerApiException>(() => allocator.Reserve("tcp", "127.0.0.1", port));
    }

    [Fact]
    public void An_empty_host_ip_means_the_wildcard_address()
    {
        var allocator = new PortAllocator();
        var port = allocator.Reserve("tcp", "", null);

        Assert.True(allocator.IsReserved("tcp", "0.0.0.0", port));
    }

    [Fact]
    public void Bad_input_is_a_400()
    {
        var allocator = new PortAllocator();

        Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<DockerApiException>(() => allocator.Reserve("icmp", "", null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<DockerApiException>(() => allocator.Reserve("tcp", "not-an-ip", null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<DockerApiException>(() => allocator.Reserve("tcp", "", 70000)).Status);
    }

    [Fact]
    public void Reserve_is_thread_safe()
    {
        var allocator = new PortAllocator();
        var ports = new ConcurrentBag<int>();

        Parallel.For(0, 40, _ => ports.Add(allocator.Reserve("tcp", "", null)));

        Assert.Equal(40, ports.Distinct().Count());
        Assert.Equal(40, allocator.ReservationCount);
    }

    [Fact]
    public void ReleaseAll_clears_every_reservation()
    {
        var allocator = new PortAllocator();
        allocator.Reserve("tcp", "", null);
        allocator.Reserve("tcp", "", null);

        allocator.ReleaseAll();

        Assert.Equal(0, allocator.ReservationCount);
    }
}
