using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ProtoDeck.Services;

/// <summary>
/// Schema-driven JSON &lt;-&gt; protobuf wire codec. Works from reflected descriptors, so no generated types are needed.
/// Follows proto3 JSON conventions: 64-bit ints as strings, bytes as base64, enums by name,
/// Timestamp as RFC 3339, Duration as "1.5s", wrapper types as bare scalars.
/// </summary>
public static class ProtoJsonCodec
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "google.protobuf.DoubleValue", "google.protobuf.FloatValue", "google.protobuf.Int64Value",
        "google.protobuf.UInt64Value", "google.protobuf.Int32Value", "google.protobuf.UInt32Value",
        "google.protobuf.BoolValue", "google.protobuf.StringValue", "google.protobuf.BytesValue"
    };

    // ───────────────────────── public API ─────────────────────────

    /// <summary>Encodes the editor text. Client-streaming methods accept a JSON array (one message per element).</summary>
    public static List<byte[]> EncodeRequests(MessageDescriptor type, string json, bool allowMany)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, Lenient);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return [EncodeMessage(type, root)];

        if (!allowMany)
            throw new FormatException("This method takes one request message. Use a JSON object, not an array.");

        return root.EnumerateArray().Select(e => EncodeMessage(type, e)).ToList();
    }

    public static byte[] EncodeMessage(MessageDescriptor type, JsonElement json) =>
        Build(o => WriteMessage(o, type, json));

    public static JsonNode Decode(byte[] data, MessageDescriptor type) => DecodeMessage(data, type);

    /// <summary>Skeleton request with every field present at its default value.</summary>
    public static JsonNode Template(MessageDescriptor type, HashSet<string>? stack = null)
    {
        switch (type.FullName)
        {
            case "google.protobuf.Timestamp": return S("1970-01-01T00:00:00Z");
            case "google.protobuf.Duration": return S("0s");
        }
        if (Wrappers.Contains(type.FullName)) return DefaultValue(type.FindFieldByNumber(1));

        var obj = new JsonObject();
        stack ??= [];
        if (!stack.Add(type.FullName)) return obj; // recursive type: stop

        var seenOneofs = new HashSet<OneofDescriptor>();
        foreach (var f in type.Fields.InDeclarationOrder())
        {
            if (f.RealContainingOneof is { } oneof && !seenOneofs.Add(oneof)) continue; // first member of each oneof only

            JsonNode node;
            if (f.IsMap) node = new JsonObject();
            else if (f.IsRepeated) node = new JsonArray();
            else if (f.FieldType == FieldType.Message) node = Template(f.MessageType, stack);
            else node = DefaultValue(f);
            obj[f.JsonName] = node;
        }

        stack.Remove(type.FullName);
        return obj;
    }

    // ───────────────────────── encoding ─────────────────────────

    private static byte[] Build(Action<CodedOutputStream> write)
    {
        using var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        write(o);
        o.Flush();
        return ms.ToArray();
    }

    private static void WriteMessage(CodedOutputStream o, MessageDescriptor type, JsonElement json)
    {
        var name = type.FullName;

        if (name == "google.protobuf.Timestamp")
        {
            var t = DateTimeOffset.Parse(Str(json), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            WriteSecondsNanos(o, t.ToUnixTimeSeconds(), (int)(t.UtcTicks % TimeSpan.TicksPerSecond * 100));
            return;
        }

        if (name == "google.protobuf.Duration")
        {
            var s = Str(json).Trim();
            if (!s.EndsWith('s')) throw new FormatException("Duration must look like \"1.5s\".");
            var d = decimal.Parse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture);
            var secs = (long)decimal.Truncate(d);
            WriteSecondsNanos(o, secs, (int)((d - secs) * 1_000_000_000m));
            return;
        }

        if (Wrappers.Contains(name))
        {
            var inner = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("value", out var v) ? v : json;
            WriteSingular(o, type.FindFieldByNumber(1), inner);
            return;
        }

        if (json.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Expected a JSON object for {name}, got {json.ValueKind}.");

        foreach (var p in json.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Null) continue;

            var f = type.Fields.InDeclarationOrder().FirstOrDefault(x => x.JsonName == p.Name || x.Name == p.Name)
                    ?? throw new FormatException($"Unknown field '{p.Name}' in {name}.");
            try
            {
                WriteField(o, f, p.Value);
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or InvalidOperationException)
            {
                throw new FormatException($"{f.Name}: {e.Message}", e);
            }
        }
    }

    private static void WriteSecondsNanos(CodedOutputStream o, long secs, int nanos)
    {
        if (secs != 0) { o.WriteTag(1, WireFormat.WireType.Varint); o.WriteInt64(secs); }
        if (nanos != 0) { o.WriteTag(2, WireFormat.WireType.Varint); o.WriteInt32(nanos); }
    }

    private static void WriteField(CodedOutputStream o, FieldDescriptor f, JsonElement v)
    {
        if (f.IsMap)
        {
            if (v.ValueKind != JsonValueKind.Object) throw new FormatException("expected an object (map).");
            var kf = f.MessageType.FindFieldByNumber(1);
            var vf = f.MessageType.FindFieldByNumber(2);
            foreach (var p in v.EnumerateObject())
            {
                var key = JsonSerializer.SerializeToElement(p.Name);
                var value = p.Value;
                var entry = Build(eo => { WriteSingular(eo, kf, key); WriteSingular(eo, vf, value); });
                o.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                o.WriteBytes(ByteString.CopyFrom(entry));
            }
            return;
        }

        if (f.IsRepeated)
        {
            if (v.ValueKind != JsonValueKind.Array) throw new FormatException("expected an array.");
            if (f.IsPacked)
            {
                var packed = Build(po => { foreach (var item in v.EnumerateArray()) WriteScalar(po, f, item); });
                if (packed.Length == 0) return;
                o.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                o.WriteBytes(ByteString.CopyFrom(packed));
            }
            else
            {
                foreach (var item in v.EnumerateArray()) WriteSingular(o, f, item);
            }
            return;
        }

        WriteSingular(o, f, v);
    }

    private static void WriteSingular(CodedOutputStream o, FieldDescriptor f, JsonElement v)
    {
        switch (f.FieldType)
        {
            case FieldType.Message:
                o.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                o.WriteBytes(ByteString.CopyFrom(EncodeMessage(f.MessageType, v)));
                return;
            case FieldType.Group:
                throw new NotSupportedException("Group fields are not supported.");
            default:
                o.WriteTag(f.FieldNumber, WireTypeOf(f.FieldType));
                WriteScalar(o, f, v);
                return;
        }
    }

    private static void WriteScalar(CodedOutputStream o, FieldDescriptor f, JsonElement v)
    {
        switch (f.FieldType)
        {
            case FieldType.Double: o.WriteDouble(ToDouble(v)); break;
            case FieldType.Float: o.WriteFloat((float)ToDouble(v)); break;
            case FieldType.Int64: o.WriteInt64(ToLong(v)); break;
            case FieldType.UInt64: o.WriteUInt64(ToULong(v)); break;
            case FieldType.Int32: o.WriteInt32(checked((int)ToLong(v))); break;
            case FieldType.Fixed64: o.WriteFixed64(ToULong(v)); break;
            case FieldType.Fixed32: o.WriteFixed32(checked((uint)ToULong(v))); break;
            case FieldType.Bool: o.WriteBool(ToBool(v)); break;
            case FieldType.String: o.WriteString(Str(v)); break;
            case FieldType.Bytes: o.WriteBytes(ByteString.FromBase64(Str(v))); break;
            case FieldType.UInt32: o.WriteUInt32(checked((uint)ToULong(v))); break;
            case FieldType.SFixed32: o.WriteSFixed32(checked((int)ToLong(v))); break;
            case FieldType.SFixed64: o.WriteSFixed64(ToLong(v)); break;
            case FieldType.SInt32: o.WriteSInt32(checked((int)ToLong(v))); break;
            case FieldType.SInt64: o.WriteSInt64(ToLong(v)); break;
            case FieldType.Enum: o.WriteEnum(ToEnum(f.EnumType, v)); break;
            default: throw new NotSupportedException($"Unsupported scalar type {f.FieldType}.");
        }
    }

    private static WireFormat.WireType WireTypeOf(FieldType t) => t switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes or FieldType.Message => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint
    };

    private static bool IsPackable(FieldType t) =>
        t is not (FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group);

    private static string Str(JsonElement v) =>
        v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();

    private static long ToLong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : checked((long)v.GetDouble()),
        JsonValueKind.String => long.Parse(v.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"expected an integer, got {v.ValueKind}.")
    };

    private static ulong ToULong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetUInt64(out var l) ? l : checked((ulong)v.GetDouble()),
        JsonValueKind.String => ulong.Parse(v.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"expected an unsigned integer, got {v.ValueKind}.")
    };

    private static double ToDouble(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String => double.Parse(v.GetString()!, CultureInfo.InvariantCulture), // "NaN", "Infinity", "-Infinity", "1.5"
        _ => throw new FormatException($"expected a number, got {v.ValueKind}.")
    };

    private static bool ToBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.Parse(v.GetString()!),
        _ => throw new FormatException($"expected a boolean, got {v.ValueKind}.")
    };

    private static int ToEnum(EnumDescriptor e, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        var s = Str(v);
        if (e.FindValueByName(s) is { } named) return named.Number;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new FormatException($"'{s}' is not a value of enum {e.Name}.");
    }

    // ───────────────────────── decoding ─────────────────────────

    private static JsonNode DecodeMessage(byte[] data, MessageDescriptor type)
    {
        var obj = new JsonObject();
        var input = new CodedInputStream(data);
        uint tag;

        while ((tag = input.ReadTag()) != 0)
        {
            var f = type.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            if (f is null) { input.SkipLastField(); continue; }

            var wireType = WireFormat.GetTagWireType(tag);
            var key = f.JsonName;

            if (f.IsMap)
            {
                var (k, v) = DecodeMapEntry(input.ReadBytes().ToByteArray(), f.MessageType);
                if (obj[key] is not JsonObject map) { map = new JsonObject(); obj[key] = map; }
                map[k] = v;
            }
            else if (f.IsRepeated)
            {
                if (obj[key] is not JsonArray arr) { arr = new JsonArray(); obj[key] = arr; }
                if (wireType == WireFormat.WireType.LengthDelimited && IsPackable(f.FieldType))
                {
                    var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                    while (!packed.IsAtEnd) arr.Add(ReadScalar(packed, f));
                }
                else
                {
                    arr.Add(ReadSingular(input, f));
                }
            }
            else
            {
                obj[key] = ReadSingular(input, f);
            }
        }

        var name = type.FullName;
        if (name == "google.protobuf.Timestamp") return TimestampJson(obj);
        if (name == "google.protobuf.Duration") return DurationJson(obj);
        if (Wrappers.Contains(name)) return obj["value"]?.DeepClone() ?? DefaultValue(type.FindFieldByNumber(1));
        return obj;
    }

    private static (string Key, JsonNode Value) DecodeMapEntry(byte[] bytes, MessageDescriptor entry)
    {
        var kf = entry.FindFieldByNumber(1);
        var vf = entry.FindFieldByNumber(2);
        var input = new CodedInputStream(bytes);
        JsonNode? key = null, value = null;
        uint tag;

        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: key = ReadSingular(input, kf); break;
                case 2: value = ReadSingular(input, vf); break;
                default: input.SkipLastField(); break;
            }
        }

        key ??= DefaultValue(kf);
        value ??= DefaultValue(vf);
        var keyText = key is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : key.ToJsonString();
        return (keyText, value);
    }

    private static JsonNode ReadSingular(CodedInputStream input, FieldDescriptor f) => f.FieldType switch
    {
        FieldType.Message => DecodeMessage(input.ReadBytes().ToByteArray(), f.MessageType),
        FieldType.Group => throw new NotSupportedException("Group fields are not supported."),
        _ => ReadScalar(input, f)
    };

    private static JsonNode ReadScalar(CodedInputStream i, FieldDescriptor f)
    {
        switch (f.FieldType)
        {
            case FieldType.Double: return Dbl(i.ReadDouble());
            case FieldType.Float:
            {
                var x = i.ReadFloat();
                return float.IsFinite(x) ? JsonValue.Create(x) : S(x.ToString(CultureInfo.InvariantCulture));
            }
            case FieldType.Int64: return S(i.ReadInt64().ToString(CultureInfo.InvariantCulture));
            case FieldType.UInt64: return S(i.ReadUInt64().ToString(CultureInfo.InvariantCulture));
            case FieldType.Int32: return N(i.ReadInt32());
            case FieldType.Fixed64: return S(i.ReadFixed64().ToString(CultureInfo.InvariantCulture));
            case FieldType.Fixed32: return JsonValue.Create(i.ReadFixed32());
            case FieldType.Bool: return JsonValue.Create(i.ReadBool());
            case FieldType.String: return S(i.ReadString());
            case FieldType.Bytes: return S(i.ReadBytes().ToBase64());
            case FieldType.UInt32: return JsonValue.Create(i.ReadUInt32());
            case FieldType.SFixed32: return N(i.ReadSFixed32());
            case FieldType.SFixed64: return S(i.ReadSFixed64().ToString(CultureInfo.InvariantCulture));
            case FieldType.SInt32: return N(i.ReadSInt32());
            case FieldType.SInt64: return S(i.ReadSInt64().ToString(CultureInfo.InvariantCulture));
            case FieldType.Enum:
            {
                var n = i.ReadEnum();
                return f.EnumType.FindValueByNumber(n) is { } ev ? S(ev.Name) : N(n);
            }
            default: throw new NotSupportedException($"Unsupported scalar type {f.FieldType}.");
        }
    }

    private static JsonNode TimestampJson(JsonObject o)
    {
        var secs = Int64Of(o["seconds"]);
        var nanos = (int?)o["nanos"] ?? 0;
        var t = DateTimeOffset.FromUnixTimeSeconds(secs).AddTicks(nanos / 100);
        return S(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }

    private static JsonNode DurationJson(JsonObject o)
    {
        var secs = Int64Of(o["seconds"]);
        var nanos = (int?)o["nanos"] ?? 0;
        var total = secs + nanos / 1_000_000_000m;
        return S(total.ToString(CultureInfo.InvariantCulture) + "s");
    }

    private static long Int64Of(JsonNode? n) =>
        n is null ? 0 : long.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture);

    // ───────────────────────── JSON helpers ─────────────────────────

    private static JsonNode S(string s) => JsonValue.Create(s)!;
    private static JsonNode N(int n) => JsonValue.Create(n);
    private static JsonNode Dbl(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : S(d.ToString(CultureInfo.InvariantCulture)); // "NaN" / "Infinity"

    private static JsonNode DefaultValue(FieldDescriptor f) => f.FieldType switch
    {
        FieldType.Int64 or FieldType.UInt64 or FieldType.Fixed64 or FieldType.SFixed64 or FieldType.SInt64 => S("0"),
        FieldType.Bool => JsonValue.Create(false),
        FieldType.String or FieldType.Bytes => S(""),
        FieldType.Enum => S(f.EnumType.Values[0].Name),
        FieldType.Message => new JsonObject(),
        _ => N(0)
    };
}
