using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Lore records taken out of Rustfall (0.74.7): Michael did not write them. New worlds never place them
// (their files are gone and their chests hold a random lore book instead). In an existing world:
//
// - a copy still lying in a chest turns into a random lore book from the pool below when someone reads it;
// - /genlorepurge swaps every copy in loaded chests, display cases, items on the ground and online players'
//   inventories for that random book now, and takes the records out of every player's journal.
public partial class LandmassGeneratorModSystem
{
    // record id -> the random lore pool its replacement book draws from
    private static readonly Dictionary<string, string> RemovedLore = new()
    {
        ["alchemist_register"] = "rfpool-lab",
        ["alchemist_last_entry"] = "rfpool-lab",
        ["eerie_decree"] = "rfpool-village",
        ["eerie_marking"] = "rfpool-village",
        ["eerie_ledger"] = "rfpool-tobias",
        ["eerie_letter"] = "rfpool-village",
        ["eerie_last_warden"] = "rfpool-sea",
        ["eerie_robber"] = "rfpool-trade",
        ["hanging_gardens_chronicle"] = "rfpool-village",
        ["spoils_scavenger"] = "rfpool-trade",
        ["tin_chart"] = "rfpool-lab",
        ["volcano_chart"] = "rfpool-trade",
    };

    private static string RemovedLorePool(ItemStack stack)
    {
        string code = stack?.Attributes?.GetString("discoveryCode");
        if (code == null || !code.StartsWith("rf-")) return null;
        return RemovedLore.TryGetValue(code.Substring(3), out string pool) ? pool : null;
    }

    // A removed record's book becomes a random lore book of its pool (same book item, so it looks the same).
    private static bool ConvertRemovedLore(ItemStack stack)
    {
        string pool = RemovedLorePool(stack);
        if (pool == null) return false;
        stack.Attributes.RemoveAttribute("discoveryCode");
        stack.Attributes.RemoveAttribute("chapterIds");
        stack.Attributes.SetString("category", pool);
        return true;
    }

    // Called first thing when a lore book is read.
    private void ConvertRemovedLoreInHand(TreeAttribute tree)
    {
        if (sapi.World.PlayerByUid(tree.GetString("playeruid")) is not IServerPlayer plr) return;
        ItemSlot slot = plr.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack == null || !ConvertRemovedLore(slot.Itemstack)) return;
        tree.SetString("category", slot.Itemstack.Attributes.GetString("category"));
        slot.MarkDirty();
    }

    private void StartLoreRemoval(ICoreServerAPI api)
    {
        RegisterCmd(api, "genlorepurge", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Take the removed lore records out of this world: every copy in loaded chests, display cases, on the ground and in online players' inventories becomes a random lore book, and the records leave every player's journal. Books in areas not loaded now change when someone reads them.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => OnGenLorePurge()));
    }

    private TextCommandResult OnGenLorePurge()
    {
        int inChests = 0, onGround = 0, carried = 0;
        int Slots(IInventory inv)
        {
            int n = 0;
            if (inv == null) return 0;
            foreach (ItemSlot slot in inv)
                if (slot?.Itemstack != null && ConvertRemovedLore(slot.Itemstack)) { slot.MarkDirty(); n++; }
            return n;
        }

        foreach (var chunk in sapi.WorldManager.AllLoadedChunks.Values)
        {
            if (chunk?.BlockEntities == null) continue;
            foreach (BlockEntity be in chunk.BlockEntities.Values.ToList())
            {
                if (be is not BlockEntityContainer c) continue;
                int n = Slots(c.Inventory);
                if (n > 0) { inChests += n; be.MarkDirty(true); }
            }
        }
        foreach (Entity e in sapi.World.LoadedEntities.Values.ToList())
            if (e is EntityItem ei && ei.Itemstack != null && ConvertRemovedLore(ei.Itemstack)) { ei.WatchedAttributes.MarkAllDirty(); onGround++; }
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            foreach (IInventory inv in plr.InventoryManager.Inventories.Values) carried += Slots(inv);

        int pages = PurgeRemovedLoreFromJournals();
        string msg = $"Removed lore: {inChests} in chests and cases, {onGround} on the ground, {carried} carried, swapped for random lore books; {pages} journal entr{(pages == 1 ? "y" : "ies")} taken out.";
        sapi.Logger.Notification("[landmassgenerator] {0}", msg);
        return TextCommandResult.Success(msg);
    }

    // The game keeps each player's journal (and what they have discovered) in private dictionaries of ModJournal.
    private int PurgeRemovedLoreFromJournals()
    {
        var journal = sapi.ModLoader.GetModSystem<ModJournal>();
        if (journal == null) return 0;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var codes = new HashSet<string>(RemovedLore.Keys.Select(k => "rf-" + k));
        int removed = 0;

        if (typeof(ModJournal).GetField("journalsByPlayerUid", flags)?.GetValue(journal) is Dictionary<string, Journal> journals)
        {
            var channel = typeof(ModJournal).GetField("serverChannel", flags)?.GetValue(journal) as IServerNetworkChannel;
            foreach (var (uid, j) in journals)
            {
                int n = j.Entries.RemoveAll(en => en?.LoreCode != null && codes.Contains(en.LoreCode));
                if (n == 0) continue;
                removed += n;
                for (int i = 0; i < j.Entries.Count; i++) j.Entries[i].EntryId = i;
                if (sapi.World.PlayerByUid(uid) is IServerPlayer online && online.ConnectionState == EnumClientState.Playing)
                    channel?.SendPacket(j, online);
            }
        }
        if (typeof(ModJournal).GetField("loreDiscoveryiesByPlayerUid", flags)?.GetValue(journal) is IDictionary discoveries)
            foreach (var perPlayer in discoveries.Values)
                if (perPlayer is IDictionary d)
                    foreach (string code in codes) if (d.Contains(code)) d.Remove(code);
        return removed;
    }
}
