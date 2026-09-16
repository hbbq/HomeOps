using System.Text;

namespace HomeOps.Api.Displays;

public sealed class DisplayMessageQueue
{
    public const int MaximumMessageBytes = 1024;
    public const int MaximumQueueDepth = 20;

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<string>> _queues = new(StringComparer.Ordinal);

    public DisplayMessageEnqueueResult TryEnqueue(string displayId, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumMessageBytes)
        {
            return DisplayMessageEnqueueResult.MessageTooLong;
        }

        lock (_lock)
        {
            if (!_queues.TryGetValue(displayId, out var queue))
            {
                queue = new Queue<string>();
                _queues.Add(displayId, queue);
            }

            if (queue.Count >= MaximumQueueDepth)
            {
                return DisplayMessageEnqueueResult.QueueFull;
            }

            queue.Enqueue(text);
            return DisplayMessageEnqueueResult.Enqueued;
        }
    }

    public bool TryDequeue(string displayId, out string? text)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(displayId, out var queue) || !queue.TryDequeue(out text))
            {
                text = null;
                return false;
            }

            if (queue.Count == 0)
            {
                _queues.Remove(displayId);
            }

            return true;
        }
    }
}

public enum DisplayMessageEnqueueResult
{
    Enqueued,
    QueueFull,
    MessageTooLong
}
