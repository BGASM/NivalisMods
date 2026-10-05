using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using NivalisModKit;

namespace NivalisTimeSpeed;

// How fast the in-game clock runs: half, normal or double speed. Uses the kit's shared clock (GameClock), so it
// combines with other mods' speed changes instead of fighting them. Only the clock: NPCs, cooking and walking are
// unchanged.
[BepInPlugin(Guid, "Nivalis Time Speed", "1.0.0")]
[BepInDependency(ModKit.Guid, ">=0.2.0")]
public class Plugin : BasePlugin
{
    const string Guid = "bgasm.nivalis.timespeed";

    public override void Load()
    {
        var speed = Config.Bind("General", "ClockSpeed", 1f, new ConfigDescription(
            "How fast the in-game clock runs: 0.5 = days last twice as long, 1 = normal, 2 = days pass twice as fast.",
            new AcceptableValueList<float>(0.5f, 1f, 2f)));
        void Apply()
        {
            if (speed.Value == 1f) GameClock.ClearClockSpeed(Guid);
            else GameClock.SetClockSpeed(Guid, speed.Value);
            Log.LogInfo($"Clock speed x{speed.Value:0.##}");
        }
        speed.SettingChanged += (_, _) => Apply();
        Apply();
        ModMenu.ListSettings(Guid);
    }
}
