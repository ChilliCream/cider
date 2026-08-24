using System.Net;
using Cider.Core.DockerApi;
using Xunit;

namespace Cider.Tests.DockerApi;

public class DockerErrorsTests
{
    [Fact]
    public void Not_found_messages_match_dockers_wording()
    {
        Assert.Equal("No such container: abc", DockerErrors.NoSuchContainer("abc").Message);
        Assert.Equal("No such image: alpine:latest", DockerErrors.NoSuchImage("alpine:latest").Message);
        Assert.Equal("network mynet not found", DockerErrors.NoSuchNetwork("mynet").Message);
        Assert.Equal("get data: no such volume", DockerErrors.NoSuchVolume("data").Message);
        Assert.Equal("No such exec instance: e1", DockerErrors.NoSuchExec("e1").Message);

        Assert.Equal(HttpStatusCode.NotFound, DockerErrors.NoSuchContainer("abc").Status);
        Assert.Equal(404, DockerErrors.NoSuchImage("x").StatusCode);
    }

    [Fact]
    public void Conflicts_carry_409()
    {
        Assert.Equal(HttpStatusCode.Conflict, DockerErrors.Conflict("boom").Status);

        Assert.Equal(
            "Conflict. The container name \"/web\" is already in use by container \"abc\". " +
            "You have to remove (or rename) that container to be able to reuse that name.",
            DockerErrors.ContainerNameConflict("/web", "abc").Message);

        Assert.Equal(
            "cannot remove container \"web\": container is running: stop the container before removing or force remove",
            DockerErrors.ContainerRunning("web").Message);
    }

    [Fact]
    public void Other_factories_use_the_right_status()
    {
        Assert.Equal(HttpStatusCode.BadRequest, DockerErrors.BadParameter("bad").Status);
        Assert.Equal(HttpStatusCode.NotImplemented, DockerErrors.NotImplemented("nope").Status);
        Assert.Equal(HttpStatusCode.InternalServerError, DockerErrors.Internal("boom").Status);

        var notModified = DockerErrors.NotModified();
        Assert.Equal(HttpStatusCode.NotModified, notModified.Status);
        Assert.Equal("", notModified.Message);
    }
}
