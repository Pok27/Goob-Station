using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._CorvaxGoob.GuideGenerator;

public static class SpriteEntityJsonGenerator
{
    private const string SpriteComponentName = "Sprite";

    private static void PublishJson(StreamWriter file, bool matchingFilter)
    {
        var prototypeManager = IoCManager.Resolve<IPrototypeManager>();
        var serializationManager = IoCManager.Resolve<ISerializationManager>();
        var componentFactory = IoCManager.Resolve<IComponentFactory>();
        var allowedIds = EntityProjectGenerator.GetProjectEntityIds(); // Corvax-Wiki-Project

        var output = new Dictionary<string, object?>();

        foreach (var proto in prototypeManager.EnumeratePrototypes<EntityPrototype>())
        {
            if (EntityNameDuplicatesJsonGenerator.MatchesEntityNameFilter(proto) != matchingFilter)
                continue;

            if (!allowedIds.Contains(proto.ID)) // Corvax-Wiki-Project
                continue;

            var components = YAMLEntry.GetComposedComponentMappings(proto, prototypeManager, serializationManager, componentFactory);
            if (!components.TryGetValue(SpriteComponentName, out var spriteNode))
                continue;

            output[proto.ID] = FieldEntry.DataNodeToObject(spriteNode);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        file.Write(JsonSerializer.Serialize(output, options));
    }

    public static void PublishIncludedJson(StreamWriter file)
    {
        PublishJson(file, true);
    }

    public static void PublishExcludedJson(StreamWriter file)
    {
        PublishJson(file, false);
    }
}
