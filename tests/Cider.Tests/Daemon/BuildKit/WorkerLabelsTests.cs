using Cider.Daemon.BuildKit;
using Moby.Buildkit.V1;
using Moby.Buildkit.V1.Types;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary><see cref="WorkerLabels"/> in isolation.</summary>
public sealed class WorkerLabelsTests
{
    [Fact]
    public void Strip_removes_the_snapshotter_label_from_every_worker()
    {
        var response = new ListWorkersResponse();
        var worker1 = new WorkerRecord { ID = "w1" };
        worker1.Labels["org.mobyproject.buildkit.worker.snapshotter"] = "overlayfs";
        worker1.Labels["org.mobyproject.buildkit.worker.executor"] = "oci";
        response.Record.Add(worker1);

        var worker2 = new WorkerRecord { ID = "w2" };
        worker2.Labels["org.mobyproject.buildkit.worker.snapshotter"] = "overlayfs";
        response.Record.Add(worker2);

        WorkerLabels.Strip(response);

        Assert.False(response.Record[0].Labels.ContainsKey("org.mobyproject.buildkit.worker.snapshotter"));
        Assert.Equal("oci", response.Record[0].Labels["org.mobyproject.buildkit.worker.executor"]);
        Assert.False(response.Record[1].Labels.ContainsKey("org.mobyproject.buildkit.worker.snapshotter"));
    }

    [Fact]
    public void Strip_is_a_no_op_when_the_label_is_absent()
    {
        var response = new ListWorkersResponse();
        var worker = new WorkerRecord { ID = "w1" };
        worker.Labels["org.mobyproject.buildkit.worker.executor"] = "oci";
        response.Record.Add(worker);

        WorkerLabels.Strip(response);

        Assert.Equal("oci", response.Record[0].Labels["org.mobyproject.buildkit.worker.executor"]);
    }

    [Fact]
    public void Strip_adds_the_host_gateway_ip_label_when_given_and_absent()
    {
        var response = new ListWorkersResponse();
        var worker = new WorkerRecord { ID = "w1" };
        worker.Labels["org.mobyproject.buildkit.worker.snapshotter"] = "overlayfs";
        response.Record.Add(worker);

        WorkerLabels.Strip(response, "192.168.64.1");

        Assert.False(response.Record[0].Labels.ContainsKey("org.mobyproject.buildkit.worker.snapshotter"));
        Assert.Equal("192.168.64.1", response.Record[0].Labels["org.mobyproject.buildkit.worker.moby.host-gateway-ip"]);
    }

    [Fact]
    public void Strip_does_not_overwrite_an_existing_host_gateway_ip_label()
    {
        var response = new ListWorkersResponse();
        var worker = new WorkerRecord { ID = "w1" };
        worker.Labels["org.mobyproject.buildkit.worker.moby.host-gateway-ip"] = "10.0.0.1";
        response.Record.Add(worker);

        WorkerLabels.Strip(response, "192.168.64.1");

        Assert.Equal("10.0.0.1", response.Record[0].Labels["org.mobyproject.buildkit.worker.moby.host-gateway-ip"]);
    }
}
