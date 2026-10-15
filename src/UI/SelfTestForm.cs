using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardWriterHelper
{
    // ---- End-to-end self-test ----
    // Opens its own small window, types into it through the real keyboard hook and
    // SendInput path, and reports what ended up in the text box.

    sealed class SelfTestForm : Form
    {
        static readonly IntPtr TestMarker = (IntPtr)0x4B5754;
        const int MaxFocusWaitTicks = 80, SettleTicks = 12;
        // Stands in the script for the "next suggestion" shortcut ({NEXT}).
        public const char NextKey = '\u0001';

        readonly TextBox box = new TextBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly string script;
        readonly Settings settings = new Settings(null);
        readonly Corrector corrector;
        int next, focusWait, settle;

        public readonly Counters Counters = new Counters();
        public string Screen, Problem;

        // excludeSelf: put this test program on the skip list, to prove that
        // nothing is corrected in a skipped program.
        public SelfTestForm(string script, bool excludeSelf)
        {
            this.script = script.Replace("{BS}", "\b").Replace("{NEXT}", NextKey.ToString()).Replace("{TAB}", "\t");
            Text = "Keyboard Writer Helper - self-test (closes by itself)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 110);
            TopMost = true;
            box.Multiline = true;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Segoe UI", 11f);
            Controls.Add(box);

            string folder = Path.Combine(Path.GetTempPath(), "KeyboardWriterHelper-selftest-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(folder);
            ListFile excluded = ListFile.ExcludedApps(folder);
            if (excludeSelf) excluded.Add(Path.GetFileName(Application.ExecutablePath));
            corrector = new Corrector(new SpellService(Counters, settings.SpellOptions()), ListFile.MyWords(folder),
                ListFile.MyFixes(folder), excluded, Counters, settings, TestMarker);

            timer.Interval = 30;
            timer.Tick += delegate { Step(); };
            Shown += delegate { Activate(); box.Focus(); timer.Start(); };
            FormClosed += delegate
            {
                timer.Stop();
                corrector.Dispose();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            };
        }

        void Step()
        {
            // Never type unless our own window has the keyboard.
            if (Native.GetForegroundWindow() != Handle || !box.Focused)
            {
                if (next > 0) { Finish("the test window lost keyboard focus part-way through"); return; }
                if (++focusWait > MaxFocusWaitTicks) { Finish("the test window could not get keyboard focus"); return; }
                Native.SetForegroundWindow(Handle);
                Activate();
                box.Focus();
                return;
            }

            if (next < script.Length)
            {
                SendChar(script[next++]);
                return;
            }
            if (++settle > SettleTicks) Finish(null);
        }

        void SendChar(char c)
        {
            List<Native.INPUT> inputs = new List<Native.INPUT>();
            if (c == '\b')
            {
                Native.AddKey(inputs, Native.VK_BACK, 0, 0, TestMarker);
            }
            else if (c == NextKey)
            {
                // Modifiers down, the key, modifiers up.
                Hotkey hotkey = settings.NextHotkey;
                Native.AddKey(inputs, (ushort)hotkey.Key, 0, 0, TestMarker);
                foreach (int modifier in new int[] { hotkey.Shift ? Native.VK_SHIFT : 0, hotkey.Alt ? Native.VK_MENU : 0, hotkey.Ctrl ? Native.VK_CONTROL : 0 })
                {
                    if (modifier == 0) continue;
                    List<Native.INPUT> pair = new List<Native.INPUT>();
                    Native.AddKey(pair, (ushort)modifier, 0, 0, TestMarker);
                    inputs.Insert(0, pair[0]);
                    inputs.Add(pair[1]);
                }
            }
            else
            {
                short scan = Native.VkKeyScanW(c);
                bool shift = (scan & 0x100) != 0;
                Native.AddKey(inputs, (ushort)(scan & 0xFF), 0, 0, TestMarker);
                if (shift)
                {
                    // Wrap the key in Shift down ... Shift up.
                    List<Native.INPUT> shiftKey = new List<Native.INPUT>();
                    Native.AddKey(shiftKey, Native.VK_SHIFT, 0, 0, TestMarker);
                    inputs.Insert(0, shiftKey[0]);
                    inputs.Add(shiftKey[1]);
                }
            }
            Native.Send(inputs);
        }

        void Finish(string problem)
        {
            timer.Stop();
            Problem = problem;
            Screen = box.Text;
            Close();
        }
    }
}
