using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Content.Shared.Tag;
using Content.Shared.Trigger.Components.Effects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server.Corvax.GuideGenerator;

public static class TagJsonGenerator
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void PublishJson(Stream stream)
    {
        var proto = IoCManager.Resolve<IPrototypeManager>();
        var ser = IoCManager.Resolve<ISerializationManager>();
        var compFactory = IoCManager.Resolve<IComponentFactory>();

        var output = new Dictionary<string, List<string>>();

        foreach (var p in proto.EnumeratePrototypes(typeof(EntityPrototype)))
        {
            if (p is not EntityPrototype entProto)
                continue;

            if (entProto.Abstract)
                continue;

            var tags = new HashSet<string>();

            if (entProto.Components.TryGetValue("Tag", out var tagEntry))
            {
                if (tagEntry.Component is TagComponent tagComp)
                {
                    foreach (var tag in tagComp.Tags)
                        tags.Add(tag.Id);
                }
            }

            var composed = YAMLEntry.GetComposedComponentMappings(entProto, proto, ser, compFactory);
            ExtractTagsFromMapping(composed, "Tag", tags);

            if (tags.Count > 0)
                output[entProto.ID] = tags.OrderBy(t => t).ToList();
        }

        if (output.Count == 0)
            return;

        JsonSerializer.Serialize(stream, output, SerializeOptions);
    }

    private static void ExtractTagsFromMapping(
        Dictionary<string, MappingDataNode> composed,
        string compName,
        HashSet<string> tags)
    {
        if (!composed.TryGetValue(compName, out var mapping))
            return;

        if (!mapping.TryGet("tags", out SequenceDataNode? tagsSeq))
            return;

        foreach (var node in tagsSeq)
        {
            if (node is ValueDataNode valueNode && !string.IsNullOrEmpty(valueNode.Value))
                tags.Add(valueNode.Value);
        }
    }
}
