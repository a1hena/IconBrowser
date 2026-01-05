using System.Text;
using System.Text.Json;

namespace IconBrowser.DataGenerator;

class Program
{
    private const string PatchBaseUrl = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining-patches/master";
    private const string DataBaseUrl = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en";

    // Tables that have icon mappings: (TableName, IconColumnIndex)
    // Column indices are 0-based
    private static readonly (string Name, int IconColumn)[] IconTables =
    [
        ("Item", 68),       // Icon column (69th column)
        ("Action", 3),      // Icon column (4th column)
        ("Status", 3),      // Icon column (4th column)
        ("Mount", 19),      // Icon column (20th column)
        ("Companion", 12),  // Icon column (13th column)
        ("Emote", 2),       // Icon column (3rd column)
        ("Achievement", 4)  // Icon column (5th column)
    ];

    static async Task Main(string[] args)
    {
        var outputDir = args.Length > 0 ? args[0] : "../IconBrowser/Data/Generated";

        Console.WriteLine("IconBrowser Data Generator");
        Console.WriteLine("==========================");
        Console.WriteLine($"Output directory: {Path.GetFullPath(outputDir)}");
        Console.WriteLine();

        Directory.CreateDirectory(outputDir);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "IconBrowser-DataGenerator");

        // 1. Fetch patch list
        Console.WriteLine("Fetching patchlist.json...");
        var patchListJson = await httpClient.GetStringAsync($"{PatchBaseUrl}/patchlist.json");
        var patches = JsonSerializer.Deserialize<List<PatchInfoRaw>>(patchListJson) ?? [];
        Console.WriteLine($"  Found {patches.Count} patches");

        // 2. Fetch entity-to-patch mappings
        Console.WriteLine();
        Console.WriteLine("Fetching entity-to-patch mappings...");
        var entityPatchMappings = new Dictionary<string, Dictionary<int, int>>();

        foreach (var (tableName, _) in IconTables)
        {
            var url = $"{PatchBaseUrl}/patchdata/{tableName}.json";
            Console.WriteLine($"  Fetching {tableName}.json...");

            try
            {
                var json = await httpClient.GetStringAsync(url);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
                entityPatchMappings[tableName] = mapping.ToDictionary(
                    kvp => int.Parse(kvp.Key),
                    kvp => kvp.Value
                );
                Console.WriteLine($"    Found {mapping.Count} entries");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Failed to fetch: {ex.Message}");
            }
        }

        // 3. Fetch CSV data and build icon mappings
        Console.WriteLine();
        Console.WriteLine("Fetching CSV data for icon mappings...");
        var iconPatchMappings = new Dictionary<string, Dictionary<int, int>>(); // category -> (iconId -> patchId)

        foreach (var (tableName, iconColumn) in IconTables)
        {
            if (!entityPatchMappings.ContainsKey(tableName))
            {
                Console.WriteLine($"  Skipping {tableName} (no patch data)");
                continue;
            }

            var url = $"{DataBaseUrl}/{tableName}.csv";
            Console.WriteLine($"  Fetching {tableName}.csv...");

            try
            {
                var csv = await httpClient.GetStringAsync(url);
                var iconMapping = ParseCsvForIcons(csv, iconColumn, entityPatchMappings[tableName]);

                if (iconMapping.Count > 0)
                {
                    iconPatchMappings[tableName] = iconMapping;
                    Console.WriteLine($"    Found {iconMapping.Count} icon mappings");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: Failed to fetch: {ex.Message}");
            }
        }

        // 4. Generate consolidated data
        Console.WriteLine();
        Console.WriteLine("Generating consolidated data...");

        var consolidatedData = new ConsolidatedData
        {
            GeneratedAt = DateTime.UtcNow,
            Patches = patches.Select(p => new PatchInfoCompact
            {
                Id = p.ID,
                Version = p.Version,
                Name = p.Name_en ?? p.Version,
                ExVersion = p.ExVersion,
                ExName = p.ExName ?? "Unknown",
                IsExpansion = p.IsExpansion
            }).ToList(),
            IconMappings = iconPatchMappings.ToDictionary(
                kvp => kvp.Key,
                kvp => CompressMapping(kvp.Value)
            )
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var consolidatedJson = JsonSerializer.Serialize(consolidatedData, jsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "patchdata.json"), consolidatedJson);
        Console.WriteLine($"  Saved patchdata.json ({consolidatedJson.Length / 1024}KB)");

        // Debug version
        var jsonOptionsPretty = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "patchdata.debug.json"),
            JsonSerializer.Serialize(consolidatedData, jsonOptionsPretty));

        // 5. Generate C# loader code
        Console.WriteLine("Generating PatchDataLoader.cs...");
        var loaderCode = GenerateLoaderCode();
        await File.WriteAllTextAsync(Path.Combine(outputDir, "PatchDataLoader.cs"), loaderCode);

        // 6. Summary
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Patches: {patches.Count}");
        foreach (var (category, mapping) in iconPatchMappings)
        {
            var ranges = CompressMapping(mapping).Count;
            var minIcon = mapping.Keys.Min();
            var maxIcon = mapping.Keys.Max();
            Console.WriteLine($"  {category}: {mapping.Count} icons (ID {minIcon}-{maxIcon}), {ranges} ranges");
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }

    // Latest patch ID to use for unmapped entities
    private const int LatestPatchId = 95; // 7.0

    static Dictionary<int, int> ParseCsvForIcons(string csv, int iconColumn, Dictionary<int, int> entityPatchMapping)
    {
        var iconMapping = new Dictionary<int, int>();
        var lines = csv.Split('\n');

        // Skip header line
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            try
            {
                var columns = ParseCsvLine(line);
                if (columns.Count <= iconColumn) continue;

                // Get entity ID (first column)
                if (!int.TryParse(columns[0], out var entityId)) continue;

                // Get icon ID
                if (!int.TryParse(columns[iconColumn], out var iconId)) continue;
                if (iconId <= 0) continue;

                // Get patch ID for this entity (use latest patch if not mapped)
                var patchId = entityPatchMapping.TryGetValue(entityId, out var mappedPatchId)
                    ? mappedPatchId
                    : LatestPatchId;

                // Map icon to patch (keep the earliest patch if icon appears multiple times)
                if (!iconMapping.ContainsKey(iconId) || iconMapping[iconId] > patchId)
                {
                    iconMapping[iconId] = patchId;
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return iconMapping;
    }

    static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    static List<int[]> CompressMapping(Dictionary<int, int> mapping)
    {
        var ranges = new List<int[]>();
        var sorted = mapping.OrderBy(x => x.Key).ToList();

        if (sorted.Count == 0) return ranges;

        var start = sorted[0].Key;
        var patchId = sorted[0].Value;
        var prev = start;

        for (var i = 1; i < sorted.Count; i++)
        {
            var current = sorted[i];
            if (current.Key == prev + 1 && current.Value == patchId)
            {
                prev = current.Key;
            }
            else
            {
                ranges.Add([start, prev, patchId]);
                start = current.Key;
                patchId = current.Value;
                prev = start;
            }
        }

        ranges.Add([start, prev, patchId]);
        return ranges;
    }

    static string GenerateLoaderCode()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated by IconBrowser.DataGenerator");
        sb.AppendLine("// Do not edit manually - run the generator to update");
        sb.AppendLine();
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine();
        sb.AppendLine("namespace IconBrowser.Data;");
        sb.AppendLine();
        sb.AppendLine("public record PatchInfo(int Id, string Version, string Name, int ExVersion, string ExName, bool IsExpansion);");
        sb.AppendLine();
        sb.AppendLine("public static class PatchDataLoader");
        sb.AppendLine("{");
        sb.AppendLine("    private static PatchInfo[]? _patches;");
        sb.AppendLine("    private static Dictionary<string, List<(int Start, int End, int PatchId)>>? _iconMappings;");
        sb.AppendLine("    private static string[]? _expansionNames;");
        sb.AppendLine();
        sb.AppendLine("    public static PatchInfo[] Patches => _patches ??= LoadPatches();");
        sb.AppendLine("    public static string[] ExpansionNames => _expansionNames ??= LoadExpansionNames();");
        sb.AppendLine();
        sb.AppendLine("    private static PatchInfo[] LoadPatches()");
        sb.AppendLine("    {");
        sb.AppendLine("        var data = LoadData();");
        sb.AppendLine("        return data?.Patches?.Select(p => new PatchInfo(p.Id, p.Version, p.Name, p.ExVersion, p.ExName, p.IsExpansion)).ToArray() ?? [];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static string[] LoadExpansionNames()");
        sb.AppendLine("    {");
        sb.AppendLine("        return Patches");
        sb.AppendLine("            .Where(p => p.IsExpansion)");
        sb.AppendLine("            .OrderBy(p => p.ExVersion)");
        sb.AppendLine("            .GroupBy(p => p.ExVersion)");
        sb.AppendLine("            .Select(g => g.First().ExName)");
        sb.AppendLine("            .ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static Dictionary<string, List<(int Start, int End, int PatchId)>> GetIconMappings()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_iconMappings != null) return _iconMappings;");
        sb.AppendLine();
        sb.AppendLine("        var data = LoadData();");
        sb.AppendLine("        _iconMappings = new Dictionary<string, List<(int, int, int)>>();");
        sb.AppendLine();
        sb.AppendLine("        if (data?.IconMappings != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var (category, ranges) in data.IconMappings)");
        sb.AppendLine("            {");
        sb.AppendLine("                _iconMappings[category] = ranges.Select(r => (r[0], r[1], r[2])).ToList();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return _iconMappings;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static PatchInfo? GetPatch(int id) => Patches.FirstOrDefault(p => p.Id == id);");
        sb.AppendLine("    public static PatchInfo? GetPatch(string version) => Patches.FirstOrDefault(p => p.Version == version);");
        sb.AppendLine();
        sb.AppendLine("    public static IEnumerable<PatchInfo> GetPatchesForExpansion(int exVersion)");
        sb.AppendLine("        => Patches.Where(p => p.ExVersion == exVersion);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Gets the patch ID for an icon in a specific category.</summary>");
        sb.AppendLine("    public static int? GetIconPatchId(string category, int iconId)");
        sb.AppendLine("    {");
        sb.AppendLine("        var mappings = GetIconMappings();");
        sb.AppendLine("        if (!mappings.TryGetValue(category, out var ranges)) return null;");
        sb.AppendLine();
        sb.AppendLine("        foreach (var (start, end, patchId) in ranges)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (iconId >= start && iconId <= end)");
        sb.AppendLine("                return patchId;");
        sb.AppendLine("        }");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Gets the patch ID for an icon across all categories.</summary>");
        sb.AppendLine("    public static int? GetIconPatchId(int iconId)");
        sb.AppendLine("    {");
        sb.AppendLine("        foreach (var category in Categories)");
        sb.AppendLine("        {");
        sb.AppendLine("            var patchId = GetIconPatchId(category, iconId);");
        sb.AppendLine("            if (patchId.HasValue) return patchId;");
        sb.AppendLine("        }");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Gets the patch version string for an icon.</summary>");
        sb.AppendLine("    public static string? GetIconPatchVersion(int iconId)");
        sb.AppendLine("    {");
        sb.AppendLine("        var patchId = GetIconPatchId(iconId);");
        sb.AppendLine("        return patchId.HasValue ? GetPatch(patchId.Value)?.Version : null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Gets icon ID ranges for a specific patch and category.</summary>");
        sb.AppendLine("    public static (int Start, int End)[] GetIconRangesForPatch(string category, int patchId)");
        sb.AppendLine("    {");
        sb.AppendLine("        var mappings = GetIconMappings();");
        sb.AppendLine("        if (!mappings.TryGetValue(category, out var ranges)) return [];");
        sb.AppendLine();
        sb.AppendLine("        return ranges");
        sb.AppendLine("            .Where(r => r.PatchId == patchId)");
        sb.AppendLine("            .Select(r => (r.Start, r.End))");
        sb.AppendLine("            .ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Gets all icon ID ranges for a specific patch across all categories.</summary>");
        sb.AppendLine("    public static (string Category, int Start, int End)[] GetAllIconRangesForPatch(int patchId)");
        sb.AppendLine("    {");
        sb.AppendLine("        var result = new List<(string, int, int)>();");
        sb.AppendLine("        foreach (var category in Categories)");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var (start, end) in GetIconRangesForPatch(category, patchId))");
        sb.AppendLine("            {");
        sb.AppendLine("                result.Add((category, start, end));");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return result.ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static string[] Categories => GetIconMappings().Keys.ToArray();");
        sb.AppendLine();
        sb.AppendLine("    private static ConsolidatedData? LoadData()");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            var assembly = Assembly.GetExecutingAssembly();");
        sb.AppendLine("            var resourceName = assembly.GetManifestResourceNames()");
        sb.AppendLine("                .FirstOrDefault(n => n.EndsWith(\"patchdata.json\"));");
        sb.AppendLine();
        sb.AppendLine("            if (resourceName == null) return null;");
        sb.AppendLine();
        sb.AppendLine("            using var stream = assembly.GetManifestResourceStream(resourceName);");
        sb.AppendLine("            if (stream == null) return null;");
        sb.AppendLine();
        sb.AppendLine("            return JsonSerializer.Deserialize<ConsolidatedData>(stream);");
        sb.AppendLine("        }");
        sb.AppendLine("        catch");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private class ConsolidatedData");
        sb.AppendLine("    {");
        sb.AppendLine("        public DateTime GeneratedAt { get; set; }");
        sb.AppendLine("        public List<PatchInfoCompact>? Patches { get; set; }");
        sb.AppendLine("        public Dictionary<string, List<int[]>>? IconMappings { get; set; }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private class PatchInfoCompact");
        sb.AppendLine("    {");
        sb.AppendLine("        public int Id { get; set; }");
        sb.AppendLine("        public string Version { get; set; } = \"\";");
        sb.AppendLine("        public string Name { get; set; } = \"\";");
        sb.AppendLine("        public int ExVersion { get; set; }");
        sb.AppendLine("        public string ExName { get; set; } = \"\";");
        sb.AppendLine("        public bool IsExpansion { get; set; }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}

public class ConsolidatedData
{
    public DateTime GeneratedAt { get; set; }
    public List<PatchInfoCompact> Patches { get; set; } = [];
    public Dictionary<string, List<int[]>> IconMappings { get; set; } = [];
}

public class PatchInfoCompact
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public int ExVersion { get; set; }
    public string ExName { get; set; } = "";
    public bool IsExpansion { get; set; }
}

public class PatchInfoRaw
{
    public int ID { get; set; }
    public string Version { get; set; } = "";
    public int ExVersion { get; set; }
    public string? ExName { get; set; }
    public string? Name_en { get; set; }
    public string? Name_ja { get; set; }
    public long? ReleaseDate { get; set; }
    public bool IsExpansion { get; set; }
}
