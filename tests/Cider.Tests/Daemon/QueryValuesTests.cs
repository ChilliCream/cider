using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// <see cref="QueryValues.Tail"/> is the split half of the <c>tail=0</c> bug: it must distinguish
/// "unset" (null, meaning everything) from a genuine zero, and it must never let a malformed or
/// negative value collapse to zero — that would silently hide all log output, a worse failure
/// than the bug being fixed.
/// </summary>
public sealed class QueryValuesTests
{
    private static HttpRequest NewRequest(string? queryString)
    {
        var context = new DefaultHttpContext();
        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        return context.Request;
    }

    [Fact]
    public void Tail_absent_means_unset()
    {
        Assert.Null(QueryValues.Tail(NewRequest(null)));
    }

    [Fact]
    public void Tail_all_means_unset()
    {
        Assert.Null(QueryValues.Tail(NewRequest("?tail=all")));
        Assert.Null(QueryValues.Tail(NewRequest("?tail=ALL")));
    }

    [Fact]
    public void Tail_zero_is_a_real_zero_not_unset()
    {
        Assert.Equal(0, QueryValues.Tail(NewRequest("?tail=0")));
    }

    [Fact]
    public void Tail_one_parses_as_one()
    {
        Assert.Equal(1, QueryValues.Tail(NewRequest("?tail=1")));
    }

    [Fact]
    public void Tail_malformed_or_negative_falls_back_to_unset_not_zero()
    {
        // A malformed/negative tail must NOT collapse to 0 — that would (via LogStore's
        // Tail-is-not-null filter) hide every line, a worse failure than the original bug.
        // dockerd's own behaviour on a bad tail value is to fall back to "all".
        Assert.Null(QueryValues.Tail(NewRequest("?tail=banana")));
        Assert.Null(QueryValues.Tail(NewRequest("?tail=-1")));
        Assert.Null(QueryValues.Tail(NewRequest("?tail=")));
    }
}
