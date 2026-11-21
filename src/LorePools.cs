using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Random lore books that draw from a chain of lore categories (0.69.0).
//
// A vanilla lore book with a category reveals, when read, the next unread
// page of a random journal in that one category, and says "Nothing new in
// these pages" once the category is used up. Rustfall hands out many random
// books, and the Jonas category holds only eight pages (Michael's six scenes
// of the falx journal and vanilla's two), so a pool tries its categories in
// order and moves on when one is used up:
//
//   randomlore:rfpool-sea     diaries, then Jonas, then villager stories, ...
//
// "vanilla" is any of the game's six open categories, "any" every category
// in the game (vanilla, Better Ruins, ...) except the ones bound to a place.
// The pick happens when the book is read, through the journal's own picker,
// so it follows what that player has already found.
public partial class LandmassGeneratorModSystem
{
    private static readonly Dictionary<string, string[]> LorePoolChains = new()
    {
        ["rfpool-jonas"] = new[] { "jonas", "research", "tobias", "vanilla", "any" },
        ["rfpool-tobias"] = new[] { "tobias", "jonas", "vanilla", "any" },
        ["rfpool-sea"] = new[] { "diaries", "jonas", "villager", "vanilla", "any" },
        ["rfpool-village"] = new[] { "villager", "diaries", "vanilla", "any" },
        ["rfpool-lab"] = new[] { "research", "jonas", "archives", "vanilla", "any" },
        ["rfpool-war"] = new[] { "forlornhope", "lastwall", "jonas", "vanilla", "any" },
        ["rfpool-trade"] = new[] { "diaries", "villager", "archives", "vanilla", "any" },
        // Old bookshelves (0.73.1): any Rustfall record not yet found, then the falx scenes, then vanilla.
        ["rfpool-shelf"] = new[] { "rustfall", "jonas", "vanilla", "any" },
    };

    private static readonly string[] VanillaLoreCategories = { "jonas", "tobias", "villager", "diaries", "research", "archives" };

    // Never drawn at random: the Rustfall records belong to their own chests,
    // and these vanilla ones to their own story sites.
    private static readonly HashSet<string> PlaceBoundLore = new() { "rustfall", "archivesnote", "archivesnote2", "lazaret", "tapestry", "wallcarving" };

    private static readonly MethodInfo JournalRandomPick = typeof(ModJournal).GetMethod("getRandomLoreDiscovery", BindingFlags.NonPublic | BindingFlags.Instance);

    private List<JournalAsset> _journalAssets;

    private List<JournalAsset> JournalAssets()
        => _journalAssets ??= sapi.Assets.GetMany<JournalAsset>(sapi.World.Logger, "config/lore/").Values.Where(a => a?.Code != null).ToList();

    private void StartLorePools(ICoreServerAPI api)
    {
        // Ahead of the journal's own listener (priority 0.5).
        api.Event.RegisterEventBusListener(OnLorePoolBook, 1.0, "loreDiscovery");

        var p = api.ChatCommands.Parsers;
        RegisterCmd(api, "genlorebook", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Put a lore book in your hand, made from one chest contents spec (lore:lighthouse_keeper, randomlore:rfpool-sea, journal:falx:5). 'read' then reads it, the way right-clicking it does.")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .WithArgs(p.Word("spec"), p.OptionalWord("read"))
                .HandleWith(OnGenLoreBook));

        RegisterCmd(api, "genmaps", name =>
            api.ChatCommands.Create(name)
                .WithDescription("List every island map and the place it marks (HUD coordinates), the worn chart's pool, and five worn charts read at the starter.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => OnGenMaps()));
    }

    private TextCommandResult OnGenMaps()
    {
        int hx = sapi.WorldManager.MapSizeX / 2, hz = sapi.WorldManager.MapSizeZ / 2;
        var lines = new List<string>();
        foreach (Item item in sapi.World.Items.Where(i => i?.Code?.Domain == "landmassgenerator" && i.Code.Path.StartsWith("islandmap-")))
        {
            if (item.Attributes?["randomTarget"]?.AsBool() == true) continue;
            Vec3d pos = FindMapTarget(item.Attributes);
            lines.Add($"{item.Code.Path}: " + (pos == null ? "NOT FOUND" : $"HUD {(int)pos.X - hx}, {(int)pos.Z - hz}"));
        }
        var pool = MapTargets();
        lines.Add($"worn chart pool, {pool.Count} places: " + string.Join(", ", pool.Select(t => t.Label)));
        var at = new Vec3d(hx, sapi.World.SeaLevel, hz);
        lines.Add("worn charts read at the starter: " + string.Join(", ", Enumerable.Range(0, 5).Select(_ => PickMapTarget(null, at)?.Label ?? "nothing")));
        foreach (string l in lines) sapi.Logger.Notification("[landmassgenerator] maps: {0}", l);
        return TextCommandResult.Success(string.Join("\n", lines));
    }

    private TextCommandResult OnGenLoreBook(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer plr) return TextCommandResult.Error("Players only.");
        var notes = new List<string>();
        ItemStack stack = MakeChestStack((string)args[0], notes);
        if (stack == null) return TextCommandResult.Error("No book: " + string.Join("; ", notes));
        ItemSlot slot = plr.InventoryManager.ActiveHotbarSlot;
        if (slot == null) return TextCommandResult.Error("No hotbar slot selected.");
        slot.Itemstack = stack;
        slot.MarkDirty();
        if ((args.Parsers[1].IsMissing ? "" : (string)args[1]) != "read") return TextCommandResult.Success($"In your hand: {stack.GetName()}.");
        // what the lore item sends when a player right-clicks it
        var tree = new TreeAttribute();
        tree.SetString("playeruid", plr.PlayerUID);
        tree.SetString("category", stack.Attributes.GetString("category"));
        tree.SetItemstack("itemstack", stack.Clone());
        sapi.Event.PushEvent("loreDiscovery", tree);
        string code = slot.Itemstack?.Attributes.GetString("discoveryCode");
        int[] pages = (slot.Itemstack?.Attributes["chapterIds"] as IntArrayAttribute)?.value;
        return TextCommandResult.Success(code == null ? "Read it: nothing new." : $"Read it: journal {code}, page {string.Join(",", pages ?? new int[0])}.");
    }

    private void OnLorePoolBook(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not TreeAttribute tree) return;
        string pool = tree.GetString("category");
        if (pool == null || !LorePoolChains.TryGetValue(pool, out string[] chain)) return;
        if (sapi.World.PlayerByUid(tree.GetString("playeruid")) is not IServerPlayer plr) return;
        ItemSlot slot = plr.InventoryManager.ActiveHotbarSlot;
        // A book read once already holds its page; the journal shows it again.
        if (slot?.Itemstack == null || slot.Itemstack.Attributes.GetString("discoveryCode") != null) return;
        var journal = sapi.ModLoader.GetModSystem<ModJournal>();
        if (journal == null) return;

        if (JournalRandomPick == null)
        {
            // A game version without the picker: let the journal draw from the first category.
            tree.SetString("category", chain[0]);
            return;
        }
        LoreDiscovery found = null;
        foreach (string step in chain)
        {
            IEnumerable<string> cats = step switch
            {
                "vanilla" => VanillaLoreCategories.OrderBy(_ => sapi.World.Rand.Next()),
                "any" => JournalAssets().Select(a => a.Category).Where(c => c != null && !PlaceBoundLore.Contains(c)).Distinct().OrderBy(_ => sapi.World.Rand.Next()),
                _ => new[] { step },
            };
            foreach (string cat in cats)
            {
                found = JournalRandomPick.Invoke(journal, new object[] { sapi.World, plr, cat }) as LoreDiscovery;
                if (found != null) break;
            }
            if (found != null) break;
        }
        handling = EnumHandling.PreventSubsequent;
        if (found == null)
        {
            plr.SendIngameError("alreadydiscovered", Lang.Get("Nothing new in these pages"));
            return;
        }
        if (journal.TryDiscoverLore(found, plr, slot))
        {
            slot.MarkDirty();
            sapi.World.PlaySoundAt(new AssetLocation("sounds/effect/writing"), plr.Entity, null, true, 32f, 1f);
        }
        sapi.Logger.Notification("[landmassgenerator] {0} read a {1} book: journal {2}, page {3}", plr.PlayerName, pool, found.Code, string.Join(",", found.ChapterIds));
    }

    // journal:<code>:<page>[:<item code>]: a lore book holding that one page
    // of a journal, the way a found page does (journal:falx:5 is The Bells).
    private ItemStack MakeJournalPage(string spec, List<string> notes)
    {
        string[] parts = spec.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[2], out int page)) { notes.Add($"'{spec}' is not journal:<code>:<page>"); return null; }
        var asset = JournalAssets().FirstOrDefault(a => a.Code == parts[1]);
        if (asset == null || page < 0 || page >= (asset.Pieces?.Length ?? 0)) { notes.Add($"no page {page} in journal '{parts[1]}'"); return null; }
        string itemCode = parts.Length > 3 ? string.Join(":", parts.Skip(3)) : "game:lore-book-aged-orangebrown";
        Item item = sapi.World.GetItem(new AssetLocation(itemCode));
        if (item == null) { notes.Add($"'{itemCode}' is not in this game"); return null; }
        var st = new ItemStack(item);
        if (asset.Category != null) st.Attributes.SetString("category", asset.Category);
        st.Attributes.SetString("discoveryCode", asset.Code);
        st.Attributes["chapterIds"] = new IntArrayAttribute(new[] { page });
        return st;
    }
}
