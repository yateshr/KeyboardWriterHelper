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
    // ---- The keyboard thread: hooks, key translation, typing the fix ----

    sealed class Corrector : IDisposable
    {
        // Marks our own injected keystrokes so the hook can tell them apart.
        static readonly IntPtr Magic = (IntPtr)0x4B5748;
        // Longest the keyboard thread waits for a spelling answer. Windows drops a
        // hook that takes too long (LowLevelHooksTimeout, 300 ms by default).
        // Safe budget well below Windows LowLevelHooksTimeout (which can be as low as 100ms under policy)
        const int LookupBudgetMs = 40;

        // Backspace right after a fix always undoes it; the word is only added to
        // "my words" for good when the same fix is undone this many times, so a
        // stray Backspace cannot switch a correction off.
        const int UndosBeforeLearning = 2;

        readonly SpellService spelling;
        readonly ListFile myWords, myFixes, excludedApps;
        readonly Dictionary<string, int> undoCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Counters counters;
        readonly Settings settings;
        // Windows SendInput could not type into (elevated programs), with their process id.
        readonly Dictionary<IntPtr, uint> uipiBlocked = new Dictionary<IntPtr, uint>();
        // Keys injected with this marker count as typed by the user (self-test only).
        readonly IntPtr testMarker;
        readonly Engine engine;
        readonly Thread thread, mouseThread;
        readonly ManualResetEvent started = new ManualResetEvent(false), mouseStarted = new ManualResetEvent(false);
        volatile uint mouseThreadId;
        // Set by the mouse thread on a click, taken by the keyboard thread.
        volatile bool clicked;
        // The fullscreen check of the window in front, and when it was made (null: not yet).
        bool fullScreen;
        int? fullScreenCheckedAt;
        // The control that had the keyboard at the last key, and whether it is a password box.
        IntPtr lastFocus;
        bool lastFocusIsPassword;
        // The keyboard layout at the last key, and its letters (for key proximity).
        IntPtr lastLayout;
        string[] layoutRows;
        // Held in fields so the delegates are not garbage collected while hooked.
        readonly Native.HookProc keyProc, mouseProc;
        readonly byte[] keyState = new byte[256];
        readonly StringBuilder keyChars = new StringBuilder(8);
        IntPtr keyHook, mouseHook, lastForeground;
        uint threadId;
        Exception startupError;
        volatile bool enabled = true;
        volatile string lastApp = "";
        bool wasEnabled = true;
        // The key currently held down, to recognise auto-repeat, and whether it
        // was taken as one of our shortcuts.
        int heldKey;
        bool heldIsShortcut;
        // Presses of the next-suggestion shortcut not acted on yet. The swap is
        // made when the last of Ctrl/Alt/Shift is let go: typed while they are
        // still down, the Backspaces would arrive as Ctrl+Alt+Backspace.
        int pendingNext;
        // That last key. Windows may still count it as down while its key-up is
        // being handled, so the swap starts by letting go of it once more.
        int liftKey;

        // All raised on the keyboard thread. Corrected and Undone: (typed, fix).
        // Changed: (typed, fix before, fix now) - another candidate was picked.
        public event Action<string, string> Corrected, Undone;
        public event Action<string, string, string> Changed;
        public event Action<string, string[]> CandidateOffered;
        public event Action CandidateDismissed;
        public event Action<string, string> PredictionOffered;
        public event Action PredictionDismissed;
        public event Action EnabledChanged;
        // A word was added to "my words" because its fix was undone repeatedly.
        public event Action<string> Learned;

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        // The program the user last typed in ("notepad.exe").
        public string LastApp { get { return lastApp; } }

        public Corrector(SpellService spelling, ListFile myWords, ListFile myFixes, ListFile excludedApps,
            Counters counters, Settings settings, IntPtr testMarker)
        {
            this.settings = settings;
            this.spelling = spelling;
            this.myWords = myWords;
            this.myFixes = myFixes;
            this.excludedApps = excludedApps;
            this.counters = counters;
            this.testMarker = testMarker;
            engine = new Engine(Lookup, myWords.Contains, myFixes.Get, Type, (Func<string, string, string, string>)spelling.PredictNext);
            engine.Clipboard = Native.GetClipboardText;
            engine.Corrected += OnCorrected;
            engine.Undone += OnUndone;
            engine.Changed += delegate(string typed, string before, string now) { if (Changed != null) Changed(typed, before, now); };
            engine.CandidateOffered += delegate(string fix, string[] choices) { if (CandidateOffered != null) CandidateOffered(fix, choices); };
            engine.CandidateDismissed += delegate { if (CandidateDismissed != null) CandidateDismissed(); };
            engine.PredictionOffered += delegate(string pred, string pfx) { if (PredictionOffered != null) PredictionOffered(pred, pfx); };
            engine.PredictionDismissed += delegate { if (PredictionDismissed != null) PredictionDismissed(); };
            keyProc = KeyProc;
            mouseProc = MouseProc;
            Rehearse();

            thread = new Thread(Run);
            thread.Name = "Keyboard";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            started.WaitOne();
            if (startupError != null)
                throw new InvalidOperationException("Could not install the keyboard hook: " + startupError.Message);

            // The mouse hook has a thread of its own: on the keyboard thread the
            // pointer would freeze whenever a key waits for a spelling answer.
            mouseThread = new Thread(RunMouse);
            mouseThread.Name = "Mouse";
            mouseThread.IsBackground = true;
            mouseThread.Start();
            mouseStarted.WaitOne();
        }

        public void Dispose()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (mouseThreadId != 0) Native.PostThreadMessage(mouseThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            if (mouseThread != null) mouseThread.Join(2000);
        }

        // Runs the whole correction path once before the hooks exist, so the first
        // real correction does not pay for JIT compilation inside the hook callback.
        void Rehearse()
        {
            Engine rehearsal = new Engine(Lookup, myWords.Contains, myFixes.Get, delegate { }, (Func<string, string, string, string>)spelling.PredictNext);
            foreach (char c in "Teh wrold ") rehearsal.Char(c);
            rehearsal.Next();
            rehearsal.Backspace();
            lastForeground = Native.GetForegroundWindow();
            Native.ProcessNameOf(lastForeground);
            Type(0, "");
            lastForeground = IntPtr.Zero;
            counters.Words = counters.TooSlow = 0;
        }

        void Run()
        {
            try
            {
                IntPtr module = Native.GetModuleHandle(null);
                keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
                if (keyHook == IntPtr.Zero)
                    throw new InvalidOperationException("Windows error " + Marshal.GetLastWin32Error());
                threadId = Native.GetCurrentThreadId();
            }
            catch (Exception ex) { startupError = ex; }
            finally { started.Set(); }
            if (startupError != null) return;

            // Hook callbacks are delivered from inside GetMessage.
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }

            Native.UnhookWindowsHookEx(keyHook);
        }

        void RunMouse()
        {
            try
            {
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
                mouseThreadId = Native.GetCurrentThreadId();
            }
            finally { mouseStarted.Set(); }
            // Without it a click is simply not noticed; correction still works.
            if (mouseHook == IntPtr.Zero) return;

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }

            Native.UnhookWindowsHookEx(mouseHook);
        }

        Suggestion Lookup(string word, string secondPrevious, string previous, bool cautious)
        {
            counters.Words++;
            bool tooSlow;
            Suggestion result = spelling.Suggest(word, secondPrevious, previous, cautious, LookupBudgetMs, out tooSlow);
            if (tooSlow) counters.TooSlow++;
            return result;
        }

        void OnCorrected(string from, string to)
        {
            counters.Corrections++;
            if (Corrected != null) Corrected(from, to);
        }

        void OnUndone(string word, string fix)
        {
            counters.Undone++;
            if (Undone != null) Undone(word, fix);
            int times;
            undoCounts.TryGetValue(word, out times);
            undoCounts[word] = ++times;
            if (times >= UndosBeforeLearning && !myWords.Contains(word))
            {
                myWords.Add(word);
                if (Learned != null) Learned(word);
            }
        }

        // On the mouse thread. The engine belongs to the keyboard thread, so a
        // click is only noted here and acted on at the next key (ClickBreak).
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                long msg = wParam.ToInt64();
                // The caret may now be in the middle of existing text ("walk|" + "ing"),
                // so the next word is left alone, like after an arrow key.
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
                {
                    clicked = true;
                    // The pill hides at once; its handler only queues that on the UI thread.
                    try { if (CandidateDismissed != null) CandidateDismissed(); }
                    catch (Exception ex) { counters.Error(ex); }
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        // A click since the last key: forget the word being typed. True if there was one.
        bool ClickBreak()
        {
            if (!clicked) return false;
            clicked = false;
            engine.Break(false);
            return true;
        }

        IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
                {
                    Native.KBDLLHOOKSTRUCT up = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (IsUserKey(up) && (int)up.vkCode == heldKey) heldKey = 0;
                    // Done from inside this call, like every other fix: what is
                    // typed here goes ahead of anything the user types next.
                    if (IsUserKey(up) && pendingNext > 0 && !ModifierHeld((int)up.vkCode))
                        SwapPending((int)up.vkCode);
                }
                else if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                {
                    Native.KBDLLHOOKSTRUCT key = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    long begin = Stopwatch.GetTimestamp();
                    bool swallow = false;
                    // A failure here must never block the user's typing.
                    try { swallow = HandleKey(key); }
                    catch (Exception ex)
                    {
                        counters.Error(ex);
                        engine.Break(false);
                    }
                    int ms = (int)((Stopwatch.GetTimestamp() - begin) * 1000 / Stopwatch.Frequency);
                    if (ms > counters.SlowestKeyMs) counters.SlowestKeyMs = ms;
                    if (swallow) return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }

        static bool IsDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        // The next-suggestion shortcut was pressed and the last of its keys
        // (released) is going up.
        void SwapPending(int released)
        {
            int times = pendingNext;
            pendingNext = 0;
            // Clicked somewhere since the fix: the caret may have moved away from it.
            if (ClickBreak()) return;
            long begin = Stopwatch.GetTimestamp();
            try
            {
                while (times-- > 0)
                {
                    liftKey = released;
                    engine.Next();
                }
            }
            catch (Exception ex)
            {
                counters.Error(ex);
                engine.Break(false);
            }
            liftKey = 0;
            int ms = (int)((Stopwatch.GetTimestamp() - begin) * 1000 / Stopwatch.Frequency);
            if (ms > counters.SlowestKeyMs) counters.SlowestKeyMs = ms;
        }

        // Is a Shift, Ctrl or Alt key down, not counting the one being let go
        // right now? (Its own state may or may not be updated yet.)
        static bool ModifierHeld(int released)
        {
            for (int vk = Native.VK_LSHIFT; vk <= Native.VK_RMENU; vk++)
            {
                // The left and right key of a pair, and the pair's common code.
                int common = Native.VK_SHIFT + (vk - Native.VK_LSHIFT) / 2;
                if (vk != released && common != released && IsDown(vk)) return true;
            }
            return false;
        }

        // Alt pressed and let go with nothing in between opens the menu bar of
        // the program in front. A shortcut key we swallow counts as nothing, so
        // put a key without meaning in its place.
        void MaskAlt()
        {
            List<Native.INPUT> inputs = new List<Native.INPUT>(2);
            Native.AddKey(inputs, Native.VK_NOTHING, 0, 0, Magic);
            Native.Send(inputs);
        }

        // False for keystrokes injected by a program (ours included).
        bool IsUserKey(Native.KBDLLHOOKSTRUCT key)
        {
            return (key.flags & Native.LLKHF_INJECTED) == 0 ||
                   (testMarker != IntPtr.Zero && key.dwExtraInfo == testMarker);
        }

        bool HandleKey(Native.KBDLLHOOKSTRUCT key)
        {
            ClickBreak();
            if (!IsUserKey(key))
            {
                // Text typed by another program (or us) is not the user's spelling.
                if (key.dwExtraInfo != Magic) engine.Break(false);
                return false;
            }
            counters.Keys++;

            int vk = (int)key.vkCode;
            if (vk == Native.VK_SHIFT || vk == Native.VK_CONTROL || vk == Native.VK_MENU || vk == Native.VK_CAPITAL ||
                vk == Native.VK_LWIN || vk == Native.VK_RWIN || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x90 || vk == 0x91)
                return false;

            // A held key repeating (a game's movement keys, "sooooo", holding
            // Backspace) is not a word being spelled.
            bool repeat = vk == heldKey;
            heldKey = vk;
            // A shortcut held down does nothing more, and does not reach the program either.
            if (repeat && heldIsShortcut) return true;
            heldIsShortcut = false;
            // Any key but the next-suggestion shortcut itself calls the swap off.
            int nextPresses = pendingNext;
            pendingNext = 0;

            bool ctrl = IsDown(Native.VK_CONTROL), alt = IsDown(Native.VK_MENU), shift = IsDown(Native.VK_SHIFT);
            // Never with Right Alt (AltGr): that is how some layouts type letters.
            bool chord = (ctrl || alt) && !IsDown(Native.VK_RMENU) && !IsDown(Native.VK_LWIN) && !IsDown(Native.VK_RWIN);
            if (chord && settings.PauseHotkey.Matches(vk, ctrl, alt, shift))
            {
                heldIsShortcut = true;
                if (!ctrl) MaskAlt();
                enabled = !enabled;
                if (EnabledChanged != null) EnabledChanged();
                return true;
            }

            bool on = enabled;
            if (on != wasEnabled)
            {
                wasEnabled = on;
                engine.Break(false);
            }
            if (!on) return false;
            engine.FixOnPunctuation = settings.FixOnPunctuation;
            engine.CapitalizeSentences = settings.CapitalizeSentences;
            engine.FixTwoCapitals = settings.FixTwoCapitals;
            engine.FixPhrases = settings.FixPhrases;
            engine.DoubleSpacePeriod = settings.DoubleSpacePeriod;
            engine.SmartDashes = settings.SmartDashes;
            engine.SmartArrows = settings.SmartArrows;
            engine.SmartEllipsis = settings.SmartEllipsis;
            engine.SmartCompose = settings.SmartCompose;

            IntPtr foreground = Native.GetForegroundWindow();
            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            if (foreground != lastForeground)
            {
                lastForeground = foreground;
                lastApp = Native.ProcessNameOf(foreground);
                fullScreenCheckedAt = null;
                engine.Break(true);
            }

            if (repeat || excludedApps.Contains(lastApp) || IsBlocked(foreground) || IsFullScreen(foreground) ||
                IsPasswordBox(foregroundThread))
            {
                engine.Break(false);
                return false;
            }

            // Straight after a fix: pick its next-best candidate (once the keys are
            // let go, see pendingNext). At any other moment these keys belong to
            // the program in front and pass through.
            if (chord && settings.NextHotkey.Matches(vk, ctrl, alt, shift) && engine.CanNext)
            {
                heldIsShortcut = true;
                if (!ctrl) MaskAlt();
                pendingNext = nextPresses + 1;
                return true;
            }

            bool isAltGr = IsDown(Native.VK_RMENU);
            if ((ctrl && !isAltGr) || (alt && !isAltGr) || IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN))
            {
                engine.Break(false);
                return false;
            }

            if (vk == Native.VK_BACK) return engine.Backspace();
            // Enter never triggers a correction: it submits forms, and the
            // word before could be a password or a command.
            if (vk == Native.VK_RETURN) { engine.Break(true); return false; }

            // Tab accepts a Smart Compose suggestion, but only one the user can see
            // in the pill; at any other moment Tab belongs to the program.
            if (vk == Native.VK_TAB)
            {
                if (settings.SmartCompose && settings.ShowCandidatePopup && engine.CanAcceptPrediction)
                    return engine.AcceptPrediction();
                engine.Break(true);
                return false;
            }

            if (vk == Native.VK_ESCAPE || (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || (vk >= 0x70 && vk <= 0x87))
            {
                engine.Break(false);
                return false;
            }

            Array.Clear(keyState, 0, keyState.Length);
            if (IsDown(Native.VK_SHIFT)) keyState[Native.VK_SHIFT] = 0x80;
            if ((Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0) keyState[Native.VK_CAPITAL] = 1;
            if (isAltGr)
            {
                keyState[Native.VK_CONTROL] = 0x80;
                keyState[Native.VK_LCONTROL] = 0x80;
                keyState[Native.VK_MENU] = 0x80;
                keyState[Native.VK_RMENU] = 0x80;
            }
            IntPtr layout = Native.GetKeyboardLayout(foregroundThread);
            if (layout != lastLayout)
            {
                lastLayout = layout;
                layoutRows = Native.LetterRows(layout);
            }
            spelling.UpdateLayout(layoutRows);
            // Only English keyboards: other languages must not go through an English dictionary.
            if (((int)(layout.ToInt64() & 0x3FF)) != Native.LANG_ENGLISH)
            {
                engine.Break(false);
                return false;
            }
            keyChars.Length = 0;
            // Flag 4: translate without disturbing the keyboard's dead-key state.
            int n = Native.ToUnicodeEx(key.vkCode, key.scanCode, keyState, keyChars, keyChars.Capacity, 4, layout);
            if (n != 1)
            {
                engine.Break(false);
                return false;
            }
            return engine.Char(keyChars[0]);
        }

        // Fullscreen games: checked when the window in front changes and then at
        // most once a second, not on every key.
        bool IsFullScreen(IntPtr foreground)
        {
            if (!settings.SkipFullscreenGames) return false;
            int now = Environment.TickCount;
            if (fullScreenCheckedAt == null || unchecked(now - fullScreenCheckedAt.Value) > 1000)
            {
                fullScreen = Native.IsForegroundFullScreen(foreground);
                fullScreenCheckedAt = now;
            }
            return fullScreen;
        }

        // A window SendInput could not type into. Its handle may later belong to
        // another window, so the process must still be the same one.
        bool IsBlocked(IntPtr window)
        {
            uint process;
            if (!uipiBlocked.TryGetValue(window, out process)) return false;
            if (Native.ProcessIdOf(window) == process) return true;
            uipiBlocked.Remove(window);
            return false;
        }

        // The control with the keyboard is a password box. Win32 and WinForms
        // edit controls say so; browsers and WPF do not. Looked up again only
        // when the focus moves.
        bool IsPasswordBox(uint foregroundThread)
        {
            Native.GUITHREADINFO gui = new Native.GUITHREADINFO();
            gui.cbSize = Native.GuiThreadInfoSize;
            if (!Native.GetGUIThreadInfo(foregroundThread, ref gui)) return false;
            if (gui.hwndFocus != lastFocus)
            {
                lastFocus = gui.hwndFocus;
                lastFocusIsPassword = Native.IsPasswordEdit(gui.hwndFocus);
            }
            return lastFocusIsPassword;
        }

        // Engine output: erase the misspelled word and type the replacement.
        void Type(int backspaces, string text)
        {
            // Another window came to the front while the word was looked up: the
            // fix belongs to the old one and must not be typed into the new one.
            if (Native.GetForegroundWindow() != lastForeground) return;

            // Real scan codes, for programs that read those instead of the key code.
            ushort backScan = (ushort)Native.MapVirtualKey(Native.VK_BACK, 0);
            ushort spaceScan = (ushort)Native.MapVirtualKey(Native.VK_SPACE, 0);
            List<Native.INPUT> inputs = new List<Native.INPUT>((backspaces + text.Length) * 2 + 2);
            if (liftKey != 0)
            {
                // Only the key-up: pressing Alt or Ctrl on its own means something
                // to some programs.
                List<Native.INPUT> pair = new List<Native.INPUT>(2);
                Native.AddKey(pair, (ushort)liftKey, 0, 0, Magic);
                inputs.Add(pair[1]);
                liftKey = 0;
            }
            for (int i = 0; i < backspaces; i++) Native.AddKey(inputs, Native.VK_BACK, backScan, 0, Magic);
            foreach (char c in text)
            {
                if (c == ' ') Native.AddKey(inputs, Native.VK_SPACE, spaceScan, 0, Magic);
                else Native.AddKey(inputs, 0, c, Native.KEYEVENTF_UNICODE, Magic);
            }
            if (!Native.Send(inputs))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 5 || error == 0)
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero) uipiBlocked[fg] = Native.ProcessIdOf(fg);
                }
                engine.Break(false);
            }
        }
    }
}
