using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using ProtoDeck.Reflection;

namespace ProtoDeck.Services;

/// <summary>Server reflection (v1, falling back to v1alpha) → live <see cref="ServiceDescriptor"/>s.</summary>
public static class ReflectionClient
{
    private const string V1 = "grpc.reflection.v1.ServerReflection";
    private const string V1Alpha = "grpc.reflection.v1alpha.ServerReflection";

    private static readonly Marshaller<ServerReflectionRequest> RequestMarshaller =
        Marshallers.Create<ServerReflectionRequest>(r => r.ToByteArray(), b => ServerReflectionRequest.Parser.ParseFrom(b));

    private static readonly Marshaller<ServerReflectionResponse> ResponseMarshaller =
        Marshallers.Create<ServerReflectionResponse>(r => r.ToByteArray(), b => ServerReflectionResponse.Parser.ParseFrom(b));

    public static async Task<IReadOnlyList<ServiceDescriptor>> LoadAsync(GrpcChannel channel, CancellationToken ct)
    {
        try
        {
            return await LoadAsync(channel, V1, ct);
        }
        catch (Exception v1) when (v1 is not OperationCanceledException)
        {
            try
            {
                return await LoadAsync(channel, V1Alpha, ct);
            }
            catch (Exception alpha) when (alpha is not OperationCanceledException)
            {
                throw new InvalidOperationException($"v1: {v1.Message} | v1alpha: {alpha.Message}", alpha);
            }
        }
    }

    private static async Task<IReadOnlyList<ServiceDescriptor>> LoadAsync(GrpcChannel channel, string serviceName, CancellationToken ct)
    {
        var method = new Method<ServerReflectionRequest, ServerReflectionResponse>(
            MethodType.DuplexStreaming, serviceName, "ServerReflectionInfo", RequestMarshaller, ResponseMarshaller);

        using var call = channel.CreateCallInvoker()
            .AsyncDuplexStreamingCall(method, null, new CallOptions(cancellationToken: ct));

        async Task<ServerReflectionResponse> Ask(ServerReflectionRequest request)
        {
            await call.RequestStream.WriteAsync(request);
            if (!await call.ResponseStream.MoveNext(ct))
                throw new InvalidOperationException("Reflection stream closed by server.");

            var response = call.ResponseStream.Current;
            if (response.MessageResponseCase == ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse)
                throw new InvalidOperationException(
                    $"Reflection error {response.ErrorResponse.ErrorCode}: {response.ErrorResponse.ErrorMessage}");
            return response;
        }

        var listed = await Ask(new ServerReflectionRequest { ListServices = "*" });
        var serviceNames = listed.ListServicesResponse.Service
            .Select(s => s.Name)
            .Where(n => !n.StartsWith("grpc.reflection.", StringComparison.Ordinal))
            .ToList();

        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);

        void Absorb(ServerReflectionResponse response)
        {
            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                var proto = FileDescriptorProto.Parser.ParseFrom(bytes);
                files.TryAdd(proto.Name, proto);
            }
        }

        foreach (var name in serviceNames)
            Absorb(await Ask(new ServerReflectionRequest { FileContainingSymbol = name }));

        while (true)
        {
            var missing = files.Values.SelectMany(f => f.Dependency).Where(d => !files.ContainsKey(d)).Distinct().ToList();
            if (missing.Count == 0) break;

            foreach (var dep in missing)
            {
                if (DescriptorBuilder.TryGetBuiltIn(dep, out var known))
                {
                    files[dep] = known;
                    continue;
                }

                Absorb(await Ask(new ServerReflectionRequest { FileByFilename = dep }));
                if (!files.ContainsKey(dep))
                    throw new InvalidOperationException($"Server did not return dependency '{dep}'.");
            }
        }

        try { await call.RequestStream.CompleteAsync(); } catch (Exception) { /* ignored */ }

        var wanted = serviceNames.ToHashSet(StringComparer.Ordinal);
        return DescriptorBuilder.Build(files)
            .SelectMany(f => f.Services)
            .Where(s => wanted.Contains(s.FullName))
            .OrderBy(s => s.FullName, StringComparer.Ordinal)
            .ToList();
    }
}
