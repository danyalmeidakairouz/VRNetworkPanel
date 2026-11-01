using System;
using System.Runtime.InteropServices;

namespace VRNetworkPanel
{
    /// <summary>
    /// Minimal edge-triggered hotkey polling. Pure Win32 with zero game dependencies, and it only
    /// ever *reads* key state.
    ///
    /// Why not UnityEngine.Input: V Rising is on the new Input System, so the legacy
    /// UnityEngine.Input bridge is not dependable under IL2CPP. GetAsyncKeyState always works.
    /// The cost is that it is global, so presses are ignored unless the foreground window
    /// belongs to this process - otherwise the hotkeys would fire while you are alt-tabbed.
    /// </summary>
    internal sealed class Hotkeys
    {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private static readonly uint OwnPid = GetCurrentProcessId();

        private readonly bool[] _down = new bool[256];
        private bool _focused;

        // Seeded from the live key state, so a key already held when this is built is not a
        // press - on a hot reload the new copy is built mid-press of whatever key triggered it.
        public Hotkeys()
        {
            for (int vk = 1; vk <= 0xFF; vk++)
                _down[vk] = (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>Call once per frame before any <see cref="WasPressed"/> call.</summary>
        public void Update()
        {
            // The call fails (returns 0) without writing pid during brief focus transitions and
            // on the lock screen; reading pid then would be reading an unwritten stack slot.
            _focused = GetWindowThreadProcessId(GetForegroundWindow(), out uint pid) != 0
                       && pid == OwnPid;
        }

        /// <summary>True on the frame the key transitions from up to down.</summary>
        public bool WasPressed(int vk)
        {
            if (vk <= 0 || vk > 0xFF) return false;
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool was = _down[vk];
            _down[vk] = down;
            // Edge is tracked even while unfocused, so alt-tabbing back does not fire a press
            // that was consumed by another window.
            return down && !was && _focused;
        }

        /// <summary>True while the key is held (for auto-repeat). Reads as up while unfocused.</summary>
        public bool IsDown(int vk)
        {
            if (vk <= 0 || vk > 0xFF || !_focused) return false;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>
        /// Virtual-key code for a key name, or 0 if unrecognised. Accepts "F6", "A", "5",
        /// "Space", "Insert", "Numpad0", ... Case-insensitive. Empty means "unbound".
        /// </summary>
        public static int Parse(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            name = name.Trim();

            // F1 - F24
            if ((name[0] == 'F' || name[0] == 'f') && name.Length > 1
                && int.TryParse(name.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                return 0x6F + fn;

            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c;
            }

            if (name.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.Substring(6), out int np) && np >= 0 && np <= 9)
                return 0x60 + np;

            switch (name.ToLowerInvariant())
            {
                case "space": return 0x20;
                case "tab": return 0x09;
                case "enter": case "return": return 0x0D;
                case "backspace": return 0x08;
                case "escape": case "esc": return 0x1B;
                case "insert": return 0x2D;
                case "delete": case "del": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": return 0x21;
                case "pagedown": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "capslock": return 0x14;
                case "leftshift": return 0xA0;
                case "rightshift": return 0xA1;
                case "leftcontrol": case "leftctrl": return 0xA2;
                case "rightcontrol": case "rightctrl": return 0xA3;
                case "leftalt": return 0xA4;
                case "rightalt": return 0xA5;
                default: return 0;
            }
        }

        /// <summary>
        /// The reverse of <see cref="Parse"/>, or null if there is no name for the code. Every
        /// name returned here MUST round-trip - <c>Parse(NameOf(vk)) == vk</c> - or the settings
        /// panel would save a key the plugin then refuses to read back, and the only symptom
        /// would be a hotkey that silently does nothing.
        /// </summary>
        public static string NameOf(int vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Numpad" + (vk - 0x60);
            switch (vk)
            {
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x2D: return "Insert";
                case 0x14: return "CapsLock";
                case 0xA0: return "LeftShift";
                case 0xA1: return "RightShift";
                case 0xA2: return "LeftControl";
                case 0xA3: return "RightControl";
                case 0xA4: return "LeftAlt";
                case 0xA5: return "RightAlt";
                default: return null;
            }
        }

        /// <summary>
        /// Every key the settings panel will capture as a binding: exactly the set NameOf can
        /// name. The panel's own control keys are deliberately ABSENT - Escape (cancels a
        /// capture), Backspace (unbinds), Enter, Delete, the arrows, Home/End, PageUp/PageDown.
        /// A hotkey captured onto one of those fights the panel; MenuKey = Enter would make every
        /// row impossible to edit again, since the menu key is tested before the panel sees the
        /// frame. Parse still reads them all, so the config file stays fully expressive.
        /// </summary>
        public static readonly int[] Bindable = BuildBindable();

        private static int[] BuildBindable()
        {
            var list = new System.Collections.Generic.List<int>(80);
            for (int f = 0x70; f <= 0x87; f++) list.Add(f);       // F1 - F24
            for (int c = 'A'; c <= 'Z'; c++) list.Add(c);
            for (int d = '0'; d <= '9'; d++) list.Add(d);
            for (int n = 0x60; n <= 0x69; n++) list.Add(n);       // Numpad0 - Numpad9
            list.AddRange(new[] { 0x20, 0x09, 0x2D, 0x14, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 });
            return list.ToArray();
        }
    }
}
