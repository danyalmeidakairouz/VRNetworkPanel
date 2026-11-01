using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VRNetworkPanel
{
    /// <summary>
    /// A small in-game settings panel (MenuKey, F1 by default), ported down from VRVisionBoost's
    /// F4 ConfigMenu. Three rules carried over from there, each paid for in game:
    ///
    /// 1. <b>It draws with GUI.Label and GUI.DrawTexture, and NOTHING else.</b> Those are the
    ///    only IMGUI calls measured to survive il2cpp stripping in this game. GUI.Button,
    ///    GUI.Toggle, GUI.TextField, GUI.BeginScrollView and GUILayout all compile against the
    ///    interop assembly and throw "Method unstripping failed" at runtime - VRVisionBoost
    ///    shipped a broken panel that way. No GUIStyle.CalcSize either: unproven.
    ///
    /// 2. <b>All input is Win32; Event.current drives nothing.</b> No IMGUI mouse or keyboard
    ///    event reaches this build (measured), so the panel is keyboard-only, read through
    ///    GetAsyncKeyState like the hotkeys. Event.current is asked only which pass this is.
    ///
    /// 3. <b>Rows are discovered by reflection over <see cref="Settings"/>, never listed.</b>
    ///    A hand-listed menu drifts from the config file. Bools flip, key names are captured as
    ///    a single keypress, AcceptableValueList entries cycle, AcceptableValueRange entries step.
    ///    There is deliberately no typing editor: a handful of settings do not need one, and it was the
    ///    largest and most layout-dependent part of the original.
    ///
    /// It writes only ConfigEntry.BoxedValue (BepInEx saves on set), so there is nothing to revert.
    /// </summary>
    internal sealed class SettingsPanel
    {
        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        public bool Visible { get; private set; }

        /// <summary>True while waiting for a keypress to bind. Plugin skips its menu-key test
        /// then, or the menu key could never be rebound from the menu - it would close it.</summary>
        public bool CapturingKey { get; private set; }

        private enum Kind { Bool, Key, Choice, Number }

        private sealed class Row
        {
            internal ConfigEntryBase Entry;
            internal string Section;
            internal string Key;    // the config key - used for logic, never shown
            internal string Label;  // what the row shows: Humanize(Key), built once
            internal string Tip;
            internal Kind Kind;
            internal bool IsInt;
            internal string[] Choices;        // Kind.Choice
            internal float Min, Max, Step;    // Kind.Number
        }

        private readonly List<Row> _rows = new();
        private int _sectionCount;
        private int _selected;

        // Virtual-key codes the panel itself uses. None of them is in Hotkeys.Bindable.
        private const int VkBack = 0x08, VkEnter = 0x0D, VkEsc = 0x1B;
        private const int VkEnd = 0x23, VkHome = 0x24;
        private const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
        private const int VkDelete = 0x2E;
        private static readonly int[] NavKeys =
            { VkBack, VkEnter, VkEsc, VkEnd, VkHome, VkLeft, VkUp, VkRight, VkDown, VkDelete };

        private bool _primeInput;
        private int _repeatVk;
        private float _nextRepeat;

        private GUIStyle _label, _value, _header, _hint;
        private Texture2D _texPanel, _texRow, _texSel, _texAccent, _texLine;
        private bool _stylesFailed;

        public SettingsPanel(Settings cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
            BuildRows();
        }

        // ================================================================= rows

        private void BuildRows()
        {
            var bySection = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.GetValue(_cfg) is not ConfigEntryBase entry) continue;

                var row = new Row
                {
                    Entry = entry,
                    Section = entry.Definition.Section,
                    Key = entry.Definition.Key,
                    Label = Humanize(entry.Definition.Key),
                    Tip = entry.Description?.Description ?? string.Empty,
                };
                Type t = entry.SettingType;
                AcceptableValueBase acceptable = entry.Description?.AcceptableValues;

                if (t == typeof(bool))
                {
                    row.Kind = Kind.Bool;
                }
                else if (t == typeof(string) && acceptable is AcceptableValueList<string> list)
                {
                    row.Kind = Kind.Choice;
                    row.Choices = list.AcceptableValues;
                }
                else if (t == typeof(string))
                {
                    // Every free-text setting in this mod is a key name. If a non-key string is
                    // ever added, it needs an AcceptableValueList or a typing editor - capture
                    // would silently turn it into a key name.
                    row.Kind = Kind.Key;
                }
                else if (t == typeof(int) || t == typeof(float))
                {
                    row.Kind = Kind.Number;
                    row.IsInt = t == typeof(int);
                    if (acceptable is AcceptableValueRange<int> ri) { row.Min = ri.MinValue; row.Max = ri.MaxValue; }
                    else if (acceptable is AcceptableValueRange<float> rf) { row.Min = rf.MinValue; row.Max = rf.MaxValue; }
                    else { row.Min = 0f; row.Max = float.MaxValue; }
                    row.Step = row.IsInt ? 1f : Mathf.Max(1f, (row.Max - row.Min) / 98f);
                    if (!row.IsInt && row.Max < float.MaxValue) row.Step = RoundStep(row.Step);
                }
                else
                {
                    continue; // nothing in Settings today; skip rather than draw an uneditable row
                }

                if (!bySection.TryGetValue(row.Section, out List<Row> rows))
                    bySection[row.Section] = rows = new List<Row>();
                rows.Add(row);
            }

            // Sections are numbered ("1. General", "2. Display"); sort by that number so a
            // "10." would not land between "1." and "2." the way an ordinal sort puts it.
            var names = new List<string>(bySection.Keys);
            names.Sort((a, b) =>
            {
                int c = SectionNumber(a).CompareTo(SectionNumber(b));
                return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
            });
            foreach (string s in names) _rows.AddRange(bySection[s]);
            _sectionCount = names.Count;
        }

        /// <summary>A step that reads cleanly: 1, 2, 5, 10, 20, 50, ... (SampleMs 100-5000 -> 50).</summary>
        private static float RoundStep(float raw)
        {
            float p = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float m = raw / p;
            return (m < 1.5f ? 1f : m < 3.5f ? 2f : m < 7.5f ? 5f : 10f) * p;
        }

        /// <summary>
        /// A config key as a readable label: "MenuKey" -> "Menu Key", "ShowTraffic" -> "Show
        /// Traffic". A space goes before a capital that follows a lower-case letter or digit, and
        /// before the last capital of a run that starts a new word ("FPSLimit" -> "FPS Limit").
        /// A trailing unit "Ms" reads as "(ms)": "SampleMs" -> "Sample (ms)". Display only - the
        /// key itself is what the config file and the code use.
        /// </summary>
        internal static string Humanize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key ?? string.Empty;
            var sb = new System.Text.StringBuilder(key.Length + 4);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (i > 0 && char.IsUpper(c))
                {
                    char prev = key[i - 1];
                    bool afterLower = char.IsLower(prev) || char.IsDigit(prev);
                    bool endsAcronym = char.IsUpper(prev) && i + 1 < key.Length && char.IsLower(key[i + 1]);
                    if (afterLower || endsAcronym) sb.Append(' ');
                }
                sb.Append(c);
            }
            string s = sb.ToString();
            if (s.EndsWith(" Ms", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3) + " (ms)";
            return s;
        }

        private static int SectionNumber(string section)
        {
            int n = 0, i = 0;
            while (i < section.Length && i < 6 && section[i] >= '0' && section[i] <= '9')
                n = n * 10 + (section[i++] - '0');
            return i == 0 ? int.MaxValue : n;
        }

        private static string Format(Row r)
        {
            object v = r.Entry.BoxedValue;
            switch (r.Kind)
            {
                case Kind.Bool: return v is bool b && b ? "ON" : "OFF";
                case Kind.Key: return string.IsNullOrWhiteSpace(v as string) ? "(none)" : (string)v;
                case Kind.Number:
                    return r.IsInt
                        ? Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                        : Convert.ToSingle(v, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
                default: return v?.ToString() ?? string.Empty;
            }
        }

        // ================================================================= open / close

        public void Toggle()
        {
            if (Visible) { Close(); return; }
            Visible = true;
            _primeInput = true;
            _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        }

        public void Close()
        {
            Visible = false;
            CapturingKey = false;
        }

        // ================================================================= input

        /// <summary>Once per frame. Returns true while open, so Plugin can keep the panel's keys
        /// from also firing the toggle hotkey.</summary>
        public bool Update(Hotkeys keys)
        {
            if (!Visible) return false;

            // A panel that cannot draw must give the keyboard back, or the hotkeys would all
            // stop working with nothing on screen to explain why.
            if (_stylesFailed) { Close(); return false; }

            if (_primeInput)
            {
                // WasPressed only updates its memory for keys it is asked about, and these are
                // asked about only while open - so a key held at the last close is remembered as
                // down and its next press gives no edge. One discarded read resynchronises.
                _primeInput = false;
                foreach (int vk in NavKeys) keys.WasPressed(vk);
                foreach (int vk in Hotkeys.Bindable) keys.WasPressed(vk);
                _repeatVk = 0;
                return true;
            }

            if (_rows.Count == 0)
            {
                if (keys.WasPressed(VkEsc)) Close();
                return true;
            }
            Row r = _rows[_selected];

            if (CapturingKey)
            {
                if (keys.WasPressed(VkEsc)) { CapturingKey = false; return true; }
                // Backspace unbinds - "empty" is a legitimate value for the other keys, and
                // capture alone could never produce it. Not in Bindable, so it cannot bind itself.
                // Refused on MenuKey: one stray Backspace there would lock the panel out until
                // the config file is edited by hand.
                if (keys.WasPressed(VkBack))
                {
                    if (r.Key != "MenuKey") Commit(r, string.Empty);
                    CapturingKey = false;
                    return true;
                }
                foreach (int vk in Hotkeys.Bindable)
                {
                    if (!keys.WasPressed(vk)) continue;
                    string name = Hotkeys.NameOf(vk);
                    if (name != null) Commit(r, name);
                    CapturingKey = false;
                    break;
                }
                return true;
            }

            if (keys.WasPressed(VkEsc)) { Close(); return true; }

            if (Repeat(keys, VkUp)) _selected = Mathf.Max(0, _selected - 1);
            if (Repeat(keys, VkDown)) _selected = Mathf.Min(_rows.Count - 1, _selected + 1);
            if (keys.WasPressed(VkHome)) _selected = 0;
            if (keys.WasPressed(VkEnd)) _selected = _rows.Count - 1;
            r = _rows[_selected];

            if (Repeat(keys, VkLeft)) Adjust(r, -1);
            if (Repeat(keys, VkRight)) Adjust(r, 1);
            if (keys.WasPressed(VkEnter))
            {
                if (r.Kind == Kind.Key)
                {
                    // Re-prime: nothing polled the bindable keys while the panel sat open, and
                    // the game still gets input behind it - so a movement key held now would
                    // read as a fresh press next frame and bind itself before anything was
                    // pressed. After this, a key held now binds only once released and pressed.
                    foreach (int vk in Hotkeys.Bindable) keys.WasPressed(vk);
                    CapturingKey = true;
                }
                else if (r.Kind == Kind.Bool) Commit(r, !(r.Entry.BoxedValue is bool b && b));
                else Adjust(r, 1);
            }
            if (keys.WasPressed(VkDelete) && r.Entry.DefaultValue != null) Commit(r, r.Entry.DefaultValue);
            return true;
        }

        /// <summary>Edge on first press, then auto-repeat while held. One key repeats at a time.</summary>
        private bool Repeat(Hotkeys keys, int vk)
        {
            if (keys.WasPressed(vk))
            {
                _repeatVk = vk;
                _nextRepeat = Time.unscaledTime + 0.35f;
                return true;
            }
            if (_repeatVk != vk) return false;
            if (!keys.IsDown(vk)) { _repeatVk = 0; return false; }
            if (Time.unscaledTime < _nextRepeat) return false;
            _nextRepeat = Time.unscaledTime + 0.06f;
            return true;
        }

        /// <summary>Left / Right / Enter on a row. Key rows are not stepped - Enter captures.</summary>
        private void Adjust(Row r, int dir)
        {
            switch (r.Kind)
            {
                case Kind.Bool:
                    // Left = OFF, Right = ON, not a flip: under auto-repeat a flip strobes the
                    // setting (and saves the file) every repeat while the key is held.
                    bool want = dir > 0;
                    if (!(r.Entry.BoxedValue is bool b && b == want)) Commit(r, want);
                    break;
                case Kind.Choice:
                {
                    int i = Array.IndexOf(r.Choices, r.Entry.BoxedValue as string);
                    int n = r.Choices.Length;
                    // An unknown value (hand-edited) steps from the start of the list.
                    i = i < 0 ? 0 : ((i + dir) % n + n) % n;
                    Commit(r, r.Choices[i]);
                    break;
                }
                case Kind.Number:
                {
                    float cur = Convert.ToSingle(r.Entry.BoxedValue, CultureInfo.InvariantCulture);
                    // The next grid point in that direction, so a hand-edited 503 steps to 550 /
                    // 500 rather than 553 / 453. The epsilon keeps an on-grid value from
                    // "rounding" onto itself through float error.
                    float g = cur / r.Step;
                    float next = (dir > 0 ? Mathf.Floor(g + 1e-4f) + 1f : Mathf.Ceil(g - 1e-4f) - 1f) * r.Step;
                    next = Mathf.Clamp(next, r.Min, r.Max);
                    if (Mathf.Approximately(next, cur)) break; // at the end of the range: no save
                    Commit(r, r.IsInt ? (object)Mathf.RoundToInt(next) : next);
                    break;
                }
            }
        }

        /// <summary>Write through. BepInEx persists on set, so there is no save button. Nothing
        /// needs notifying: every value is read at its use site, and the key names are re-parsed
        /// by PanelDriver.Update the frame their string changes.</summary>
        private void Commit(Row r, object value)
        {
            try
            {
                Type want = r.Entry.SettingType;
                if (value != null && value.GetType() != want)
                    value = Convert.ChangeType(value, want, CultureInfo.InvariantCulture);
                r.Entry.BoxedValue = value;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Could not set {r.Key}: {e.Message}");
            }
        }

        // ================================================================= draw

        /// <summary>Repaint pass only. Every control is a texture and a label - see rule 1.</summary>
        public void Draw()
        {
            if (!Visible || !EnsureStyles()) return;
            // Repaint pass only - filtered by the caller (PanelDriver.OnGUI).

            // 1080p is the reference; larger screens scale up so the panel stays legible.
            float s = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);
            float rowH = 24f * s, headH = 30f * s, pad = 14f * s;
            float titleH = 30f * s, footH = 64f * s;
            float w = Mathf.Min(560f * s, Screen.width - 40f);
            float h = Mathf.Min(titleH + _rows.Count * rowH + _sectionCount * headH + footH + 12f * s,
                                Screen.height - 40f);
            var panel = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            _label.fontSize = _value.fontSize = Mathf.RoundToInt(14f * s);
            _header.fontSize = Mathf.RoundToInt(15f * s);
            _hint.fontSize = Mathf.RoundToInt(12f * s);

            var prev = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(panel, _texPanel);

            GUI.color = new Color(1f, 0.85f, 0.45f);
            GUI.Label(new Rect(panel.x + pad, panel.y + 4f * s, w - pad * 2f, titleH - 6f * s),
                      $"{Plugin.NAME} {Plugin.VERSION}  -  settings", _header);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(panel.x, panel.y + titleH, w, 1f), _texLine);

            float x = panel.x + pad, iw = w - pad * 2f;
            float labelW = iw * 0.48f, valueX = x + labelW + 8f * s, valueW = iw - labelW - 8f * s;
            float y = panel.y + titleH + 2f * s;
            float footY = panel.yMax - footH;
            string sec = null;

            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (!string.Equals(r.Section, sec, StringComparison.Ordinal))
                {
                    sec = r.Section;
                    if (y + headH <= footY)
                    {
                        GUI.color = new Color(0.55f, 0.78f, 1f);
                        GUI.Label(new Rect(x, y + 6f * s, iw, headH - 6f * s), sec, _header);
                        GUI.color = Color.white;
                    }
                    y += headH;
                }
                var rr = new Rect(x, y, iw, rowH);
                y += rowH;
                if (rr.yMax > footY) continue; // a very short screen: clip rather than overlap

                bool sel = i == _selected;
                // GUI.color tints DrawTexture too, and the previous row left it set to its value's
                // text colour - near-black after a switch that is ON. Without this reset the row
                // below an ON switch drew its background, and its selection highlight, tinted
                // almost black: MenuKey (below Enabled) and Background (below ShowTraffic).
                GUI.color = Color.white;
                GUI.DrawTexture(rr, sel ? _texSel : (i % 2 == 0 ? _texRow : _texPanel));
                if (sel) GUI.DrawTexture(new Rect(rr.x, rr.y, 3f * s, rr.height), _texAccent);

                GUI.color = sel ? Color.white : new Color(0.84f, 0.84f, 0.88f);
                GUI.Label(new Rect(rr.x + 10f * s, rr.y, labelW - 12f * s, rr.height), r.Label, _label);

                var vr = new Rect(valueX, rr.y + 2f * s, valueW, rr.height - 4f * s);
                bool capturing = sel && CapturingKey;
                bool on = r.Kind == Kind.Bool && r.Entry.BoxedValue is bool b && b;
                GUI.color = Color.white;
                GUI.DrawTexture(r.Kind == Kind.Bool ? new Rect(vr.x, vr.y, Mathf.Min(64f * s, vr.width), vr.height) : vr,
                                capturing || on ? _texAccent : _texRow);
                string shown = capturing ? "press a key" : Format(r);
                if (sel && !capturing && (r.Kind == Kind.Choice || r.Kind == Kind.Number))
                    shown = "<  " + shown + "  >";
                GUI.color = capturing || on ? new Color(0.06f, 0.10f, 0.05f) : new Color(0.86f, 0.86f, 0.90f);
                GUI.Label(r.Kind == Kind.Bool ? new Rect(vr.x, vr.y, Mathf.Min(64f * s, vr.width), vr.height) : vr,
                          shown, _value);
            }

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(panel.x, footY - 4f * s, w, 1f), _texLine);
            if (_rows.Count > 0)
            {
                string tip = _rows[_selected].Tip.Replace('\n', ' ');
                if (tip.Length > 170) tip = tip.Substring(0, 167) + "...";
                GUI.color = new Color(0.74f, 0.74f, 0.80f);
                GUI.Label(new Rect(x, footY, iw, footH - 20f * s), tip, _hint);
            }
            string keysHint = CapturingKey
                ? (_rows[_selected].Key == "MenuKey" ? "Press the key to bind it   Esc cancels"
                                                     : "Press the key to bind it   Backspace unbinds   Esc cancels")
                : "Up/Down select   Left/Right change   Enter edit   Del default   Esc close";
            GUI.color = new Color(0.52f, 0.57f, 0.66f);
            GUI.Label(new Rect(x, panel.yMax - 19f * s, iw, 16f * s), keysHint, _hint);
            GUI.color = prev;
        }

        /// <summary>Build styles and textures once. Never reads GUI.skin. If construction throws,
        /// the panel declines to draw and says so once - and Update() then gives the keyboard back.</summary>
        private bool EnsureStyles()
        {
            if (_stylesFailed) return false;
            if (_label != null) return true;
            try
            {
                _texPanel = Solid(new Color(0.08f, 0.08f, 0.10f, 0.94f));
                _texRow = Solid(new Color(0.14f, 0.14f, 0.17f, 0.94f));
                _texSel = Solid(new Color(0.20f, 0.24f, 0.32f, 0.97f));
                _texAccent = Solid(new Color(0.98f, 0.78f, 0.32f, 1f));
                _texLine = Solid(new Color(0.35f, 0.35f, 0.42f, 1f));
                _label = NewStyle(TextAnchor.MiddleLeft, false, FontStyle.Normal);
                _value = NewStyle(TextAnchor.MiddleCenter, false, FontStyle.Normal);
                _header = NewStyle(TextAnchor.MiddleLeft, false, FontStyle.Bold);
                _hint = NewStyle(TextAnchor.UpperLeft, true, FontStyle.Normal);
                return true;
            }
            catch (Exception e)
            {
                _stylesFailed = true;
                _log.LogError($"Settings panel could not build its styles, so it will not draw: {e.Message}");
                return false;
            }
        }

        private static GUIStyle NewStyle(TextAnchor anchor, bool wrap, FontStyle font)
        {
            var st = new GUIStyle { alignment = anchor, wordWrap = wrap, clipping = TextClipping.Clip, fontStyle = font };
            st.normal.textColor = Color.white; // once; GUI.color tints it per draw
            return st;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>The textures are HideAndDontSave, so a hot reload would leak them.</summary>
        public void Dispose()
        {
            Close();
            foreach (var t in new[] { _texPanel, _texRow, _texSel, _texAccent, _texLine })
                if (t != null) UnityEngine.Object.Destroy(t);
            _texPanel = _texRow = _texSel = _texAccent = _texLine = null;
            _label = null;
        }
    }
}
