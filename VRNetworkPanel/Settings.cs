using BepInEx.Configuration;

namespace VRNetworkPanel
{
    /// <summary>ConfigEntry definitions only. Every value is read at its use site each frame, so
    /// nothing here is parsed or cached - except the key names, which Plugin re-parses.
    ///
    /// THE SETTINGS PANEL REFLECTS OVER THIS CLASS: every public ConfigEntry field becomes a row,
    /// in declaration order within its section, so a new setting appears there for free. The
    /// AcceptableValueRange / AcceptableValueList on an entry is what the panel steps within or
    /// cycles through - an entry with neither is a free number or a key name.</summary>
    internal sealed class Settings
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> MenuKey;
        public ConfigEntry<string> ToggleKey;

        public ConfigEntry<string> Corner;
        public ConfigEntry<int> FontSize;
        public ConfigEntry<float> SampleMs;
        public ConfigEntry<bool> ShowTraffic;
        public ConfigEntry<bool> Background;

        public Settings(ConfigFile cfg)
        {
            Enabled = cfg.Bind("1. General", "Enabled", true,
                "Show a thin Dota 2 style readout of FPS, ping, jitter and packet loss in / out. Reads only and writes nothing to the game. Packet loss comes from the Steam connection's own delivery-quality figures (IN = the server's packets that failed to reach you, OUT = yours that failed to reach the server); on a server that does not use the Steam transport, loss shows '-' and ping falls back to the game's own latency figure, drawn grey with a '?' because its unit is not yet confirmed. Values turn yellow then red: ping at 80 / 150 ms, jitter at 10 / 30 ms, loss at 1% / 5%, FPS below 50 / 30. JITTER is the average size of each change in your ping over the last 5 seconds - '-' for the first 2 seconds after connecting, 0 if ping has not moved. Steam's ping is itself a smoothed average, so this is the steadiness of that average, not of individual packets: it reads lower than true packet jitter and cannot see packets arriving in bursts. A one-off jump in ping (a route change) also lifts it for up to 5 seconds.");
            MenuKey = cfg.Bind("1. General", "MenuKey", "F1",
                "Key that opens / closes the in-game settings panel. Single keys only (F1-F24, A-Z, 0-9, Space, Insert, Numpad0-9, ...); modifier combinations like Ctrl+F1 are not supported. Empty disables the panel.");
            ToggleKey = cfg.Bind("1. General", "ToggleKey", "",
                "Key that shows / hides the readout. Flips Enabled and saves it. Empty by default so it cannot clash with another mod's key; set one if you want it.");

            Corner = cfg.Bind("2. Display", "Corner", "TopLeft",
                new ConfigDescription("Where the readout sits on screen.",
                    new AcceptableValueList<string>("TopLeft", "TopCenter", "TopRight",
                                                    "BottomLeft", "BottomCenter", "BottomRight")));
            FontSize = cfg.Bind("2. Display", "FontSize", 13,
                new ConfigDescription("Text size of the readout. Shrunk further if the strip would not fit the screen width.",
                    new AcceptableValueRange<int>(8, 40)));
            SampleMs = cfg.Bind("2. Display", "SampleMs", 500f,
                new ConfigDescription("How often the shown ping, jitter, loss and traffic numbers change, in ms. The connection itself is read about ten times a second regardless, so jitter can see each ping change; this only sets how often the digits move, so lowering it mostly adds flicker. FPS is averaged over half a second regardless.",
                    new AcceptableValueRange<float>(100f, 5000f)));
            ShowTraffic = cfg.Bind("2. Display", "ShowTraffic", false,
                "Add a second line with incoming and outgoing traffic in KB/s and packets per second. Steam transport only; shows '-' otherwise.");
            Background = cfg.Bind("2. Display", "Background", true,
                "Draw a translucent dark strip behind the readout so it stays legible over bright scenery.");
        }
    }
}
