using System.Text.Encodings.Web;
using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.GuideGenerator;

public static class ComponentJsonGenerator
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

        // Map: component name -> (entity id -> component fields)
        var output = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var p in proto.EnumeratePrototypes(typeof(EntityPrototype)))
        {
            if (p is not EntityPrototype entProto)
                continue;

            foreach (var (compName, componentFields) in BuildEntityComponentMap(entProto, proto, ser, compFactory))
            {
                GetOrCreateEntry(output, compName)[entProto.ID] = componentFields;
            }
        }

        if (output.Count == 0)
            return;

        foreach (var (compName, map) in output)
        {
            var defaultObj = FieldEntry.ComputeComponentDefault(compName, compFactory, ser);
            var outObj = FieldEntry.DeduplicateAgainstDefault(defaultObj, map);

            res.UserData.CreateDir(destRoot);
            var directoryName = TextTools.CapitalizeString(compName);
            var fileName = directoryName + ".json";
            using (var stream = res.UserData.OpenWrite(destRoot / fileName))
            {
                JsonSerializer.Serialize(stream, outObj, SerializeOptions);
            }

            var componentRoot = destRoot / directoryName;
            res.UserData.CreateDir(componentRoot);

            using (var defaultStream = res.UserData.OpenWrite(componentRoot / "defaultFields.json"))
            {
                JsonSerializer.Serialize(defaultStream, defaultObj, SerializeOptions);
            }
        }
    }

    private static Dictionary<string, object?> GetOrCreateEntry(
        Dictionary<string, Dictionary<string, object?>> output, string key)
    {
        if (!output.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, object?>();
            output[key] = map;
        }
        return map;
    }

    public static Dictionary<string, object?> BuildEntityComponentMap(
        EntityPrototype entProto,
        IPrototypeManager proto,
        ISerializationManager ser,
        IComponentFactory compFactory)
    {
        var components = new Dictionary<string, object?>(StringComparer.Ordinal);
        var composedComponents = YAMLEntry.GetComposedComponentMappings(entProto, proto, ser, compFactory);

        foreach (var (compName, entry) in entProto.Components)
        {
            if (!FieldEntry.TryWriteValueAsMapping(ser, entry.Component.GetType(), entry.Component, out var node))
                continue;

            composedComponents.TryGetValue(compName, out var composedNode);
            components[compName] = FieldEntry.ProcessNode(entry.Component, node, composedNode);
        }

        foreach (var (compName, node) in composedComponents)
        {
            if (entProto.Components.ContainsKey(compName))
                continue;

            components[compName] = FieldEntry.DataNodeToObject(node);
        }

        return components;
    }
}
