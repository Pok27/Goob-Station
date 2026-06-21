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
        var entMan = IoCManager.Resolve<IEntityManager>();

        // Map: component name -> (entity id -> component fields)
        var output = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var p in proto.EnumeratePrototypes(typeof(EntityPrototype)))
        {
            if (p is not EntityPrototype entProto)
                continue;

            var composedComponents = YAMLEntry.GetComposedComponentMappings(entProto, proto, ser, compFactory);

            foreach (var (compName, entry) in entProto.Components)
            {
                MappingDataNode node;
                try
                {
                    node = ser.WriteValueAs<MappingDataNode>(entry.Component.GetType(), entry.Component);
                }
                catch
                {
                    continue;
                }

                composedComponents.TryGetValue(compName, out var composedNode);
                GetOrCreateEntry(output, compName)[entProto.ID] = FieldEntry.ProcessNode(entry.Component, node, composedNode);
            }

            foreach (var (compName, node) in composedComponents)
            {
                if (entProto.Components.ContainsKey(compName))
                    continue;

                GetOrCreateEntry(output, compName)[entProto.ID] = FieldEntry.DataNodeToObject(node);
            }
        }

        if (output.Count == 0)
            return;

        foreach (var (compName, map) in output)
        {
            var defaultObj = FieldEntry.ComputeComponentDefault(compName, compFactory, entMan, ser);
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
}
