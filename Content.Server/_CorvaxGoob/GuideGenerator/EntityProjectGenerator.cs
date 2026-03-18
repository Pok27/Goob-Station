using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Content.Server._CorvaxGoob.GuideGenerator;

// Corvax-Wiki-Project
public static partial class EntityProjectGenerator
{
    /// <summary>
    /// Prefix for prototype project folders, e.g. "_CorvaxGoob", "_MyFork".
    /// </summary>
    private const string ProjectFolderPrefix = "_";

    /// <summary>
    /// Special core project folder that should be ignored when building project lists.
    /// </summary>
    private const string ExcludedCoreProjectFolder = "_Corvax";

    public static HashSet<string> GetProjectEntityIds()
    {
        var workingDir = Directory.GetCurrentDirectory();
        var prototypesRoot = Path.Combine(workingDir, "Resources", "Prototypes");
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(prototypesRoot))
            return ids;

        foreach (var dir in Directory.EnumerateDirectories(prototypesRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(ProjectFolderPrefix))
                continue;
            if (name.Equals(ExcludedCoreProjectFolder, StringComparison.Ordinal))
                continue;

            foreach (var path in Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories))
            {
                ExtractIdsFromYaml(path, ids);
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
            {
                ExtractIdsFromYaml(path, ids);
            }
        }

        return ids;
    }

    public static void PublishJson(StreamWriter file)
    {
        var ids = GetProjectEntityIds();
        if (ids.Count == 0)
            return;

        var sorted = ids.ToList();
        sorted.Sort(StringComparer.Ordinal);

        var serializeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        file.Write(JsonSerializer.Serialize(sorted, serializeOptions));
    }

    [GeneratedRegex(@"^\s*-\s*type\s*:\s*(?<type>\S+)\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex EntryStartRegex();

    [GeneratedRegex(@"^\s*id\s*:\s*(?<id>.+?)\s*$", RegexOptions.ExplicitCapture)]
    private static partial Regex IdRegex();

    private static void ExtractIdsFromYaml(
        string path,
        HashSet<string> output)
    {
        var inEntityEntry = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine;

            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entryStartMatch = EntryStartRegex().Match(line);
            if (entryStartMatch.Success)
            {
                var type = entryStartMatch.Groups["type"].Value;
                inEntityEntry = string.Equals(type, "entity", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEntityEntry)
                continue;

            var idMatch = IdRegex().Match(line);
            if (!idMatch.Success)
                continue;

            var id = idMatch.Groups["id"].Value.Trim();
            id = id.Trim('\'', '"');
            if (string.IsNullOrWhiteSpace(id))
                continue;

            output.Add(id);
            inEntityEntry = false;
        }
    }
}
