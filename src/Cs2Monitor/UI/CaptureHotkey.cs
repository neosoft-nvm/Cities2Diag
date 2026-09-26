using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cs2Monitor.UI;

/// <summary>
/// Global capture hotkey via a low-level keyboard hook (WH_KEYBOARD_LL).
/// Unlike RegisterHotKey it cannot be "taken" by another application, and it sees the key before the game
/// does. The key is passed on unchanged. The hook callback runs on the UI thread (it needs a message loop)
/// and must return quickly, or Windows silently removes the hook.
/// </summary>
internal sealed class CaptureHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc; // keep the delegate alive while the hook exists
    private readonly IntPtr _hook;
    private readonly Keys _key;
    private readonly bool _ctrl, _alt, _shift, _win;
    private readonly Action _onPressed;
    private bool _down;

    public string Description { get; }
    public string? Error { get; }
    public bool Active => _hook != IntPtr.Zero;

    public CaptureHotkey(string spec, Action onPressed)
    {
        _onPressed = onPressed;
        _proc = Callback;
        Description = spec;
        if (!TryParse(spec, out _key, out _ctrl, out _alt, out _shift, out _win, out var parseError))
        {
            Error = parseError;
            return;
        }
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Error = $"keyboard hook failed (error {Marshal.GetLastWin32Error()})";
    }

    /// <summary>"Ctrl+Alt+M", "Shift+F9", "Pause", … (key names as in System.Windows.Forms.Keys).</summary>
    public static bool TryParse(string spec, out Keys key, out bool ctrl, out bool alt, out bool shift, out bool win, out string? error)
    {
        key = Keys.None;
        ctrl = alt = shift = win = false;
        error = null;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win": win = true; continue;
            }
            if (key != Keys.None || !Enum.TryParse(raw, true, out key))
            {
                error = $"cannot parse hotkey '{spec}'";
                return false;
            }
        }
        if (key == Keys.None) error = $"hotkey '{spec}' has no key";
        return error == null;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var vk = (Keys)Marshal.ReadInt32(lParam);
            if (vk == _key)
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    bool mods = Down(0x11) == _ctrl && Down(0x12) == _alt && Down(0x10) == _shift && (Down(0x5B) || Down(0x5C)) == _win;
                    if (mods && !_down)
                    {
                        _down = true; // ignore auto-repeat while held
                        _onPressed();
                    }
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP) _down = false;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}

/// <summary>Audible + visible confirmation of a capture that works while the game has focus.</summary>
internal static class CaptureFeedback
{
    private static readonly Lazy<byte[]> Tone = new(() => MakeTone());

    /// <summary>Two short rising beeps generated in memory (does not depend on the Windows sound scheme).</summary>
    public static void Beep()
    {
        try
        {
            var player = new System.Media.SoundPlayer(new MemoryStream(Tone.Value));
            player.Play(); // asynchronous
        }
        catch { /* no audio device */ }
    }

    private static byte[] MakeTone()
    {
        const int rate = 22050;
        var samples = new List<short>();
        void Note(double hz, int ms)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / (rate * 0.005)); // 5 ms fade in/out, no clicks
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * 9000));
            }
        }
        Note(880, 90);
        Note(0, 40);
        Note(1320, 110);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}

/// <summary>
/// Small click-through, non-activating "Captured" notice in the top-right corner. It never takes focus from the
/// game. (It cannot appear over a game in exclusive fullscreen; the beep covers that case.)
/// </summary>
internal sealed class CaptureToast : Form
{
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    private readonly System.Windows.Forms.Timer _hide = new() { Interval = 2200 };
    private readonly Label _text = new();

    public CaptureToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 34, 40);
        Opacity = 0.92;
        Padding = new Padding(14, 8, 14, 8);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _text.AutoSize = true;
        _text.ForeColor = Color.FromArgb(235, 200, 70);
        _text.Font = new Font("Segoe UI Semibold", 14f);
        Controls.Add(_text);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            return cp;
        }
    }

    public void ShowMessage(string message)
    {
        _text.Text = message;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var size = GetPreferredSize(Size.Empty);
        Location = new Point(area.Right - size.Width - 24, area.Top + 24);
        if (!Visible) Show();
        _hide.Stop();
        _hide.Start();
    }
}
