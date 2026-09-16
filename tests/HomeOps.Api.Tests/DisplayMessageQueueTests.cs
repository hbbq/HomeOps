using System.Collections.Concurrent;
using HomeOps.Api.Displays;
using Xunit;

namespace HomeOps.Api.Tests;

public sealed class DisplayMessageQueueTests
{
    [Fact]
    public void TryDequeue_ReturnsMessagesInFifoOrder()
    {
        var queue = new DisplayMessageQueue();

        Assert.Equal(DisplayMessageEnqueueResult.Enqueued, queue.TryEnqueue("display1", "first"));
        Assert.Equal(DisplayMessageEnqueueResult.Enqueued, queue.TryEnqueue("display1", "second"));

        Assert.True(queue.TryDequeue("display1", out var first));
        Assert.Equal("first", first);
        Assert.True(queue.TryDequeue("display1", out var second));
        Assert.Equal("second", second);
        Assert.False(queue.TryDequeue("display1", out _));
    }

    [Fact]
    public void Queues_AreIndependentByDisplayId()
    {
        var queue = new DisplayMessageQueue();
        queue.TryEnqueue("display1", "one");
        queue.TryEnqueue("display2", "two");

        Assert.True(queue.TryDequeue("display2", out var display2Message));
        Assert.Equal("two", display2Message);
        Assert.True(queue.TryDequeue("display1", out var display1Message));
        Assert.Equal("one", display1Message);
    }

    [Fact]
    public void TryEnqueue_RejectsNewestMessageWhenQueueIsFull()
    {
        var queue = new DisplayMessageQueue();
        for (var index = 0; index < DisplayMessageQueue.MaximumQueueDepth; index++)
        {
            Assert.Equal(DisplayMessageEnqueueResult.Enqueued, queue.TryEnqueue("display1", index.ToString()));
        }

        Assert.Equal(DisplayMessageEnqueueResult.QueueFull, queue.TryEnqueue("display1", "rejected"));

        for (var index = 0; index < DisplayMessageQueue.MaximumQueueDepth; index++)
        {
            Assert.True(queue.TryDequeue("display1", out var text));
            Assert.Equal(index.ToString(), text);
        }
    }

    [Fact]
    public void TryEnqueue_EnforcesUtf8ByteLimit()
    {
        var queue = new DisplayMessageQueue();

        Assert.Equal(
            DisplayMessageEnqueueResult.Enqueued,
            queue.TryEnqueue("display1", new string('å', DisplayMessageQueue.MaximumMessageBytes / 2)));
        Assert.Equal(
            DisplayMessageEnqueueResult.MessageTooLong,
            queue.TryEnqueue("display2", new string('å', DisplayMessageQueue.MaximumMessageBytes / 2 + 1)));
    }

    [Fact]
    public async Task TryDequeue_ConcurrentlyConsumesEveryMessageOnce()
    {
        var queue = new DisplayMessageQueue();
        var expected = Enumerable.Range(0, DisplayMessageQueue.MaximumQueueDepth)
            .Select(index => index.ToString())
            .ToArray();
        foreach (var message in expected)
        {
            queue.TryEnqueue("display1", message);
        }

        var consumed = new ConcurrentBag<string>();
        var consumers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (queue.TryDequeue("display1", out var text))
            {
                consumed.Add(text!);
            }
        }));
        await Task.WhenAll(consumers);

        Assert.Equal(expected.Order(), consumed.Order());
        Assert.False(queue.TryDequeue("display1", out _));
    }
}
