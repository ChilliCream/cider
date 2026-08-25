using Cider.AppleContainer.Xpc;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerRuntime"/>'s network/volume write-path request building and the volume
/// error-code disambiguation (task cider-ede.11), exercised as pure functions — no live apiserver.
/// </summary>
public class ResourceMappingTests
{
    // ---- BuildNetworkConfiguration -----------------------------------------------------------

    [Fact]
    public void BuildNetworkConfiguration_defaults_to_nat_mode_and_the_vmnet_plugin()
    {
        var spec = new NetworkSpec { Name = "n1" };

        var config = XpcContainerRuntime.BuildNetworkConfiguration(spec);

        Assert.Equal("n1", config.Name);
        Assert.Equal("nat", config.Mode);
        Assert.Equal("container-network-vmnet", config.Plugin);
        Assert.Null(config.Ipv4Subnet);
        Assert.Null(config.Ipv6Subnet);
        Assert.Empty(config.Labels);
        Assert.Empty(config.Options);
    }

    [Fact]
    public void BuildNetworkConfiguration_maps_Internal_to_hostOnly_mode()
    {
        var spec = new NetworkSpec { Name = "n1", Internal = true };

        var config = XpcContainerRuntime.BuildNetworkConfiguration(spec);

        Assert.Equal("hostOnly", config.Mode);
    }

    [Fact]
    public void BuildNetworkConfiguration_carries_subnets_and_labels_through()
    {
        var spec = new NetworkSpec
        {
            Name = "n1",
            Subnet = "192.168.200.0/24",
            SubnetV6 = "fd00::/64",
            Labels = new Dictionary<string, string> { ["team"] = "platform" },
            Options = new Dictionary<string, string> { ["mtu"] = "1400" },
        };

        var config = XpcContainerRuntime.BuildNetworkConfiguration(spec);

        Assert.Equal("192.168.200.0/24", config.Ipv4Subnet);
        Assert.Equal("fd00::/64", config.Ipv6Subnet);
        Assert.Equal("platform", config.Labels["team"]);
        Assert.Equal("1400", config.Options["mtu"]);
    }

    // ---- BuildVolumeDriverOpts ------------------------------------------------------------------

    [Fact]
    public void BuildVolumeDriverOpts_omits_size_when_unset()
    {
        var spec = new VolumeSpec { Name = "v1" };

        var opts = XpcContainerRuntime.BuildVolumeDriverOpts(spec);

        Assert.False(opts.ContainsKey("size"));
    }

    [Fact]
    public void BuildVolumeDriverOpts_formats_size_as_a_raw_byte_count_string()
    {
        // VolumeCreate.swift:48-52: --size is passed through verbatim as driverOpts["size"], the
        // same raw byte-count string ArgBuilder.CreateVolume sends the CLI transport — no K/M/G/T/P
        // suffix formatting on either side.
        var spec = new VolumeSpec { Name = "v1", SizeBytes = 1_073_741_824 };

        var opts = XpcContainerRuntime.BuildVolumeDriverOpts(spec);

        Assert.Equal("1073741824", opts["size"]);
    }

    [Fact]
    public void BuildVolumeDriverOpts_ignores_a_zero_or_negative_size()
    {
        var spec = new VolumeSpec { Name = "v1", SizeBytes = 0 };

        var opts = XpcContainerRuntime.BuildVolumeDriverOpts(spec);

        Assert.False(opts.ContainsKey("size"));
    }

    [Fact]
    public void BuildVolumeDriverOpts_carries_the_spec_options_through_alongside_size()
    {
        var spec = new VolumeSpec
        {
            Name = "v1",
            Options = new Dictionary<string, string> { ["journal"] = "off" },
            SizeBytes = 512,
        };

        var opts = XpcContainerRuntime.BuildVolumeDriverOpts(spec);

        Assert.Equal("off", opts["journal"]);
        Assert.Equal("512", opts["size"]);
    }

    // ---- ToVolumeRuntimeException ----------------------------------------------------------------
    // VolumeError (VolumeConfiguration.swift:110-134) is not a ContainerizationError, so every case
    // — not found, already exists, in use, invalid name — arrives over XPC as apiserver code
    // "invalidArgument" (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.5's string-sniff note); this
    // reads the message text to recover the real RuntimeErrorKind, the same way
    // XpcErrorMapper.ToRuntimeErrorReason already reads "not running" for containers.

    [Fact]
    public void ToVolumeRuntimeException_maps_not_found_text_to_NotFound()
    {
        var ex = XpcException.ApiServer("invalidArgument", "volume 'v1' not found");

        var runtimeEx = XpcContainerRuntime.ToVolumeRuntimeException(ex, "volume delete v1");

        Assert.Equal(RuntimeErrorKind.NotFound, runtimeEx.Kind);
        Assert.Equal("volume delete v1: volume 'v1' not found", runtimeEx.Message);
    }

    [Fact]
    public void ToVolumeRuntimeException_maps_already_exists_text_to_Conflict()
    {
        var ex = XpcException.ApiServer("invalidArgument", "volume 'v1' already exists");

        var runtimeEx = XpcContainerRuntime.ToVolumeRuntimeException(ex, "volume create v1");

        Assert.Equal(RuntimeErrorKind.Conflict, runtimeEx.Kind);
    }

    [Fact]
    public void ToVolumeRuntimeException_maps_in_use_text_to_Conflict()
    {
        var ex = XpcException.ApiServer(
            "invalidArgument", "volume 'v1' is currently in use and cannot be accessed by another container, or deleted");

        var runtimeEx = XpcContainerRuntime.ToVolumeRuntimeException(ex, "volume delete v1");

        Assert.Equal(RuntimeErrorKind.Conflict, runtimeEx.Kind);
    }

    [Fact]
    public void ToVolumeRuntimeException_falls_back_to_the_generic_mapping_for_an_unrecognised_message()
    {
        var ex = XpcException.ApiServer("invalidArgument", "invalid volume name 'bad name'");

        var runtimeEx = XpcContainerRuntime.ToVolumeRuntimeException(ex, "volume create bad name");

        Assert.Equal(RuntimeErrorKind.InvalidArgument, runtimeEx.Kind);
    }

    [Fact]
    public void ToVolumeRuntimeException_leaves_non_invalidArgument_codes_to_the_generic_mapping()
    {
        var ex = XpcException.ApiServer("unsupported", "volume driver not supported");

        var runtimeEx = XpcContainerRuntime.ToVolumeRuntimeException(ex, "volume create v1");

        Assert.Equal(RuntimeErrorKind.NotSupported, runtimeEx.Kind);
    }
}
