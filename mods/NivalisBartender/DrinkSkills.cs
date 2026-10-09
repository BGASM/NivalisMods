using NivalisModKit;
using Nivalis.GhostSystem.Ai;
using Nivalis.SkillSystem;

namespace NivalisBartender;

// Which of a bartender's skills makes their drinks.
public enum DrinkSkill
{
    Mixed,     // prep from cooking, plating (quality, XP) from serving; needs both skills
    Cooking,   // everything from cooking, as for any cook in the game
    Serving,   // everything from serving
}

// In the game a meal's quality is set when it's plated: the plater's cooking PreparationQuality times their happiness
// influence, and plating gives them 1 cooking XP. Prep time is scaled by the cook's cooking time (StaffSkills.CookingTime,
// a multiplier); plating takes a flat 6 seconds for everyone. For drinks made by a bartender, this swaps in the serving
// skill where the setting says so: the same happiness influence times ServingLevel.ServiceQuality (the value a waiter's
// service adds to a review), prep time scaled by 1 / serving ActionSpeed (a speed), XP to serving. Drink prep also gives the bartender XP (DrinkPrepXp per drink,
// split across its prep steps), which the game gives no one for prep. The kit's Kitchen events carry it out.
static class DrinkSkills
{
    // The setting as last committed: a change applies when the settings menu closes, so trying values on the way
    // doesn't take the job off people at every click (and going back to the old value changes nothing).
    static DrinkSkill? committed;
    static DrinkSkill Mode => committed ??= Plugin.DrinkSkill.Value;

    // Applies a changed DrinkSkill: bartenders without the skills it needs lose the job (a game job if it was their
    // only one), and the staff tab redraws.
    internal static void Commit()
    {
        var now = Plugin.DrinkSkill.Value;
        if (committed == now) return;
        committed = now;
        try
        {
            foreach (var area in Venues.PlayerOwned)
            {
                var staff = area?.staff?.backingList;
                if (staff == null) continue;
                for (int i = 0; i < staff.Count; i++)
                {
                    var p = staff[i];
                    if (p == null || !Bartenders.Is(p) || Eligible(p)) continue;
                    Bartenders.Set(p, false);
                    Plugin.L.LogInfo($"Bartender: {Routing.Name(p)} lacks the skills for {now} bartending: Bartender job off");
                }
            }
        }
        catch (System.Exception e) { Plugin.L.LogWarning($"Bartender: applying DrinkSkill: {e.Message}"); }
        StaffJobs.CheckJobless();
        StaffJobs.Refresh();
        Plugin.L.LogInfo($"Bartender: drinks now use {now}");
    }

    internal static void Install()
    {
        Kitchen.StepStarting += StepStarting;
        Kitchen.MealPlated += MealPlated;
        Kitchen.PrepCompleted += PrepCompleted;
    }

    internal static bool HasCooking(Person p) => StaffSkills.Has(p, StaffSkills.Cooking);
    internal static bool HasServing(Person p) => StaffSkills.Has(p, StaffSkills.Serving);

    // Who may tend bar: the skills the setting uses. Mixed needs both; the pure settings need theirs.
    internal static bool Eligible(Person p) => Mode switch
    {
        DrinkSkill.Cooking => HasCooking(p),
        DrinkSkill.Serving => HasServing(p),
        _ => HasCooking(p) && HasServing(p),
    };

    // The job's badge: the levels of the skills the setting uses ("serving/cooking" for Mixed).
    internal static string Badge(Person p)
    {
        int cook = StaffSkills.Level(p, StaffSkills.Cooking), serve = StaffSkills.Level(p, StaffSkills.Serving);
        return Mode switch
        {
            DrinkSkill.Cooking => cook.ToString(),
            DrinkSkill.Serving => serve.ToString(),
            _ => $"{serve}/{cook}",
        };
    }

    static bool Applies(Person p, bool isDrink) => isDrink && Bartenders.Active(p);

    // Prep time multipliers (lower is faster): the game's for cooking, the serving speed's inverse for serving.
    static float ServeTime(Person p)
    {
        var s = StaffSkills.ServingOf(p);
        return s != null && s.ActionSpeed > 0f ? 1f / s.ActionSpeed : StaffSkills.NoSkillTime;
    }

    // Serving-mode prep runs at the serving skill's pace. Plating is a flat 6 seconds in the game, left as it is.
    static void StepStarting(KitchenStepContext ctx)
    {
        if (ctx.Step != KitchenStep.Prep || Mode != DrinkSkill.Serving || !Applies(ctx.Person, ctx.IsDrink)) return;
        float cook = StaffSkills.CookingTime(ctx.Person);
        if (cook > 0f) ctx.TimeScale *= ServeTime(ctx.Person) / cook;
    }

    static void MealPlated(MealPlatedContext ctx)
    {
        if (!Applies(ctx.Person, ctx.IsDrink) || Mode == DrinkSkill.Cooking) return;
        var serving = StaffSkills.ServingOf(ctx.Person);
        if (serving == null) return;   // can't happen: these settings need the skill to tend bar
        ctx.Quality = StaffSkills.HappinessInfluence(ctx.Person) * serving.ServiceQuality;
        ctx.ExperienceSkill = StaffSkills.Serving;
        if (Plugin.Verbose.Value)
            Plugin.L.LogInfo($"Bartender: {Routing.Name(ctx.Person)} plated a drink: quality {ctx.Quality:0.00} from serving " +
                             $"(cooking gave {ctx.GameQuality:0.00}); {StaffSkills.Describe(ctx.Person, StaffSkills.Serving)}");
    }

    static void PrepCompleted(PrepCompletedContext ctx)
    {
        if (!Applies(ctx.Person, ctx.IsDrink)) return;
        SkillDefinition skill = Mode == DrinkSkill.Serving ? StaffSkills.Serving : StaffSkills.Cooking;
        ctx.ExperienceSkill = skill;
        ctx.Experience = Plugin.DrinkPrepXp.Value / ctx.PrepSteps;   // per drink, however many prep steps it has
        if (Plugin.Verbose.Value)
            Plugin.L.LogInfo($"Bartender: {Routing.Name(ctx.Person)} prepped a drink (1 of {ctx.PrepSteps} steps): +{ctx.Experience:0.##} XP; " +
                             StaffSkills.Describe(ctx.Person, skill));
    }
}
