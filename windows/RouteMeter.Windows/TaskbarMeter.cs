using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using RouteMeter.Core;
using Forms = System.Windows.Forms;

namespace RouteMeter.Windows;

// The meter is an actual WS_CHILD window of Shell_TrayWnd. It follows Explorer's
// visibility, auto-hide, monitor and z-order instead of floating over the desktop.
// No DLL is injected into Explorer and no taskbar settings are changed.
public sealed class TaskbarMeter : IDisposable
{
    private readonly MeterService meter;
    private readonly Action showDetails;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TaskbarForm? form;
    public TaskbarMeter(MeterService service, Action details)
    {
        meter = service; showDetails = details;
        timer.Tick += (_, _) => Maintain(); timer.Start();
        meter.Changed += Refresh;
    }
    public void SetVisible(bool visible)
    {
        meter.Settings.TaskbarVisible = visible; meter.Save(); Maintain();
    }
    private void Maintain()
    {
        if (!meter.Settings.TaskbarVisible) { form?.Dispose(); form = null; meter.TaskbarNotice = null; return; }
        if (form is null || form.IsDisposed || !form.IsHandleCreated || !Native.IsWindow(form.Handle))
        {
            form?.Dispose(); form = new TaskbarForm(meter, showDetails, () => SetVisible(false));
        }
        meter.TaskbarNotice = form.Attach() ? null : "任务栏窗口暂不可用，正在自动重试。可从托盘打开统计。";
    }
    private void Refresh() { form?.Invalidate(); form?.UpdateTooltip(); }
    public object Diagnostics() => form?.Diagnostics() ?? new { attached = false };
    public void Dispose() { timer.Stop(); meter.Changed -= Refresh; form?.Dispose(); }

    private sealed class TaskbarForm : Forms.Form
    {
        private readonly MeterService meter;
        private readonly Forms.ToolTip tooltip = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };
        private IntPtr taskbar;
        private float scale = 1;
        private bool dark = true;
        private Font? valueFont, labelFont;
        public TaskbarForm(MeterService service, Action details, Action hide)
        {
            meter = service;
            Text = "RouteMeter Taskbar"; Name = "RouteMeterTaskbar";
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false; TopLevel = false; AutoScaleMode = Forms.AutoScaleMode.None;
            BackColor = Color.FromArgb(1, 1, 1); TransparencyKey = BackColor;
            SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("详细统计与设置", null, (_, _) => details());
            menu.Items.Add("隐藏任务栏网速", null, (_, _) => hide());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出面板（转发器继续运行）", null, (_, _) => System.Windows.Application.Current.Shutdown());
            ContextMenuStrip = menu;
            MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) details(); };
            // Force handle creation without showing a top-level window.
            _ = Handle;
        }
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return p;
            }
        }
        public bool Attach()
        {
            var host = Native.FindWindow("Shell_TrayWnd", null);
            if (host == IntPtr.Zero) { Native.ShowWindow(Handle, 0); return false; }
            var notification = Native.FindWindowEx(host, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notification == IntPtr.Zero || !Native.GetClientRect(host, out var client) || !Native.GetWindowRect(notification, out var tray))
            { Native.ShowWindow(Handle, 0); return false; }
            var origin = new Native.Point { X = tray.Left, Y = tray.Top };
            Native.MapWindowPoints(IntPtr.Zero, host, ref origin, 1);
            var dpi = Native.GetDpiForWindow(host); if (dpi == 0) dpi = 96;
            float nextScale = dpi / 96f;
            if (valueFont is null || Math.Abs(scale - nextScale) > .01)
            {
                scale = nextScale; valueFont?.Dispose(); labelFont?.Dispose();
                valueFont = new Font("Consolas", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                labelFont = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            int width = (int)Math.Round(336 * scale), height = Math.Min(client.Bottom, (int)Math.Round(40 * scale));
            // Only horizontal taskbars with room to the left of the notification area.
            if (client.Right < client.Bottom || origin.X < width || height < 20) { Native.ShowWindow(Handle, 0); return false; }
            if (taskbar != host || Native.GetParent(Handle) != host)
            {
                long style = Native.GetWindowLongPtr(Handle, -16).ToInt64();
                Native.SetWindowLongPtr(Handle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L)); // WS_POPUP -> WS_CHILD
                Native.SetParent(Handle, host);
                if (Native.GetParent(Handle) != host) { Native.ShowWindow(Handle, 0); return false; }
                taskbar = host;
            }
            int x = Math.Max(0, origin.X - width - (int)Math.Round(4 * scale));
            int y = Math.Max(0, (client.Bottom - height) / 2);
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW, top sibling
            bool nextDark = true;
            using (var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                if (theme?.GetValue("SystemUsesLightTheme") is int light) nextDark = light == 0;
            if (dark != nextDark) { dark = nextDark; Invalidate(); }
            return Native.IsWindowVisible(Handle) && Native.GetParent(Handle) == host;
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            if (valueFont is null || labelFont is null) return;
            var sample = meter.Current;
            Bytes[] values = [sample.Direct, sample.Proxy, sample.Total];
            string[] labels = ["直连≈", "代理", "合计≈"];
            Color[] colors = dark ? [Color.FromArgb(134, 191, 255), Color.FromArgb(110, 231, 188), Color.WhiteSmoke] : [Color.FromArgb(30, 88, 157), Color.FromArgb(0, 110, 76), Color.FromArgb(25, 30, 35)];
            int column = ClientSize.Width / 3, rowHeight = ClientSize.Height / 2;
            int Px(double n) => (int)Math.Round(n * scale);
            var flags = Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine;
            for (int i = 0; i < 3; i++)
            {
                int x = i * column;
                var rate = values[i].PerSecond(sample.Seconds);
                bool active = i == 0 ? meter.DirectLive : i == 2 ? meter.Live && meter.DirectLive : meter.Live;
                var color = active ? colors[i] : dark ? Color.Silver : Color.DimGray;
                Forms.TextRenderer.DrawText(e.Graphics, labels[i], labelFont, new Rectangle(x + Px(4), 0, Px(29), rowHeight), color, flags);
                for (int row = 0; row < 2; row++)
                {
                    Forms.TextRenderer.DrawText(e.Graphics, row == 0 ? "↓" : "↑", labelFont, new Rectangle(x + Px(34), row * rowHeight, Px(12), rowHeight), color, flags);
                    string value = active ? Compact(row == 0 ? rate.Down : rate.Up) : "—";
                    Forms.TextRenderer.DrawText(e.Graphics, value, valueFont, new Rectangle(x + Px(46), row * rowHeight, column - Px(51), rowHeight), color, flags | Forms.TextFormatFlags.Right);
                }
                if (i < 2) { using var pen = new Pen(dark ? Color.FromArgb(65, 72, 80) : Color.FromArgb(183, 188, 193)); e.Graphics.DrawLine(pen, x + column - 1, Px(7), x + column - 1, ClientSize.Height - Px(7)); }
            }
        }
        private static string Compact(ulong value)
        {
            string[] units = ["B", "K", "M", "G", "T", "P", "E"];
            double n = value; int unit = 0;
            while (n >= 1024 && unit < units.Length - 1) { n /= 1024; unit++; }
            return n.ToString(unit == 0 || n >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + units[unit] + "/s";
        }
        public void UpdateTooltip()
        {
            var sample = meter.Current;
            var total = sample.Total.PerSecond(sample.Seconds);
            tooltip.SetToolTip(this, meter.Status + "\n合计（参考） ↓ " + Bytes.Format(total.Down, true) + "  ↑ " + Bytes.Format(total.Up, true)
                + "\n代理为计量转发器实测，直连和合计为参考值\nK/M/G 使用 1024 进位\n点击打开详细统计 · 右键打开菜单");
        }
        public object Diagnostics()
        {
            Native.GetWindowRect(Handle, out var own); Native.GetWindowRect(taskbar, out var parent);
            return new { attached = Native.GetParent(Handle) == taskbar && taskbar != IntPtr.Zero, child = (Native.GetWindowLongPtr(Handle, -16).ToInt64() & 0x40000000) != 0, visible = Native.IsWindowVisible(Handle), bounds = new { own.Left, own.Top, own.Right, own.Bottom }, taskbar = new { parent.Left, parent.Top, parent.Right, parent.Bottom }, dpi = Native.GetDpiForWindow(Handle) };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { tooltip.Dispose(); valueFont?.Dispose(); labelFont?.Dispose(); ContextMenuStrip?.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Point point, uint count);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
