using Cider.Core.Ids;
using Xunit;

namespace Cider.Tests.Ids;

public class ContainerIdentityTests
{
    private static readonly string Id = new string('a', 60) + "beef";

    [Fact]
    public void Reuses_the_docker_name_when_apple_accepts_it()
    {
        Assert.Equal("app-web-1", ContainerIdentity.ResolveRuntimeId(Id, "app-web-1"));
        Assert.Equal("app-web-1", ContainerIdentity.ResolveRuntimeId(Id, "/app-web-1"));
    }

    [Fact]
    public void Falls_back_to_the_short_id_for_unusable_names()
    {
        Assert.Equal(DockerId.Short(Id), ContainerIdentity.ResolveRuntimeId(Id, null));
        Assert.Equal(DockerId.Short(Id), ContainerIdentity.ResolveRuntimeId(Id, ""));
        Assert.Equal(DockerId.Short(Id), ContainerIdentity.ResolveRuntimeId(Id, "-bad"));
        Assert.Equal(DockerId.Short(Id), ContainerIdentity.ResolveRuntimeId(Id, new string('n', 64)));
    }

    [Fact]
    public void Stamps_the_cider_labels_over_the_user_labels()
    {
        var labels = ContainerIdentity.BuildLabels(Id, "/web", new Dictionary<string, string> { ["a"] = "1" });

        Assert.Equal("1", labels["a"]);
        Assert.Equal(Id, labels[ContainerIdentity.IdLabel]);
        Assert.Equal("web", labels[ContainerIdentity.NameLabel]);

        Assert.Equal(Id, ContainerIdentity.ReadDockerId(labels));
        Assert.Equal("web", ContainerIdentity.ReadDockerName(labels));
    }

    [Fact]
    public void Objects_without_our_labels_are_not_ours()
    {
        Assert.Null(ContainerIdentity.ReadDockerId(null));
        Assert.Null(ContainerIdentity.ReadDockerId(new Dictionary<string, string>()));
        Assert.Null(ContainerIdentity.ReadDockerId(new Dictionary<string, string> { [ContainerIdentity.IdLabel] = "nope" }));
    }
}
