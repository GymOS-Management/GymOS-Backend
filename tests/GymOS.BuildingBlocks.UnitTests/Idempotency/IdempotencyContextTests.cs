using GymOS.BuildingBlocks.Application.Idempotency;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class IdempotencyContextTests
{
    [Fact]
    public void SetKey_stores_the_key_once()
    {
        var context = new IdempotencyContext();
        context.SetKey("key-1");
        Assert.Equal("key-1", context.Key);
    }

    [Fact]
    public void SetKey_twice_throws_exactly_one_edge_owns_it()
    {
        var context = new IdempotencyContext();
        context.SetKey("key-1");
        Assert.Throws<InvalidOperationException>(() => context.SetKey("key-2"));
    }

    [Fact]
    public void Flags_default_to_not_replayed_and_no_retry_after()
    {
        var context = new IdempotencyContext();
        Assert.Null(context.Key);
        Assert.False(context.WasReplayed);
        Assert.Null(context.RetryAfter);
    }

    [Fact]
    public void MarkReplayed_and_SetRetryAfter_are_readable_back()
    {
        var context = new IdempotencyContext();
        context.MarkReplayed();
        context.SetRetryAfter(TimeSpan.FromSeconds(30));
        Assert.True(context.WasReplayed);
        Assert.Equal(TimeSpan.FromSeconds(30), context.RetryAfter);
    }
}
