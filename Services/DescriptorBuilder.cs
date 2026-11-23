using Google.Protobuf;
using Google.Protobuf.Reflection;
using WKT = Google.Protobuf.WellKnownTypes;

namespace ProtoDeck.Services;

/// <summary>Turns a set of FileDescriptorProtos (from reflection or parsed .proto files) into live descriptors.</summary>
public static class DescriptorBuilder
{
    private static readonly Dictionary<string, FileDescriptor> BuiltIn = new[]
    {
        DescriptorReflection.Descriptor,
        WKT.AnyReflection.Descriptor,
        WKT.ApiReflection.Descriptor,
        WKT.DurationReflection.Descriptor,
        WKT.EmptyReflection.Descriptor,
        WKT.FieldMaskReflection.Descriptor,
        WKT.SourceContextReflection.Descriptor,
        WKT.StructReflection.Descriptor,
        WKT.TimestampReflection.Descriptor,
        WKT.TypeReflection.Descriptor,
        WKT.WrappersReflection.Descriptor,
    }.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public static bool TryGetBuiltIn(string name, out FileDescriptorProto proto)
    {
        if (BuiltIn.TryGetValue(name, out var fd))
        {
            proto = FileDescriptorProto.Parser.ParseFrom(fd.SerializedData);
            return true;
        }
        proto = null!;
        return false;
    }

    /// <summary>Closes the set over well-known files, orders it topologically and builds it.</summary>
    public static IReadOnlyList<FileDescriptor> Build(Dictionary<string, FileDescriptorProto> files)
    {
        foreach (var name in files.Keys.Where(BuiltIn.ContainsKey).ToList())
        {
            TryGetBuiltIn(name, out var builtIn);
            files[name] = builtIn;
        }

        while (true)
        {
            var missing = files
                .Values.SelectMany(f => f.Dependency)
                .Where(d => !files.ContainsKey(d))
                .Distinct()
                .ToList();
            if (missing.Count == 0)
                break;

            foreach (var dep in missing)
            {
                if (!TryGetBuiltIn(dep, out var known))
                    throw new InvalidOperationException(
                        $"Missing dependency '{dep}'. Add its folder to Import paths."
                    );
                files[dep] = known;
            }
        }

        return FileDescriptor.BuildFromByteStrings(
            Order(files).Select(p => p.ToByteString()).ToList()
        );
    }

    private static List<FileDescriptorProto> Order(Dictionary<string, FileDescriptorProto> files)
    {
        var result = new List<FileDescriptorProto>(files.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string name)
        {
            if (!visited.Add(name))
                return;
            var proto = files[name];
            foreach (var dep in proto.Dependency)
                Visit(dep);
            result.Add(proto);
        }

        foreach (var name in files.Keys.ToList())
            Visit(name);
        return result;
    }
}
