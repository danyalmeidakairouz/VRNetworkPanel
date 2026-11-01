using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace VRNetworkPanel
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("VRising.exe")]
    public class Plugin : BasePlugin
    {
        // The GUID determines the config filename. Changing it orphans the existing config.
        public const string GUID = "vrnetworkpanel";
        public const string NAME = "VRNetworkPanel";
        public const string VERSION = "1.1.2";

        internal static ManualLogSource Logger;
        internal static Settings Cfg;

        private PanelDriver _driver;
        private Action[] _slots;

        // Hot reload (Bloodpebble) cannot simply run Load() again: Il2CppInterop refuses a second
        // injection of a type with the same FullName, and the new copy's PanelHost IS a different
        // Type with the same name. So the host MonoBehaviour is injected exactly ONCE per process,
        // and every later load only swaps its own driver into the host's slots.
        public override void Load()
        {
            Logger = Log;
            Cfg = new Settings(Config);

            var slots = AppDomain.CurrentDomain.GetData(PanelHost.SlotsKey) as Action[];
            bool injected = ClassInjector.IsTypeRegisteredInIl2Cpp<PanelHost>();
            bool hostAlive = AppDomain.CurrentDomain.GetData(PanelHost.AliveKey) is true;

            if (slots != null && Array.Exists(slots, s => s != null))
            {
                // A correct Unload() leaves every slot empty, so a filled one is a live copy.
                Log.LogError($"{NAME} {VERSION} is not starting: another copy is already running. "
                           + "Most likely the DLL is in both BepInEx/plugins and "
                           + "BepInEx/BloodpebblePlugins - keep it in one of them.");
                return;
            }
            if (injected && !hostAlive)
            {
                Log.LogError($"{NAME} {VERSION} is not starting: an earlier load registered its frame "
                           + "host but never got it running, and a host cannot be registered twice. "
                           + "Restart the game; that earlier load's error is further up this log.");
                return;
            }
            if (slots == null)
            {
                slots = new Action[PanelHost.SlotCount];
                AppDomain.CurrentDomain.SetData(PanelHost.SlotsKey, slots);
            }

            _driver = new PanelDriver();
            try
            {
                if (!injected)
                {
                    ClassInjector.RegisterTypeInIl2Cpp<PanelHost>();
                    var host = AddComponent<PanelHost>();
                    // Nothing here uses GUILayout, so Unity's Layout pass - a whole extra OnGUI
                    // call every frame - is pure overhead. Unproven in this game, hence the try:
                    // failing costs only the saving. Takes effect for the host's lifetime, i.e.
                    // from a fresh game launch; a hot reload reuses the host already running.
                    try { host.useGUILayout = false; }
                    catch (Exception e) { Log.LogWarning($"Could not skip the IMGUI layout pass: {e.Message}"); }
                }
                // Last, so a throw above leaves the slots empty for the next load.
                _driver.Install(slots);
                _slots = slots;
            }
            catch
            {
                _driver.Shutdown();
                _driver = null;
                throw;
            }

            Log.LogInfo($"{NAME} {VERSION} loaded" + (injected ? " (hot reload - reusing the frame "
                      + "host a previous load created)." : ".")
                      + " Read-only: it shows FPS and connection figures and writes nothing to the game.");
        }

        public override bool Unload()
        {
            if (_driver == null) return true; // Load() stood down, nothing was started
            _driver.Uninstall(_slots);
            _driver.Shutdown();
            _driver = null;
            Log.LogInfo($"{NAME} {VERSION} unloaded.");
            return true;
        }
    }

    /// <summary>
    /// The one injected MonoBehaviour: a logic-free frame host that forwards Unity's callbacks to
    /// whichever load of this plugin currently owns it.
    ///
    /// THE SLOT CONTRACT IS FROZEN. The host that runs is the one compiled into the FIRST copy
    /// loaded this session, and later copies talk to it only through this array and the alive
    /// flag in AppDomain data - typed with BCL types only, because a type from this assembly is a
    /// different type in every copy. Changing keys, indices or meaning breaks hot reload from
    /// every earlier version; if it must change, bump the keys' version.
    ///
    /// Part of the same contract: since 1.1.0 the host runs with useGUILayout = false, so no
    /// Layout event ever reaches OnGUI. A driver must never use GUILayout - a hot-reloaded copy
    /// that did would get no layout pass from a host created by an earlier copy.
    /// </summary>
    public class PanelHost : MonoBehaviour
    {
        internal const string SlotsKey = "VRNetworkPanel.HostSlots.v1";
        internal const string AliveKey = "VRNetworkPanel.HostAlive.v1";
        internal const int SlotUpdate = 0, SlotOnGUI = 1, SlotOnDestroy = 2;
        internal const int SlotCount = 3;

        public PanelHost(IntPtr ptr) : base(ptr) { }

        private Action[] _slots;

        // Fetched on first use rather than in Awake, so the host cannot end up with a null array
        // whatever order the constructor and Awake run in. Hidden from Il2Cpp: not a Unity
        // message, and a System.Action return would draw an "unsupported return type" warning.
        [HideFromIl2Cpp]
        private Action Slot(int i)
        {
            _slots ??= AppDomain.CurrentDomain.GetData(SlotsKey) as Action[];
            return _slots?[i];
        }

        void Awake() => AppDomain.CurrentDomain.SetData(AliveKey, true);
        void Update() => Slot(SlotUpdate)?.Invoke();
        void OnGUI() => Slot(SlotOnGUI)?.Invoke();
        void OnDestroy()
        {
            AppDomain.CurrentDomain.SetData(AliveKey, false);
            Slot(SlotOnDestroy)?.Invoke();
        }
    }

    /// <summary>Everything the plugin does each frame. A plain object so a hot reload can
    /// replace it - see <see cref="PanelHost"/>.</summary>
    internal sealed class PanelDriver
    {
        private readonly Hotkeys _keys = new Hotkeys();
        private readonly NetPanel _panel;
        private readonly SettingsPanel _menu;
        private int _menuKey, _toggleKey;
        private string _menuCfg, _toggleCfg;

        // Update runs every frame, OnGUI several times a frame: a feature that throws there would
        // flood the log. Update errors are throttled with a held-back count; draw errors are
        // reported once a session.
        private const float ErrorLogSeconds = 5f;
        private float _errNextAt;
        private int _errSuppressed;
        private string _errLast;
        private bool _guiErrorLogged;
        private bool _menuGuiErrorLogged;
        private bool _shutDown;

        internal PanelDriver()
        {
            _panel = new NetPanel(Plugin.Cfg, Plugin.Logger);
            // Defensively: its constructor reflects over Settings, and a throw there must cost
            // the settings panel, not the readout. Same fail-open rule as VRVisionBoost's menu.
            try { _menu = new SettingsPanel(Plugin.Cfg, Plugin.Logger); }
            catch (Exception e)
            {
                _menu = null;
                Plugin.Logger.LogError("Settings panel could not be built, so the menu key will do "
                                     + $"nothing. The readout and the config file still work: {Describe(e)}");
            }
            ParseHotkeys();
        }

        internal void Install(Action[] slots)
        {
            slots[PanelHost.SlotUpdate] = Update;
            slots[PanelHost.SlotOnGUI] = OnGUI;
            slots[PanelHost.SlotOnDestroy] = Shutdown;
        }

        /// <summary>Clear only slots that are still ours - a newer load may already own them.</summary>
        internal void Uninstall(Action[] slots)
        {
            if (slots == null) return;
            for (int i = 0; i < slots.Length; i++)
                if (ReferenceEquals(slots[i]?.Target, this)) slots[i] = null;
        }

        /// <summary>
        /// Key names are the only DERIVED config values, so they self-invalidate against the live
        /// .Value (an ordinal compare per frame, no allocation): an edit picked up by any config
        /// reload then applies without a restart. The log reports what was actually bound, never
        /// the configured string, so an unparseable name cannot pass for a working hotkey.
        /// </summary>
        private void ParseHotkeys()
        {
            _menuCfg = Plugin.Cfg.MenuKey.Value;
            _toggleCfg = Plugin.Cfg.ToggleKey.Value;
            _menuKey = BindOptional(_menuCfg, "MenuKey", out string menu);
            _toggleKey = BindOptional(_toggleCfg, "ToggleKey", out string toggle);

            // WasPressed consumes the edge, and the menu key is tested first in Update(), so on
            // a shared key only the menu fires.
            if (_menuKey != 0 && _menuKey == _toggleKey)
                Plugin.Logger.LogWarning($"MenuKey and ToggleKey are both bound to {menu} - only "
                                       + "MenuKey will fire. Give them different keys.");
            Plugin.Logger.LogInfo($"Hotkeys - menu: {menu}  toggle: {toggle}");
        }

        private static int BindOptional(string configured, string label, out string effective)
        {
            int vk = Hotkeys.Parse(configured);
            effective = vk != 0 ? configured.Trim() : "(none)";
            if (vk == 0 && !string.IsNullOrWhiteSpace(configured))
                Plugin.Logger.LogWarning($"{label} '{configured}' is not a key name this plugin "
                                       + "understands, so it is unbound. Single keys only (F1-F24, "
                                       + "A-Z, 0-9, Space, Insert, Numpad0-9, ...); modifier "
                                       + "combinations like Ctrl+F3 are not supported.");
            return vk;
        }

        void Update()
        {
            try
            {
                // Also how a key rebound in the settings panel takes effect: the panel writes the
                // entry, and the compare here sees the new string on the next frame.
                if (!string.Equals(_menuCfg, Plugin.Cfg.MenuKey.Value, StringComparison.Ordinal)
                    || !string.Equals(_toggleCfg, Plugin.Cfg.ToggleKey.Value, StringComparison.Ordinal))
                    ParseHotkeys();

                _keys.Update();

                // Not while capturing: the menu key is tested before the panel sees the frame,
                // so it would otherwise close the panel instead of being captured as a binding.
                if (_menu != null && !_menu.CapturingKey && _keys.WasPressed(_menuKey))
                    _menu.Toggle();

                // An open panel owns the keyboard, so the toggle is skipped while it is.
                bool menuOpen = _menu != null && _menu.Update(_keys);
                if (!menuOpen && _keys.WasPressed(_toggleKey))
                {
                    // Writing .Value persists it. NetPanel notices on its next frame and clears
                    // its reading; it writes nothing to the game, so there is nothing to revert.
                    bool on = !Plugin.Cfg.Enabled.Value;
                    Plugin.Cfg.Enabled.Value = on;
                    Plugin.Logger.LogInfo($"Network panel {(on ? "ON" : "OFF")}");
                }

                _panel.Update();
            }
            catch (Exception e) { LogThrottled(e); }
            FlushThrottled(force: false);
        }

        void OnGUI()
        {
            // Both panels draw in the Repaint pass only. Checked once here rather than in each
            // Draw: every Event.current read hands back a fresh wrapper, and OnGUI runs at least
            // twice a frame. A null event means IMGUI did not say which pass this is - draw.
            // Inside a try: outside one, a throw here would escape into the host and be logged by
            // Unity on every OnGUI call, several times a frame, with no throttle.
            try
            {
                Event ev = Event.current;
                if (ev != null && ev.type != EventType.Repaint) return;
            }
            catch { return; }

            try { _panel.Draw(); }
            catch (Exception e)
            {
                // No early return here - that would also skip the settings panel below.
                if (!_guiErrorLogged)
                {
                    _guiErrorLogged = true;
                    Plugin.Logger.LogError("Network panel drawing failed. It is still retried every "
                                         + "frame, but this is reported only once a session: "
                                         + Describe(e));
                }
            }

            // After the readout, so it draws on top. Its own once-a-session flag: one of the two
            // failing must not silence the other.
            if (_menu == null) return;
            try { _menu.Draw(); }
            catch (Exception e)
            {
                if (_menuGuiErrorLogged) return;
                _menuGuiErrorLogged = true;
                Plugin.Logger.LogError("Settings panel drawing failed. It is still retried every "
                                     + $"frame, but this is reported only once a session: {Describe(e)}");
            }
        }

        /// <summary>Host OnDestroy at game exit, or Plugin.Unload on a hot reload. Run once.</summary>
        internal void Shutdown()
        {
            if (_shutDown) return;
            _shutDown = true;
            // Only the background texture: it is HideAndDontSave, so a reload would leak it.
            try { _panel.Dispose(); }
            catch (Exception e) { Plugin.Logger.LogError($"Network panel dispose failed: {Describe(e)}"); }
            try { _menu?.Dispose(); }
            catch (Exception e) { Plugin.Logger.LogError($"Settings panel dispose failed: {Describe(e)}"); }
            FlushThrottled(force: true);
        }

        /// <summary>Message plus inner cause - a TypeInitializationException's own message hides
        /// the actual reason.</summary>
        private static string Describe(Exception e)
            => e.InnerException == null ? e.Message : $"{e.Message} -- cause: {e.InnerException.Message}";

        /// <summary>First error at once, then at most one line per ErrorLogSeconds carrying how
        /// many identical ones were held back. A different message is printed at once.</summary>
        private void LogThrottled(Exception e)
        {
            string msg = Describe(e);
            float now = Time.unscaledTime;
            bool same = string.Equals(msg, _errLast, StringComparison.Ordinal);
            if (same && now < _errNextAt) { _errSuppressed++; return; }

            string more = "";
            if (same && _errSuppressed > 0) more = $" (+{_errSuppressed} more since last report)";
            else FlushThrottled(force: true);

            Plugin.Logger.LogError($"Network panel error: {msg}{more}");
            _errLast = msg;
            _errSuppressed = 0;
            _errNextAt = now + ErrorLogSeconds;
        }

        private void FlushThrottled(bool force)
        {
            if (_errSuppressed == 0 || (!force && Time.unscaledTime < _errNextAt)) return;
            Plugin.Logger.LogError($"Network panel error repeated {_errSuppressed} more time(s) "
                                 + $"since last report: {_errLast}");
            _errSuppressed = 0;
        }
    }
}
