extern alias protobufnet;

using Google.Protobuf.Reflection;
using PB = protobufnet::Google.Protobuf.Reflection;

namespace ProtoDeck.Services;

/// <summary>Parses .proto files (no server needed) with protobuf-net.Reflection and builds service descriptors.</summary>
public static class ProtoFileLoader
{
    public static IReadOnlyList<ServiceDescriptor> Load(IReadOnlyList<string> protoFiles, IEnumerable<string> extraImportPaths)
    {
        var set = new PB.FileDescriptorSet();

        var paths = new List<string>();
        foreach (var file in protoFiles)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            paths.Add(dir);
            if (Directory.GetParent(dir) is { } parent) paths.Add(parent.FullName); // for imports like "common/x.proto"
        }
        paths.AddRange(extraImportPaths);

        foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
            set.AddImportPath(path);

        foreach (var file in protoFiles)
            set.Add(Path.GetFileName(file), includeInOutput: true);

        set.Process();

        var errors = set.GetErrors().Where(e => e.IsError).Select(e => e.ToString()).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        foreach (var file in set.Files)
        {
            using var ms = new MemoryStream();
            protobufnet::ProtoBuf.Serializer.Serialize(ms, file);
            var proto = FileDescriptorProto.Parser.ParseFrom(ms.ToArray());
            files[proto.Name] = proto;
        }

        var services = DescriptorBuilder.Build(files)
            .SelectMany(f => f.Services)
            .OrderBy(s => s.FullName, StringComparer.Ordinal)
            .ToList();

        if (services.Count == 0) throw new InvalidOperationException("No services found in the selected .proto files.");
        return services;
    }
}
