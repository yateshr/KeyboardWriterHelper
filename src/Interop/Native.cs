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
    // ---- Win32 ----

    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
        public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
        public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
                         VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
                         VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
                         VK_NOTHING = 0xE8; // a code no keyboard key has
        public const int LANG_ENGLISH = 0x09;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        public const uint WM_APP = 0x8000;
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern short VkKeyScanW(char ch);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // File name of the program that owns a window ("notepad.exe"), or "" if unknown.
        public static string ProcessNameOf(IntPtr window)
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return "";
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return "";
            try
            {
                StringBuilder path = new StringBuilder(1024);
                int size = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) ? Path.GetFileName(path.ToString()) : "";
            }
            finally { CloseHandle(process); }
        }

        // The id of the process that owns a window, or 0.
        public static uint ProcessIdOf(IntPtr window)
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            return pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        public static readonly int GuiThreadInfoSize = Marshal.SizeOf(typeof(GUITHREADINFO));

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        const int ES_PASSWORD = 0x20;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        // An edit control that hides what is typed in it (ES_PASSWORD).
        public static bool IsPasswordEdit(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            StringBuilder name = new StringBuilder(64);
            if (GetClassName(window, name, name.Capacity) == 0) return false;
            return name.ToString().IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (GetWindowLong(window, GWL_STYLE) & ES_PASSWORD) != 0;
        }

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        public const int GWL_STYLE = -16;
        public const uint WS_CAPTION = 0x00C00000;
        public const uint WS_MAXIMIZE = 0x01000000;

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")]
        public static extern bool GlobalUnlock(IntPtr hMem);

        public static string GetClipboardText()
        {
            string result = "";
            Thread t = new Thread(delegate()
            {
                result = ReadClipboardRaw();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(40)) return "";
            return result;
        }

        static string ReadClipboardRaw()
        {
            if (!OpenClipboard(IntPtr.Zero)) return "";
            try
            {
                IntPtr hData = GetClipboardData(13); // CF_UNICODETEXT
                if (hData == IntPtr.Zero) return "";
                IntPtr ptr = GlobalLock(hData);
                if (ptr == IntPtr.Zero) return "";
                try
                {
                    string text = Marshal.PtrToStringUni(ptr);
                    if (string.IsNullOrEmpty(text)) return "";
                    if (text.Length > 500) text = text.Substring(0, 500);
                    return text;
                }
                finally { GlobalUnlock(hData); }
            }
            catch { return ""; }
            finally { CloseClipboard(); }
        }

        const uint MAPVK_VSC_TO_VK = 1;

        [DllImport("user32.dll")]
        static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

        // The scan codes of the three letter rows, from the keys of Q, A and Z
        // to the end of each row.
        static readonly int[][] LetterRowScans = { new int[] { 0x10, 0x1B }, new int[] { 0x1E, 0x28 }, new int[] { 0x2C, 0x35 } };

        // The letters a keyboard layout puts on the three letter rows, as the keys
        // physically lie; a space for a key without a plain letter. US QWERTY gives
        // "qwertyuiop  ", "asdfghjkl  ", "zxcvbnm   ".
        public static string[] LetterRows(IntPtr layout)
        {
            string[] rows = new string[LetterRowScans.Length];
            byte[] noKeysDown = new byte[256];
            StringBuilder text = new StringBuilder(8);
            for (int r = 0; r < rows.Length; r++)
            {
                StringBuilder row = new StringBuilder();
                for (int scan = LetterRowScans[r][0]; scan <= LetterRowScans[r][1]; scan++)
                {
                    uint vk = MapVirtualKeyEx((uint)scan, MAPVK_VSC_TO_VK, layout);
                    text.Length = 0;
                    // Flag 4: without disturbing the keyboard's dead-key state.
                    int n = vk == 0 ? 0 : ToUnicodeEx(vk, (uint)scan, noKeysDown, text, text.Capacity, 4, layout);
                    char c = n == 1 ? char.ToLowerInvariant(text[0]) : ' ';
                    row.Append(c >= 'a' && c <= 'z' ? c : ' ');
                }
                rows[r] = row.ToString();
            }
            return rows;
        }

        static readonly HashSet<string> WritingPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE", "ONENOTE.EXE", "notepad.exe",
            "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
            "Code.exe", "slack.exe", "Teams.exe", "ms-teams.exe", "Obsidian.exe", "Notion.exe", "soffice.bin", "WordPad.exe"
        };

        public static bool IsForegroundFullScreen(IntPtr fg)
        {
            if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
            RECT rect;
            if (!GetWindowRect(fg, out rect)) return false;
            Screen screen = Screen.FromHandle(fg);
            if (screen == null) return false;
            if (rect.Left <= screen.Bounds.Left && rect.Top <= screen.Bounds.Top &&
                rect.Right >= screen.Bounds.Right && rect.Bottom >= screen.Bounds.Bottom)
            {
                int style = GetWindowLong(fg, GWL_STYLE);
                if ((style & (int)WS_MAXIMIZE) != 0) return false;
                if ((style & (int)WS_CAPTION) != (int)WS_CAPTION)
                {
                    string name = ProcessNameOf(fg);
                    if (string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase)) return false;
                    // Word's Focus mode and a browser in F11 have no title bar either,
                    // but they are places to write, not games.
                    if (name != null && WritingPrograms.Contains(name)) return false;
                    return true;
                }
            }
            return false;
        }

        public static void AddKey(List<INPUT> inputs, ushort vk, ushort scan, uint flags, IntPtr extraInfo)
        {
            INPUT down = new INPUT();
            down.type = INPUT_KEYBOARD;
            down.U.ki.wVk = vk;
            down.U.ki.wScan = scan;
            down.U.ki.dwFlags = flags;
            down.U.ki.dwExtraInfo = extraInfo;
            INPUT up = down;
            up.U.ki.dwFlags = flags | KEYEVENTF_KEYUP;
            inputs.Add(down);
            inputs.Add(up);
        }

        public static bool Send(List<INPUT> inputs)
        {
            if (inputs == null || inputs.Count == 0) return true;
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            return sent == (uint)inputs.Count;
        }
    }
}
