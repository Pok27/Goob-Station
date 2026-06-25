using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.GuideGenerator;

public static class PrototypeJsonGenerator
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void PublishAll(IResourceManager res, ResPath destRoot)
    {
        var proto = IoCManager.Resolve<IPrototypeManager>();
        var ser = IoCManager.Resolve<ISerializationManager>();
        var compFactory = IoCManager.Resolve<IComponentFactory>();

        foreach (var kind in proto.EnumeratePrototypeKinds().OrderBy(t => t.Name))
        {
            // The entity prototype has its own generator due to its size <see cref="EntityJsonGenerator"/>.
            var isEntityPrototype = kind == typeof(EntityPrototype);

            if (HasUnsafeSerializedDataField(kind))
                continue;

            // Map: entity id -> prototype fields
            var map = new Dictionary<string, object?>();

            foreach (var p in proto.EnumeratePrototypes(kind))
            {
                if (!FieldEntry.TryWriteValueAsMapping(ser, kind, p, out var node))
                    continue;

                node.Remove("id");

                var fields = FieldEntry.ProcessNode(p, node);
                if (isEntityPrototype && p is EntityPrototype entProto)
                    fields = ProcessEntityPrototype(entProto, proto, ser, compFactory, fields);

                map[p.ID] = fields;
            }

            if (map.Count == 0)
                continue;

            var defaultObj = FieldEntry.ComputePrototypeDefault(kind, ser);
            var outObj = FieldEntry.DeduplicateAgainstDefault(defaultObj, map);

            res.UserData.CreateDir(destRoot);
            var kindName = proto.TryGetKindFrom(kind, out var actualKindName)
                ? actualKindName
                : kind.Name;
            var directoryName = TextTools.CapitalizeString(kindName);

            if (!isEntityPrototype)
            {
                var fileName = directoryName + ".json";
                using var stream = res.UserData.OpenWrite(destRoot / fileName);
                JsonSerializer.Serialize(stream, outObj, SerializeOptions);
            }
            else
            {
                var kindRoot = destRoot / directoryName;
                res.UserData.CreateDir(kindRoot);

                var entityMap = outObj.TryGetValue("id", out var idVal) && idVal is Dictionary<string, object?> em
                    ? em
                    : outObj;

                foreach (var (id, fields) in entityMap)
                {
                    using var prototypeStream = res.UserData.OpenWrite(kindRoot / (id + ".json"));
                    JsonSerializer.Serialize(prototypeStream, fields, SerializeOptions);
                }
            }
        }
    }

    private static bool HasUnsafeSerializedDataField(Type type)
    {
        return HasUnsafeSerializedDataField(type, new HashSet<Type>());
    }

    private static object? ProcessEntityPrototype(
        EntityPrototype entProto,
        IPrototypeManager proto,
        ISerializationManager ser,
        IComponentFactory compFactory,
        object? fields)
    {
        if (fields is not Dictionary<string, object?> fieldMap)
            return fields;

        var componentMap = ComponentJsonGenerator.BuildEntityComponentMap(entProto, proto, ser, compFactory);
        if (componentMap.Count == 0)
            fieldMap.Remove("components");
        else
            fieldMap["components"] = componentMap;

        return fieldMap;
    }

    private static bool HasUnsafeSerializedDataField(Type type, HashSet<Type> visited)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (!visited.Add(type))
            return false;

        return type.GetFields(flags).Cast<MemberInfo>()
            .Concat(type.GetProperties(flags))
            .Any(m => HasDataField(m) && IsUnsafeSerializedType(FieldEntry.GetMemberType(m), visited));
    }

    private static bool HasDataField(MemberInfo member)
    {
        return member.GetCustomAttributes(inherit: true)
            .Any(attr => attr.GetType().Name is nameof(DataFieldAttribute) or nameof(IdDataFieldAttribute));
    }

    private static bool IsUnsafeSerializedType(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(EntityUid) || type == typeof(NetEntity))
            return true;

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan))
            return false;

        if (type.IsArray)
            return IsUnsafeSerializedType(type.GetElementType()!, visited);

        if (type.IsGenericType)
            return type.GetGenericArguments().Any(arg => IsUnsafeSerializedType(arg, visited));

        return type.GetCustomAttributes(inherit: true)
                   .Any(attr => attr.GetType().Name is nameof(DataDefinitionAttribute) or nameof(SerializableAttribute))
                && HasUnsafeSerializedDataField(type, visited);
    }
}
