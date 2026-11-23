using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;

namespace ProtoDeck.Services;

public sealed record CallOutcome(StatusCode Code, string Detail, Metadata Headers, Metadata Trailers);

/// <summary>One-shot calls (unary, server streaming) over raw byte marshallers.</summary>
public static class DynamicInvoker
{
    internal static readonly Marshaller<byte[]> Raw = Marshallers.Create<byte[]>(static b => b, static b => b);

    internal static CallOutcome Failure(RpcException e) =>
        new(e.StatusCode, e.Status.Detail, new Metadata(), e.Trailers);

    internal static string Render(byte[] bytes, MessageDescriptor output)
    {
        try
        {
            return ProtoJsonCodec.Decode(bytes, output).ToJsonString(ProtoJsonCodec.Pretty);
        }
        catch (Exception e)
        {
            return $"// decode error: {e.Message}\n// raw: {Convert.ToHexString(bytes)}";
        }
    }

    /// <param name="onMessage">Called once per response message, possibly from a background thread.</param>
    public static async Task<CallOutcome> InvokeAsync(
        GrpcChannel channel,
        MethodDescriptor md,
        string body,
        Metadata headers,
        TimeSpan? timeout,
        Action<string> onMessage,
        CancellationToken ct)
    {
        if (md.IsClientStreaming)
            throw new InvalidOperationException("Client-streaming methods use CallSession.");

        var request = ProtoJsonCodec.EncodeRequests(md.InputType, body, allowMany: false)[0];
        var type = md.IsServerStreaming ? MethodType.ServerStreaming : MethodType.Unary;
        var method = new Method<byte[], byte[]>(type, md.Service.FullName, md.Name, Raw, Raw);
        DateTime? deadline = timeout is { } t ? DateTime.UtcNow + t : null;
        var options = new CallOptions(headers, deadline, ct);
        var output = md.OutputType;
        var invoker = channel.CreateCallInvoker();

        try
        {
            if (type == MethodType.Unary)
            {
                using var call = invoker.AsyncUnaryCall(method, null, options, request);
                onMessage(Render(await call.ResponseAsync, output));
                return new CallOutcome(StatusCode.OK, "", await call.ResponseHeadersAsync, call.GetTrailers());
            }
            else
            {
                using var call = invoker.AsyncServerStreamingCall(method, null, options, request);
                await foreach (var message in call.ResponseStream.ReadAllAsync(ct))
                    onMessage(Render(message, output));
                return new CallOutcome(StatusCode.OK, "", await call.ResponseHeadersAsync, call.GetTrailers());
            }
        }
        catch (RpcException e)
        {
            return Failure(e);
        }
    }
}

/// <summary>
/// An open client-streaming or bidirectional call. Send messages one at a time, half-close with
/// <see cref="CompleteAsync"/>, or abort with <see cref="Cancel"/>. <see cref="Completion"/> never throws.
/// </summary>
public sealed class CallSession : IDisposable
{
    private readonly MethodDescriptor _method;
    private readonly IClientStreamWriter<byte[]> _writer;
    private readonly IDisposable _call;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Task<CallOutcome> Completion { get; }

    private CallSession(MethodDescriptor method, IClientStreamWriter<byte[]> writer, IDisposable call,
        CancellationTokenSource cts, Task<CallOutcome> completion)
    {
        _method = method;
        _writer = writer;
        _call = call;
        _cts = cts;
        Completion = completion;
    }

    public static CallSession Start(GrpcChannel channel, MethodDescriptor md, Metadata headers,
        TimeSpan? timeout, Action<string> onMessage, CancellationToken ct)
    {
        if (!md.IsClientStreaming)
            throw new InvalidOperationException("Only client-streaming and bidirectional methods open a session.");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var type = md.IsServerStreaming ? MethodType.DuplexStreaming : MethodType.ClientStreaming;
        var method = new Method<byte[], byte[]>(type, md.Service.FullName, md.Name, DynamicInvoker.Raw, DynamicInvoker.Raw);
        DateTime? deadline = timeout is { } t ? DateTime.UtcNow + t : null;
        var options = new CallOptions(headers, deadline, token);
        var invoker = channel.CreateCallInvoker();
        var output = md.OutputType;

        if (md.IsServerStreaming)
        {
            var call = invoker.AsyncDuplexStreamingCall(method, null, options);
            var completion = Run(async () =>
            {
                await foreach (var message in call.ResponseStream.ReadAllAsync(token))
                    onMessage(DynamicInvoker.Render(message, output));
                return new CallOutcome(StatusCode.OK, "", await call.ResponseHeadersAsync, call.GetTrailers());
            });
            return new CallSession(md, call.RequestStream, call, cts, completion);
        }
        else
        {
            var call = invoker.AsyncClientStreamingCall(method, null, options);
            var completion = Run(async () =>
            {
                onMessage(DynamicInvoker.Render(await call.ResponseAsync, output));
                return new CallOutcome(StatusCode.OK, "", await call.ResponseHeadersAsync, call.GetTrailers());
            });
            return new CallSession(md, call.RequestStream, call, cts, completion);
        }
    }

    private static Task<CallOutcome> Run(Func<Task<CallOutcome>> body) => Task.Run(async () =>
    {
        try { return await body(); }
        catch (RpcException e) { return DynamicInvoker.Failure(e); }
        catch (OperationCanceledException) { return new CallOutcome(StatusCode.Cancelled, "Cancelled", new Metadata(), new Metadata()); }
        catch (Exception e) { return new CallOutcome(StatusCode.Unknown, e.Message, new Metadata(), new Metadata()); }
    });

    /// <summary>Encodes one JSON message and writes it to the open stream.</summary>
    public async Task SendAsync(string json)
    {
        var bytes = ProtoJsonCodec.EncodeRequests(_method.InputType, json, allowMany: false)[0];
        await _writeLock.WaitAsync();
        try { await _writer.WriteAsync(bytes); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Half-close: no more requests; the server may still send responses.</summary>
    public async Task CompleteAsync()
    {
        await _writeLock.WaitAsync();
        try { await _writer.CompleteAsync(); }
        finally { _writeLock.Release(); }
    }

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _call.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
