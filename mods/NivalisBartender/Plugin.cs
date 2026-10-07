using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using NivalisModKit;
using UnityEngine;

namespace NivalisBartender;

// Nivalis Bartender: a Bartender job in the venue's staff tab. In the game every drink is a cook's job, so beer (often
// half of all orders) takes cook trips and station time away from food. With this mod, bartenders prep and plate the
// drinks and cooks only food; staff with both jobs take anything, as cooks do in the game. If no bartender is on shift,
// cooks make drinks as before. Which skills make a bartender's drinks is a setting (DrinkSkills.cs).
//
// The kit does the game-facing work: the job in the staff tab and its save safety (StaffJobs), letting bartenders
// take kitchen jobs and choosing among the waiting ones (Kitchen.MayWork / FilterJobs). This mod only decides.
[BepInPlugin(Guid, "Nivalis Bartender", "1.0.0")]
[BepInDependency(ModKit.Guid, ">=0.6.0")]   // StaffJobs, Kitchen, StaffSkills
public class Plugin : BasePlugin
{
    internal const string Guid = "bgasm.nivalis.bartender";
    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled, CooksCoverDrinks, CooksHelpDrinkPrep, Verbose;
    internal static ConfigEntry<DrinkSkill> DrinkSkill;
    internal static ConfigEntry<float> DrinkPrepXp;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true, "Bartenders make the drinks and cooks the food.");
        CooksCoverDrinks = Config.Bind("General", "CooksCoverDrinks", true,
            "If no bartender is on shift at the venue, other cooks make the drinks (so a night without one still serves beer).");
        CooksHelpDrinkPrep = Config.Bind("General", "CooksHelpDrinkPrep", false,
            "Cooks without the Bartender job help with drink prep (blending, etc.) when there's no food prep for them, even with a " +
            "bartender on shift. Plating drinks stays with bartenders.");
        DrinkSkill = Config.Bind("General", "DrinkSkill", NivalisBartender.DrinkSkill.Mixed,
            "Which skills make a bartender's drinks, and who may tend bar. Mixed: prep from cooking, plating (quality, speed, XP) " +
            "from serving; needs both skills. Cooking: all from cooking, as in the game. Serving: all from serving.");
        DrinkPrepXp = Config.Bind("General", "DrinkPrepXp", 0.5f, new ConfigDescription(
            "XP a bartender gets for the prep of one drink (the game gives none for prep), split across the drink's prep steps. " +
            "Goes to cooking, or serving with DrinkSkill = Serving. Plating a drink gives 1, as in the game.",
            new AcceptableValueRange<float>(0f, 2f)));
        Verbose = Config.Bind("Debug", "Verbose", false, "Log changes in who takes which jobs, and each drink a bartender plates.");
        ModMenu.ListSettings(Guid);

        StaffJobs.Register(new StaffJob
        {
            Id = Guid + ".bartender",
            Name = "Bartender",
            IconPng = Resource("NivalisBartender.icon.png"),
            IconOffset = new Vector2(3f, 0f),   // centre on the torso (x 26 of 64 in the art), as the game's icons do
            CanDo = DrinkSkills.Eligible,
            IsOn = Bartenders.Is,
            Set = (p, on) =>
            {
                Bartenders.Set(p, on);
                L.LogInfo($"Bartender: {Routing.Name(p)} {(on ? "now tends the bar" : "leaves the bar")}");
            },
            Badge = DrinkSkills.Badge,
            SaveAs = p => DrinkSkills.HasCooking(p) ? VenueTasks.Cooking : VenueTasks.Serving,
        });
        Kitchen.MayWork += Routing.MayWork;
        Kitchen.FilterJobs += Routing.Filter;
        DrinkSkills.Install();
        // A new DrinkSkill takes effect when the settings menu closes (or at once if changed elsewhere, e.g. the .cfg).
        DrinkSkill.SettingChanged += (_, _) => { if (!ModMenu.IsOpen) DrinkSkills.Commit(); };
        ModMenu.Closed += DrinkSkills.Commit;
        GameEvents.GameReady += Legacy.Restore;
    }

    static byte[] Resource(string name)
    {
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            var bytes = new byte[s.Length];
            int read = 0;
            while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
            return bytes;
        }
        catch (Exception e) { L.LogWarning($"Bartender: icon: {e.Message}"); return null; }
    }
}

// ---------- who is a bartender (kept with the save) ----------

static class Bartenders
{
    static ModSaveData Store => SaveData.For(Plugin.Guid);
    const string Key = "bartenders";

    internal static bool Is(Person p)
    {
        string g = Guid(p);
        return g != null && (Store.Get<List<string>>(Key) ?? new List<string>()).Contains(g);
    }

    internal static void Set(Person p, bool on)
    {
        string g = Guid(p);
        if (g == null) return;
        var list = Store.Get<List<string>>(Key) ?? new List<string>();
        list.Remove(g);
        if (on) list.Add(g);
        Store.Set(Key, list);
    }

    static string Guid(Person p)
    {
        try { return p?.Guid; } catch { return null; }
    }

    internal static bool Cooks(Person p)
    {
        try { return (p?.RuntimeData?.Tasks & VenueTasks.Cooking) != 0; } catch { return false; }
    }

    // Flagged and has the skills the DrinkSkill setting needs (Mixed: cooking and serving).
    internal static bool Active(Person p) => Plugin.Enabled.Value && Is(p) && DrinkSkills.Eligible(p);
}

// ---------- which kitchen jobs each worker takes ----------

static class Routing
{
    // The game gives kitchen jobs only to staff with the Cook job: bartenders without it may take them too
    // (the kit has checked they have work, free hands and are on shift). Filter then keeps them to drinks.
    internal static void MayWork(KitchenMayWorkContext ctx)
    {
        if (Bartenders.Active(ctx.Person)) ctx.Allow = true;
    }

    internal static void Filter(KitchenJobsContext ctx)
    {
        if (!Plugin.Enabled.Value) return;
        var person = ctx.Person;
        bool bartender = Bartenders.Active(person), cook = Bartenders.Cooks(person);
        if (bartender && cook) return;   // both jobs: any job in queue order, as the game does
        string step = ctx.Step == KitchenStep.Prep ? "prep" : "plating";

        if (bartender)
        {
            ctx.RemoveWhere(j => !j.IsDrink);   // bartender only: drinks only
            Log(person, ctx.Jobs.Count > 0 ? $"{Name(person)} (bar) takes {step} for a drink" : $"{Name(person)} (bar): no {step} for them");
            return;
        }
        // A cook at a venue with no bartender on shift (every venue that has none, such as the town's other venues):
        // the game's own order, unless the player set cooks to leave drinks to bartenders.
        if (!AnyBartender(ctx.Area) || CooksMayMakeDrinks(ctx.Area)) return;

        bool anyFood = false;
        foreach (var j in ctx.Jobs) if (!j.IsDrink) { anyFood = true; break; }
        if (anyFood) ctx.RemoveWhere(j => j.IsDrink);   // food first
        else if (!(ctx.Step == KitchenStep.Prep && Plugin.CooksHelpDrinkPrep.Value)) ctx.RemoveAll();
        if (ctx.Jobs.Count > 0) Log(person, $"{Name(person)} (cook) takes {step} for a {(ctx.Jobs[0].IsDrink ? "drink" : "dish")}");
        else Log(person, $"{Name(person)} (cook): no {step} for them");
    }

    static bool AnyBartender(VenueAreaGhost area)
    {
        try
        {
            var staff = area?.staff?.backingList;
            if (staff != null)
                for (int i = 0; i < staff.Count; i++)
                    if (Bartenders.Active(staff[i])) return true;
        }
        catch { }
        return false;
    }

    // Cooks make drinks only when no bartender is on shift at the venue (and the setting allows it).
    static bool CooksMayMakeDrinks(VenueAreaGhost area)
    {
        if (!Plugin.CooksCoverDrinks.Value) return false;
        try
        {
            var staff = area?.staff?.backingList;
            if (staff != null)
                for (int i = 0; i < staff.Count; i++)
                    if (Bartenders.Active(staff[i]) && Staff.IsOnShift(staff[i])) return false;
        }
        catch { }
        return true;
    }

    static void Log(Person p, string line)
    {
        if (Plugin.Verbose.Value) LogChanges.Info(Plugin.L, Name(p), "Bartender: " + line);
    }

    internal static string Name(Person p)
    {
        try { return p?.DisplayedName ?? p?.Name; } catch { return "?"; }
    }
}

// ---------- saves from before the kit's StaffJobs ----------

// Test builds kept their own save safety: bartender-only workers saved with a stand-in game job, listed under "barOnly"
// ("guid|Job", or a bare guid for Cooking). Takes those stand-ins off once, then forgets the list.
static class Legacy
{
    const string BarOnlyKey = "barOnly";

    internal static void Restore()
    {
        try
        {
            var store = SaveData.For(Plugin.Guid);
            var barOnly = store.Get<List<string>>(BarOnlyKey);
            if (barOnly == null) return;
            store.Remove(BarOnlyKey);
            var jobs = new Dictionary<string, VenueTasks>();
            foreach (var e in barOnly)
            {
                var parts = e.Split('|');
                jobs[parts[0]] = parts.Length > 1 && Enum.TryParse<VenueTasks>(parts[1], out var t) ? t : VenueTasks.Cooking;
            }
            foreach (var area in Venues.PlayerOwned)
            {
                var staff = area?.staff?.backingList;
                if (staff == null) continue;
                for (int i = 0; i < staff.Count; i++)
                {
                    var p = staff[i];
                    string g = null;
                    try { g = p?.Guid; } catch { }
                    if (g == null || !jobs.TryGetValue(g, out var job) || !Bartenders.Active(p)) continue;
                    p.RuntimeData.Tasks &= ~job;
                    Plugin.L.LogInfo($"Bartender: {Routing.Name(p)} back behind the bar");
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"Bartender: older save: {e.Message}"); }
    }
}
