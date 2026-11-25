using System.Collections.Concurrent;
using System.Globalization;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Api;

/// <summary>
/// Exception thrown when an IQ request receives an <c>&lt;iq type="error"&gt;</c>
/// or an unexpected response type, preserving parsed error metadata and the full stanza.
/// </summary>
public sealed class IqException : Exception
{
    public string RequestId { get; }
    public int? ErrorCode { get; }
    public string? ErrorText { get; }
    public string? ErrorType { get; }
    public string? UnexpectedResponseType { get; }
    public Node? ResponseStanza { get; }

    public IqException(
        string requestId,
        int? errorCode,
        string? errorText,
        string? errorType,
        Node? responseStanza,
        string? unexpectedResponseType = null)
        : base(BuildMessage(requestId, errorCode, errorText, unexpectedResponseType))
    {
        RequestId = requestId;
        ErrorCode = errorCode;
        ErrorText = errorText;
        ErrorType = errorType;
        ResponseStanza = responseStanza;
        UnexpectedResponseType = unexpectedResponseType;
    }

    private static string BuildMessage(string requestId, int? code, string? text, string? unexpectedType)
    {
        if (!string.IsNullOrEmpty(unexpectedType))
        {
            return $"IQ '{requestId}' received unexpected response type '{unexpectedType}'.";
        }
        return code.HasValue
            ? $"IQ '{requestId}' failed with server error {code.Value}: {text ?? "unknown"}."
            : $"IQ '{requestId}' failed with server error: {text ?? "unknown"}.";
    }
}

/// <summary>
/// Correlated <c>&lt;iq id="..."&gt;</c> request/response table matching <c>whatsapp-rust</c>'s
/// pre-send waiter registration, duplicate-ID rejection, and guaranteed cleanup invariants.
/// </summary>
public sealed class IqClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(75);

    private readonly NoiseSessionSocket _socket;
    private readonly ConcurrentDictionary<string, WaiterEntry> _waiters = new(StringComparer.Ordinal);
    private long _nextId;
    private volatile Exception? _closedReason;

    private sealed class WaiterEntry
    {
        public TaskCompletionSource<Node> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public IqClient(NoiseSessionSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public int PendingCount => _waiters.Count;

    public string NextRequestId()
    {
        long seq = Interlocked.Increment(ref _nextId);
        return $"iq-{_socket.Generation}-{seq.ToString(CultureInfo.InvariantCulture)}";
    }

    public async Task<Node> SendIqAsync(
        Node iqNode,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(iqNode);

        if (!string.Equals(iqNode.Tag, "iq", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected <iq> stanza, got <{iqNode.Tag}>.", nameof(iqNode));
        }

        if (_closedReason is { } closed)
        {
            throw new OperationCanceledException("IQ client is closed because the session ended.", closed);
        }

        string? id = iqNode.GetAttr("id");
        Node nodeToSend = iqNode;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = NextRequestId();
            var attrs = new Dictionary<string, NodeValue>(iqNode.Attrs, StringComparer.Ordinal)
            {
                ["id"] = id
            };
            nodeToSend = new Node(iqNode.Tag, attrs, iqNode.Content);
        }

        var waiter = new WaiterEntry();
        if (!_waiters.TryAdd(id, waiter))
        {
            throw new InvalidOperationException($"Duplicate in-flight IQ request id '{id}'.");
        }

        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        CancellationTokenRegistration cancelReg = default;

        try
        {
            TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
            if (effectiveTimeout > TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
            {
                timeoutCts = new CancellationTokenSource(effectiveTimeout);
                linkedCts = ct.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                    : null;
            }

            CancellationToken effectiveToken = linkedCts?.Token ?? timeoutCts?.Token ?? ct;
            if (effectiveToken.CanBeCanceled)
            {
                cancelReg = effectiveToken.Register(static state =>
                {
                    var (owner, reqId, entry) = ((IqClient, string, WaiterEntry))state!;
                    owner._waiters.TryRemove(new KeyValuePair<string, WaiterEntry>(reqId, entry));
                    entry.Tcs.TrySetCanceled();
                }, (this, id, waiter));
            }

            // Re-check closed state after waiter registration so CancelAll races cannot leave a stranded waiter.
            if (_closedReason is { } closedAfterAdd)
            {
                throw new OperationCanceledException("IQ client is closed because the session ended.", closedAfterAdd);
            }

            // Register before wire write so immediate responses cannot arrive before the waiter exists.
            await _socket.SendNodeAsync(nodeToSend, effectiveToken).ConfigureAwait(false);

            try
            {
                return await waiter.Tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"IQ '{id}' timed out after {effectiveTimeout.TotalSeconds:0.###}s.");
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
        finally
        {
            cancelReg.Dispose();
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
            _waiters.TryRemove(new KeyValuePair<string, WaiterEntry>(id, waiter));
        }
    }

    /// <summary>
    /// Attempts to match an inbound <c>&lt;iq&gt;</c> response (<c>result</c> / <c>error</c>)
    /// to an in-flight waiter. Server-initiated <c>get</c>/<c>set</c> IQs return <c>false</c>.
    /// </summary>
    public bool TryResolve(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!string.Equals(node.Tag, "iq", StringComparison.Ordinal))
        {
            return false;
        }

        string? type = node.GetAttr("type");
        if (string.Equals(type, "get", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "set", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? id = node.GetAttr("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (!_waiters.TryRemove(id, out WaiterEntry? waiter))
        {
            return false;
        }

        if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
        {
            waiter.Tcs.TrySetResult(node);
            return true;
        }

        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            Node? errorNode = node.GetChild("error");
            int? code = null;
            if (int.TryParse(errorNode?.GetAttr("code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCode))
            {
                code = parsedCode;
            }
            string? text = errorNode?.GetAttr("text");
            string? errType = errorNode?.GetAttr("type");
            waiter.Tcs.TrySetException(new IqException(id, code, text, errType, node));
            return true;
        }

        waiter.Tcs.TrySetException(new IqException(id, null, null, null, node, unexpectedResponseType: type ?? "<missing>"));
        return true;
    }

    public void CancelAll(Exception? reason = null)
    {
        Exception effective = reason ?? new OperationCanceledException("IQ client session closed.");
        _closedReason = effective;

        foreach (string key in _waiters.Keys)
        {
            if (_waiters.TryRemove(key, out WaiterEntry? waiter))
            {
                waiter.Tcs.TrySetException(effective);
            }
        }
    }
}
