using System.Buffers;
using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Api;

/// <summary>
/// Full-duplex encrypted WhatsApp Noise transport actor.
/// Serializes outbound encryption counters through a single <see cref="Channel{T}"/> writer loop
/// (with opportunistic multi-frame batching and poison-on-write-failure safety), unpacks inbound binary stanzas,
/// and tracks a monotonic <see cref="Generation"/> ID to fence off stale disconnect callbacks.
/// </summary>
public sealed class NoiseSessionSocket : IAsyncDisposable
{
    public const int MaxBatchFrames = 16;
    public const int MaxBatchWireBytes = 64 * 1024;
    private const int OutboundQueueCapacity = 32;
    private const int ReceiveBufferSize = 4096;

    private sealed class OutboundJob
    {
        private readonly CancellationTokenRegistration _cancelReg;
        private int _claimed;

        public OutboundJob(byte[] plaintext, CancellationToken callerToken)
        {
            Plaintext = plaintext;
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (callerToken.CanBeCanceled)
            {
                _cancelReg = callerToken.Register(static s =>
                {
                    var job = (OutboundJob)s!;
                    if (Interlocked.CompareExchange(ref job._claimed, 2, 0) == 0)
                    {
                        job.Completion.TrySetCanceled();
                    }
                }, this);
            }
        }

        public byte[] Plaintext { get; }
        public TaskCompletionSource Completion { get; }

        /// <summary>
        /// Claims the job prior to encrypting so a late caller cancellation cannot skip an already-assigned nonce.
        /// </summary>
        public bool TryClaimForSend() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

        public void DisposeRegistration() => _cancelReg.Dispose();
    }

    private readonly ITransport _transport;
    private readonly NoiseSessionCodec _codec;
    private readonly Channel<OutboundJob> _outboundChannel;
    private readonly Channel<Node> _inboundChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _writerTask;
    private readonly Task _readerTask;
    private int _disposed;

    public NoiseSessionSocket(
        long generation,
        ITransport transport,
        NoiseKeys keys,
        ReadOnlySpan<byte> initialRemainder = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _codec = new NoiseSessionCodec(keys ?? throw new ArgumentNullException(nameof(keys)), initialRemainder);

        _outboundChannel = Channel.CreateBounded<OutboundJob>(new BoundedChannelOptions(OutboundQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _inboundChannel = Channel.CreateUnbounded<Node>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

        _writerTask = Task.Run(WriterLoopAsync);
        _readerTask = Task.Run(ReaderLoopAsync);
    }

    public long Generation { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _transport.IsConnected && !_codec.IsEncoderPoisoned;
    public bool IsWritePoisoned => _codec.IsEncoderPoisoned;
    public uint WriteCounter => _codec.WriteCounter;
    public uint ReadCounter => _codec.ReadCounter;
    public ChannelReader<Node> InboundNodes => _inboundChannel.Reader;

    public Task SendNodeAsync(Node node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] packed = BinaryPacker.Pack(encoder.ToArray());
        return SendRawAsync(packed, ct);
    }

    public async Task SendRawAsync(byte[] plaintext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (_codec.IsEncoderPoisoned)
        {
            throw new InvalidOperationException("Noise write key is poisoned after a failed transport write.");
        }

        var job = new OutboundJob(plaintext, ct);
        try
        {
            await _outboundChannel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            job.DisposeRegistration();
            throw new InvalidOperationException("NoiseSessionSocket outbound channel is closed.", ex);
        }
        catch (OperationCanceledException)
        {
            job.DisposeRegistration();
            throw;
        }

        await job.Completion.Task.ConfigureAwait(false);
    }

    private async Task WriterLoopAsync()
    {
        var outBuffer = new ArrayBufferWriter<byte>(4096);
        var activeWaiters = new List<OutboundJob>(MaxBatchFrames);
        Exception? fatalError = null;

        try
        {
            while (await _outboundChannel.Reader.WaitToReadAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                if (!TryDequeueClaimedJob(out OutboundJob? firstJob))
                {
                    continue;
                }

                if (_codec.IsEncoderPoisoned)
                {
                    firstJob.Completion.TrySetException(
                        fatalError ?? new InvalidOperationException("Noise write key is poisoned."));
                    firstJob.DisposeRegistration();
                    continue;
                }

                outBuffer.Clear();
                activeWaiters.Clear();

                EncryptBatchIntoBuffer(firstJob, outBuffer, activeWaiters);
                if (activeWaiters.Count == 0)
                {
                    continue;
                }

                try
                {
                    await _transport.SendAsync(outBuffer.WrittenMemory, _lifetimeCts.Token).ConfigureAwait(false);
                    foreach (var waiter in activeWaiters)
                    {
                        waiter.Completion.TrySetResult();
                        waiter.DisposeRegistration();
                    }
                }
                catch (Exception ex)
                {
                    // Ambiguous transport write: peer may or may not have advanced its counter.
                    // Permanently retire the write key and fail every caller waiting on this batch.
                    _codec.PoisonEncoder();
                    fatalError = ex;
                    foreach (var waiter in activeWaiters)
                    {
                        waiter.Completion.TrySetException(ex);
                        waiter.DisposeRegistration();
                    }
                    DrainRemainingOnFailure(ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            DrainRemainingOnFailure(new OperationCanceledException("NoiseSessionSocket closed."));
        }
        catch (Exception ex)
        {
            _codec.PoisonEncoder();
            DrainRemainingOnFailure(ex);
        }
    }

    private bool TryDequeueClaimedJob(out OutboundJob job)
    {
        while (_outboundChannel.Reader.TryRead(out OutboundJob? candidate))
        {
            if (candidate.TryClaimForSend())
            {
                job = candidate;
                return true;
            }
            candidate.DisposeRegistration();
        }

        job = null!;
        return false;
    }

    private void EncryptBatchIntoBuffer(
        OutboundJob firstJob,
        ArrayBufferWriter<byte> outBuffer,
        List<OutboundJob> activeWaiters)
    {
        OutboundJob? current = firstJob;
        while (current is not null)
        {
            try
            {
                _codec.EncryptFrameInto(current.Plaintext, outBuffer);
                activeWaiters.Add(current);
            }
            catch (Exception ex)
            {
                current.Completion.TrySetException(ex);
                current.DisposeRegistration();
                break;
            }

            if (activeWaiters.Count >= MaxBatchFrames || outBuffer.WrittenCount >= MaxBatchWireBytes)
            {
                break;
            }

            if (!_outboundChannel.Reader.TryPeek(out OutboundJob? next))
            {
                break;
            }

            int projectedSize = outBuffer.WrittenCount + 3 + next.Plaintext.Length + NoiseCipher.TagLength;
            if (projectedSize > MaxBatchWireBytes && activeWaiters.Count > 0)
            {
                break;
            }

            current = TryDequeueClaimedJob(out OutboundJob? dequeued) ? dequeued : null;
        }
    }

    private void DrainRemainingOnFailure(Exception error)
    {
        while (_outboundChannel.Reader.TryRead(out OutboundJob? pending))
        {
            pending.Completion.TrySetException(error);
            pending.DisposeRegistration();
        }
    }

    private async Task ReaderLoopAsync()
    {
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        Exception? readerError = null;
        try
        {
            // First drain any complete frames already coalesced right after ServerHello.
            await DrainDecryptedFramesAsync().ConfigureAwait(false);

            while (!_lifetimeCts.IsCancellationRequested)
            {
                int bytesRead = await _transport.ReceiveAsync(readBuffer, _lifetimeCts.Token).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                _codec.Feed(readBuffer.AsSpan(0, bytesRead));
                await DrainDecryptedFramesAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            readerError = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            _inboundChannel.Writer.TryComplete(readerError);
        }
    }

    private async Task DrainDecryptedFramesAsync()
    {
        while (_codec.TryDecryptNextFrame(out byte[] plainFrame))
        {
            byte[] unpacked = BinaryPacker.Unpack(plainFrame);
            Node node = new BinaryDecoder(unpacked).ReadNode();
            await _inboundChannel.Writer.WriteAsync(node, _lifetimeCts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outboundChannel.Writer.TryComplete();
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_writerTask, _readerTask).ConfigureAwait(false);
        }
        catch
        {
            // Suppress expected shutdown cancellation/transport teardown errors.
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _codec.Dispose();
        _lifetimeCts.Dispose();
    }
}
