using FluentAssertions;
using TCV.App.Collections;

namespace TCV.App.Tests;

public class LruCacheTests
{
    [Fact]
    public void Set_WhenFull_DropsTheLeastRecentlyUsed()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.TryGet("a", out _);   // "b" is now the oldest

        cache.Set("c", 3);

        cache.TryGet("b", out _).Should().BeFalse();
        cache.TryGet("a", out var a).Should().BeTrue();
        a.Should().Be(1);
        cache.TryGet("c", out var c).Should().BeTrue();
        c.Should().Be(3);
        cache.Count.Should().Be(2);
    }

    [Fact]
    public void Set_AnExistingKey_ReplacesItWithoutEvictingAnother()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);

        cache.Set("a", 10);

        cache.Count.Should().Be(2);
        cache.TryGet("a", out var a).Should().BeTrue();
        a.Should().Be(10);
        cache.TryGet("b", out _).Should().BeTrue();
    }

    [Fact]
    public void TryGet_ARememberedNull_IsAHit()
    {
        // "This file has no picture" is an answer worth keeping: it saves reading the file again.
        var cache = new LruCache<string, object?>(4);
        cache.Set("broken.png", null);

        cache.TryGet("broken.png", out var value).Should().BeTrue();
        value.Should().BeNull();
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var cache = new LruCache<int, int>(3);
        cache.Set(1, 1);
        cache.Set(2, 2);

        cache.Clear();

        cache.Count.Should().Be(0);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithNoRoom_IsRejected()
    {
        var create = () => new LruCache<int, int>(0);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
