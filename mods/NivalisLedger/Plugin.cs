using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using NivalisModKit;
using UnityEngine;

namespace NivalisLedger;

// Nivalis Ledger: a live profitability tracker for the player's venues, as a web page on this machine
// (http://localhost:5720 by default): sales, plate costs and margins, ingredient stock and its value, pending orders,
// staff pay, reviews. The game side is Ledger (bookkeeping) and Server (the page).
[BepInPlugin(Guid, "Nivalis Ledger", "0.1.5")]
[BepInDependency(ModKit.Guid, ">=0.6.2")]   // Venues.MenuOf (venue recipes, 0.6.2) / StockOf / OrdersOf / StaffOf (with mod jobs, 0.6.1) / ReviewsOf / ReceiptsOf
public class Plugin : BasePlugin
{
    const string Guid = "bgasm.nivalis.ledger";

    internal static ManualLogSource L;
    static ConfigEntry<int> port;

    public override void Load()
    {
        L = Log;
        port = Config.Bind("Web", "Port", 5720, new ConfigDescription(
            "Port for the Ledger page (http://localhost:PORT). Only this computer can open it. Restart to apply.",
            new AcceptableValueRange<int>(1024, 65535), new ConfigurationManagerAttributes { RequiresRestart = true }));
        ModMenu.ListSettings(Guid);

        Ledger.Install();
        bool listening = Server.Start(port.Value);
        if (listening) L.LogInfo($"Ledger: open {Server.Url} in a browser");
        ModMenu.AddPage(Guid, "Nivalis Ledger", w =>
        {
            w.AddText(listening
                ? $"Your venues' sales, costs, stock, orders, staff pay and reviews, live in your browser at {Server.Url}"
                : $"The Ledger page couldn't start on port {port.Value}; change [Web] Port and restart.");
            if (listening) w.AddButton("Open in browser", () => Application.OpenURL(Server.Url));
        });
        AddComponent<LedgerBehaviour>();
    }
}

// Runs the bookkeeping every frame (it only works once a second).
internal class LedgerBehaviour : MonoBehaviour
{
    public LedgerBehaviour(IntPtr ptr) : base(ptr) { }

    public void Update() => Ledger.Tick();
}
