using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace HMSync.Services;

// CN port: display names for the English-only stage catalog and quest tables. The CN client carries no English sheets,
// so the mapping is prebuilt (scene-names.zh.json). Stage.Name stays English: it is the canonical name sent on the wire.
public static class SceneNames
{
    private sealed record Table(Dictionary<string, string> Places, Dictionary<string, string> Quests);

    private static readonly Table Names = Load();

    private static Table Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var res = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("scene-names.zh.json", StringComparison.OrdinalIgnoreCase));
            if (res != null)
            {
                using var stream = asm.GetManifestResourceStream(res)!;
                using var reader = new StreamReader(stream);
                var table = JsonSerializer.Deserialize<Table>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (table?.Places != null && table.Quests != null) return table;
            }
        }
        catch (JsonException) { }   // names are cosmetic: fall back to English
        return new Table([], []);
    }

    public static string Quest(string name) => Names.Quests.TryGetValue(name, out var zh) ? zh : name;

    public static string Stage(CutsceneStageService.Stage stage)
    {
        var name = stage.Name;
        if (Names.Places.TryGetValue(name, out var zh)) return zh;

        // "Place (tag)": map edits and repeated dressings
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && name.EndsWith(')') && Names.Places.TryGetValue(name[..open], out var place))
            return place + "（" + name[(open + 2)..^1] + "）";

        // Auto-detected venues are named by their map code only: say which quest's scene it is.
        if (name == stage.Code && stage.Quest.Length > 0) return Quest(stage.Quest) + "（" + stage.Code + "）";
        return name;
    }
}
