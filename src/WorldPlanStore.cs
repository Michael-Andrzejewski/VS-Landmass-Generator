using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// Each world keeps the plan it was set up with (0.64.0).
//
// The worldgen renderer draws every plan island's terrain into chunks as they
// are born, and it used to read the plan FILE and the shape files at every
// load. Two ways that went wrong: editing the live plan or a shape (the Great
// Machine went from a 600-wide pyramid to a 1200-wide crater) would draw the
// new outline into the unexplored edges of a world built with the old one;
// and a test world copied into Michael's Saves read his full 29-island plan,
// so islands its setup never decorated came out bare (Eerie Island with no
// graves or monoliths). Now the world setup keeps the plan's text, and the
// text of every shape it names, in the save. The renderer, the setup's own
// island builds, the rain zones and the natural-island scan read those. A
// hand-run /genisland still reads today's files.
//
// Worlds set up before this keep reading the files until a plan is adopted
// for them: /genplan adopt <plan or folder>. A folder holds a worldplan.txt and
// any shapes that differ from the live ones (the shape a world was really
// built with); every other shape comes from the live folder.
public partial class LandmassGeneratorModSystem
{
    private const string WorldPlanTextKey = "lgWorldPlanText";
    private const string KeptShapeKey = "lgWorldShape:";

    // Set while plan islands are loaded for this world: LoadShape then takes
    // the kept shape text when the world has one.
    private bool _useKeptShapes;
    private List<string> _lastKeptFromDir = new();

    // The plan lines this world was set up with, or null for a world made
    // before 0.64.0 that has not adopted one.
    private string[] StoredWorldPlanLines()
    {
        string text = sapi.WorldManager.SaveGame.GetData<string>(WorldPlanTextKey, null);
        return text == null ? null : text.Replace("\r", "").Split('\n');
    }

    private string KeptShapeText(string shapeName)
        => sapi.WorldManager.SaveGame.GetData<string>(KeptShapeKey + shapeName.ToLowerInvariant(), null);

    // The shape every island line of a plan names.
    private static List<string> PlanShapeNames(string planText)
    {
        var names = new List<string>();
        foreach (string raw in planText.Replace("\r", "").Split('\n'))
        {
            string[] parts = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            // a ruins line builds its islands from the three ruin_isle shapes
            if (parts.Length >= 1 && parts[0].Equals("ruins", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string n in new[] { "ruin_isle", "ruin_isle_cold", "ruin_isle_arid" })
                    if (!names.Contains(n)) names.Add(n);
                continue;
            }
            if (parts.Length < 4 || !parts[0].Equals("island", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string tok in parts.Skip(3))
                if (tok.StartsWith("shape=", StringComparison.OrdinalIgnoreCase))
                {
                    string n = tok.Substring(6).Trim().ToLowerInvariant();
                    if (n.EndsWith(".txt")) n = n.Substring(0, n.Length - 4);
                    if (n.Length > 0 && !names.Contains(n)) names.Add(n);
                }
        }
        return names;
    }

    // Keep a plan and its shapes in this world's save: shapes from
    // shapeDir when it has them, else the live shape folder. Returns the
    // shapes it could not find.
    private List<string> KeepWorldPlan(string planText, string shapeDir)
    {
        var save = sapi.WorldManager.SaveGame;
        save.StoreData(WorldPlanTextKey, planText);
        var missing = new List<string>();
        _lastKeptFromDir = new List<string>();
        foreach (string n in PlanShapeNames(planText))
        {
            string p = Path.Combine(shapeDir, n + ".txt");
            if (File.Exists(p) && !string.Equals(Path.GetFullPath(shapeDir), Path.GetFullPath(shapeFolder), StringComparison.OrdinalIgnoreCase)) _lastKeptFromDir.Add(n);
            if (!File.Exists(p)) p = Path.Combine(shapeFolder, n + ".txt");
            if (!File.Exists(p)) { missing.Add(n); continue; }
            save.StoreData(KeptShapeKey + n, File.ReadAllText(p));
        }
        sapi.Logger.Notification("[landmassgenerator] World plan kept in the save ({0}){1}{2}", Summarize(planText.Replace("\r", "").Split('\n')),
            _lastKeptFromDir.Count > 0 ? "; from " + shapeDir + ": " + string.Join(", ", _lastKeptFromDir) : "",
            missing.Count > 0 ? "; shapes not found: " + string.Join(", ", missing) : "");
        return missing;
    }

    private void StartWorldPlanStore(ICoreServerAPI api)
    {
        var p = api.ChatCommands.Parsers;
        RegisterCmd(api, "genplan", name =>
            api.ChatCommands.Create(name)
                .WithDescription("The world plan this world draws its islands from. /genplan shows it. /genplan adopt [plan or folder] keeps a plan and its shapes in this world's save (LandmassGenerator/<name>.txt, or a folder LandmassGenerator/<name>/ holding worldplan.txt plus any shapes that differ from the live ones; nothing given = the live worldplan.txt). For worlds set up before 0.64.0, which still read the live files. Takes effect when the world next loads; builds nothing.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("action"), p.OptionalWord("name"))
                .HandleWith(OnGenPlan));
    }

    private TextCommandResult OnGenPlan(TextCommandCallingArgs args)
    {
        string action = args.Parsers[0].IsMissing ? "show" : ((string)args[0]).ToLowerInvariant();
        if (action == "adopt")
        {
            string name = args.Parsers[1].IsMissing ? "worldplan" : ((string)args[1]).Trim();
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return TextCommandResult.Error("Give a plan or folder name inside the LandmassGenerator folder.");
            if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            string dir = Path.Combine(shapeFolder, name), planPath;
            if (Directory.Exists(dir)) planPath = Path.Combine(dir, "worldplan.txt");
            else { dir = shapeFolder; planPath = Path.Combine(shapeFolder, name + ".txt"); }
            if (!File.Exists(planPath)) return TextCommandResult.Error($"No plan file {planPath}");
            string text = File.ReadAllText(planPath);
            var missing = KeepWorldPlan(text, dir);
            return TextCommandResult.Success($"This world now keeps {planPath} as its plan ({Summarize(text.Replace("\r", "").Split('\n'))})"
                + (_lastKeptFromDir.Count > 0 ? $"; from that folder: {string.Join(", ", _lastKeptFromDir)}" : "")
                + (missing.Count > 0 ? $"; shapes not found: {string.Join(", ", missing)}" : "")
                + ". Restart the world for it to take effect.");
        }

        string[] stored = StoredWorldPlanLines();
        if (stored != null)
        {
            var shapes = PlanShapeNames(string.Join("\n", stored));
            int kept = shapes.Count(n => KeptShapeText(n) != null);
            return TextCommandResult.Success($"This world keeps its own plan in the save: {Summarize(stored)}, {kept} of {shapes.Count} shape(s) kept.");
        }
        string planName = sapi.WorldManager.SaveGame.GetData<string>("lgWorldPlanName", null) ?? "worldplan";
        string livePath = Path.Combine(shapeFolder, planName + ".txt");
        return File.Exists(livePath)
            ? TextCommandResult.Success($"This world was set up before plans were kept in the save, so it reads {livePath} and the live shapes at every load: {Summarize(File.ReadAllLines(livePath))}. /genplan adopt fixes it to one plan.")
            : TextCommandResult.Success("This world has no plan.");
    }

    private static string Summarize(string[] lines)
    {
        int Count(string d) => lines.Count(l => l.TrimStart().StartsWith(d + " ", StringComparison.OrdinalIgnoreCase));
        return $"{Count("island")} island(s), {Count("rain")} rain line(s), {Count("storyloc")} story location(s)";
    }
}
