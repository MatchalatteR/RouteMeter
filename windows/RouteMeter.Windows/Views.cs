using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using RouteMeter.Core;
using Forms = System.Windows.Forms;

namespace RouteMeter.Windows;

public static class Theme
{
    public static readonly Brush Background = Brush("#131B24"), Card = Brush("#1C2733"), Line = Brush("#30404F"), Text = Brush("#E9F0F4"), Muted = Brush("#91A3B3"), Mint = Brush("#6EE7BC"), Blue = Brush("#86BFFF"), Amber = Brush("#E9BE70");
    public static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public static void Install(Application app)
    {
        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI")));
        window.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        window.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        window.Setters.Add(new Setter(Control.BackgroundProperty, Background));
        app.Resources.Add(typeof(Window), window);
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Card));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 6, 12, 6)));
        button.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); template.VisualTree = border;
        button.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2B3B49"))); button.Triggers.Add(hover);
        app.Resources.Add(typeof(Button), button);
        var check = new Style(typeof(CheckBox)); check.Setters.Add(new Setter(Control.ForegroundProperty, Text)); check.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 0, 0, 0))); app.Resources.Add(typeof(CheckBox), check);
    }
    public static TextBlock Label(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, Action action)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action(); return button;
    }
    public static Border Separator() => new() { Height = 1, Background = Line, Margin = new Thickness(0, 17, 0, 17) };
    public static void Cell(Grid grid, UIElement child, int column, int row = 0) { Grid.SetColumn(child, column); Grid.SetRow(child, row); grid.Children.Add(child); }
}

public sealed class MeterShell : IDisposable
{
    private readonly MeterService meter;
    public TaskbarMeter Taskbar { get; }
    private DetailWindow? details;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    public MeterShell(MeterService service)
    {
        meter = service; Taskbar = new TaskbarMeter(meter, ShowDetails);
        trayIcon = MakeIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "RouteMeter · 正在连接", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏任务栏网速", null, (_, _) => Application.Current.Dispatcher.Invoke(() => Taskbar.SetVisible(!meter.Settings.TaskbarVisible)));
        menu.Items.Add("详细统计与设置", null, (_, _) => Application.Current.Dispatcher.Invoke(ShowDetails));
        menu.Items.Add("重新嵌入任务栏", null, (_, _) => Application.Current.Dispatcher.Invoke(ShowMeter));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出面板（转发器继续运行）", null, (_, _) => Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Application.Current.Dispatcher.Invoke(ShowDetails); };
        tray.DoubleClick += (_, _) => Application.Current.Dispatcher.Invoke(ShowDetails);
        meter.Changed += Update;
    }
    private void Update()
    {
        var rates = meter.Current.Total.PerSecond(meter.Current.Seconds);
        string text = meter.Live ? "RouteMeter ↓ " + Bytes.Format(rates.Down, true) + " ↑ " + Bytes.Format(rates.Up, true) : "RouteMeter · 连接中断";
        tray.Text = text.Length > 63 ? text[..63] : text;
    }
    public void ShowMeter() { Taskbar.SetVisible(true); }
    public void ShowDetails()
    {
        if (details is null) { details = new DetailWindow(meter, ShowMeter); details.Closed += (_, _) => details = null; }
        details.Show(); details.Activate();
    }
    public void Dispose() { meter.Changed -= Update; Taskbar.Dispose(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static System.Drawing.Icon MakeIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(19, 27, 36)); g.FillEllipse(bg, 0, 0, 31, 31);
            using var mint = new System.Drawing.Pen(System.Drawing.Color.FromArgb(110, 231, 188), 3);
            using var blue = new System.Drawing.Pen(System.Drawing.Color.FromArgb(134, 191, 255), 3);
            g.DrawLines(mint, new System.Drawing.Point[] { new(10, 7), new(10, 23), new(5, 18) }); g.DrawLine(mint, 10, 23, 15, 18);
            g.DrawLines(blue, new System.Drawing.Point[] { new(22, 25), new(22, 9), new(17, 14) }); g.DrawLine(blue, 22, 9, 27, 14);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
}

public sealed class HistoryChart : FrameworkElement
{
    public IReadOnlyList<Sample?> Samples { get; set; } = [];
    public bool Live { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        for (int i = 1; i < 4; i++) dc.DrawLine(new Pen(Theme.Line, .6), new Point(0, h * i / 4), new Point(w, h * i / 4));
        double peak = Math.Max(1024, Samples.Where(x => x.HasValue).Select(x => (double)x!.Value.Total.PerSecond(x.Value.Seconds).Total).DefaultIfEmpty(0).Max());
        void Draw(Func<Sample, Bytes> select, Brush color)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool started = false;
                for (int i = 0; i < Samples.Count; i++)
                {
                    if (Samples[i] is not Sample sample) { started = false; continue; }
                    var p = new Point(w * (120 - Samples.Count + i) / 119, h - 3 - (h - 6) * Math.Min(1, select(sample).PerSecond(sample.Seconds).Total / peak));
                    if (!started) { ctx.BeginFigure(p, false, false); started = true; } else ctx.LineTo(p, true, false);
                }
            }
            dc.PushOpacity(Live ? 1 : .35); dc.DrawGeometry(null, new Pen(color, 1.6), geometry); dc.Pop();
        }
        Draw(x => x.Direct, Theme.Blue); Draw(x => x.Proxy, Theme.Mint);
    }
}

public sealed class DetailWindow : Window
{
    private readonly MeterService meter;
    private readonly Action showTaskbar;
    private readonly TextBlock stateText, countText, startedText, notice;
    private readonly TextBlock[,] rates = new TextBlock[3, 2], totals = new TextBlock[3, 3];
    private readonly HistoryChart chart = new() { Height = 95, Margin = new Thickness(0, 14, 0, 6) };
    public DetailWindow(MeterService service, Action showMeter)
    {
        Style = (Style)Application.Current.Resources[typeof(Window)];
        meter = service; showTaskbar = showMeter;
        Title = "RouteMeter · 详细统计"; Width = 530; Height = Math.Min(820, SystemParameters.WorkArea.Height - 48); MinWidth = 470; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; UseLayoutRounding = true;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var panel = new StackPanel { Margin = new Thickness(25, 22, 25, 22) }; scroll.Content = panel; Content = scroll;
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var name = new StackPanel(); name.Children.Add(new TextBlock { Text = "RouteMeter", FontSize = 24, FontWeight = FontWeights.SemiBold });
        name.Children.Add(Theme.Label("WINDOWS  /  CLASH VERGE", 10, Theme.Muted)); Theme.Cell(header, name, 0);
        var show = Theme.Button("显示任务栏网速", showTaskbar); show.VerticalAlignment = VerticalAlignment.Center; Theme.Cell(header, show, 1); panel.Children.Add(header);
        stateText = Theme.Label("", 12, Theme.Mint); stateText.Margin = new Thickness(0, 20, 0, 3); panel.Children.Add(stateText);
        countText = Theme.Label("", 11, Theme.Muted); panel.Children.Add(countText);
        panel.Children.Add(Theme.Separator());
        panel.Children.Add(Theme.Label("实时速率", 14));
        var rateGrid = MakeTable([65, 1, 1], 4, 34);
        rateGrid.Margin = new Thickness(0, 8, 0, 0);
        Theme.Cell(rateGrid, Theme.Label("↓ 下载", 11, Theme.Muted), 1); Theme.Cell(rateGrid, Theme.Label("↑ 上传", 11, Theme.Muted), 2);
        string[] names = ["直连 ≈", "代理", "合计 ≈"]; Brush[] colors = [Theme.Blue, Theme.Mint, Theme.Text];
        for (int row = 0; row < 3; row++)
        {
            Theme.Cell(rateGrid, Theme.Label(names[row], 12, colors[row]), 0, row + 1);
            for (int col = 0; col < 2; col++) { var text = Theme.Label("—", 20, colors[row]); text.FontFamily = new FontFamily("Cascadia Mono, Consolas"); rates[row, col] = text; Theme.Cell(rateGrid, text, col + 1, row + 1); }
        }
        panel.Children.Add(rateGrid);
        panel.Children.Add(chart);
        panel.Children.Add(Theme.Label("最近 120 次 · 下载 + 上传 · 蓝色直连参考 / 绿色代理实测", 10, Theme.Muted));
        panel.Children.Add(Theme.Separator());
        var usageHeader = new DockPanel();
        var reset = Theme.Button("重置累计", () =>
        {
            if (MessageBox.Show(this, "清零本机累计数据，并从下一次采样重新开始？", "重置累计", MessageBoxButton.OKCancel) == MessageBoxResult.OK) _ = meter.Reset();
        }); DockPanel.SetDock(reset, Dock.Right); usageHeader.Children.Add(reset); usageHeader.Children.Add(Theme.Label("累计用量", 14)); panel.Children.Add(usageHeader);
        startedText = Theme.Label("", 10, Theme.Muted); startedText.Margin = new Thickness(0, 7, 0, 8); panel.Children.Add(startedText);
        var totalGrid = MakeTable([65, 1, 1, 1], 4, 28);
        for (int col = 1; col < 4; col++) Theme.Cell(totalGrid, Theme.Label(new[] { "下载", "上传", "合计" }[col - 1], 11, Theme.Muted), col);
        for (int row = 0; row < 3; row++)
        {
            Theme.Cell(totalGrid, Theme.Label(names[row], 12, colors[row]), 0, row + 1);
            for (int col = 0; col < 3; col++) { var text = Theme.Label("—", 13); text.FontFamily = new FontFamily("Cascadia Mono, Consolas"); totals[row, col] = text; Theme.Cell(totalGrid, text, col + 1, row + 1); }
        }
        panel.Children.Add(totalGrid);
        var note = Theme.Label("代理为本机转发器实测传输量，含节点协议开销，不含 IP/TCP 头及重传。直连为 Mihomo 采样参考，合计也为参考值。", 11, Theme.Muted); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(note);
        notice = Theme.Label("", 11, Theme.Amber); notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(notice);
        panel.Children.Add(Theme.Separator());
        var setup = Theme.Button("接入 Clash Verge · 复制计量脚本", () => new RelaySetupWindow(meter) { Owner = this }.ShowDialog());
        setup.HorizontalAlignment = HorizontalAlignment.Left; setup.Margin = new Thickness(0, 0, 0, 18); panel.Children.Add(setup);
        var switches = new StackPanel { Orientation = Orientation.Horizontal };
        var startup = new CheckBox { Content = "开机启动", IsChecked = Storage.Startup, Margin = new Thickness(0, 0, 24, 0) };
        startup.Click += (_, _) => { try { Storage.Startup = startup.IsChecked == true; } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { startup.IsChecked = Storage.Startup; MessageBox.Show(this, "无法更新开机启动设置。", "RouteMeter"); } }; switches.Children.Add(startup);
        var taskbar = new CheckBox { Content = "任务栏显示", IsChecked = meter.Settings.TaskbarVisible }; taskbar.Click += (_, _) => { if (taskbar.IsChecked == true) showTaskbar(); else { meter.Settings.TaskbarVisible = false; meter.Save(); } }; switches.Children.Add(taskbar); panel.Children.Add(switches);
        var actions = new Grid { Margin = new Thickness(0, 18, 0, 0) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var connection = Theme.Button("直连参考 API 设置", () => new ConnectionWindow(meter) { Owner = this }.ShowDialog()); connection.HorizontalAlignment = HorizontalAlignment.Left; Theme.Cell(actions, connection, 0);
        Theme.Cell(actions, Theme.Button("退出", () => Application.Current.Shutdown()), 1); panel.Children.Add(actions);
        meter.Changed += Refresh; Closed += (_, _) => meter.Changed -= Refresh; Refresh();
    }
    private static Grid MakeTable(double[] widths, int rows, double height)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width > 1 ? new GridLength(width) : new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new() { Height = new GridLength(height) }); return grid;
    }
    private void Refresh()
    {
        stateText.Text = meter.Status; stateText.Foreground = meter.Live ? Theme.Mint : Theme.Amber;
        countText.Text = meter.SourceLabel + " · " + meter.ConnectionCount + " 个转发通道";
        startedText.Text = "自 " + meter.Started.ToLocalTime().ToString("yyyy/MM/dd HH:mm") + " 起 · 退出后保留";
        notice.Text = string.Join("\n", new[] { meter.Notice, meter.Relay?.Notice, meter.TaskbarNotice, meter.CoverageWarning }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var current = meter.Current;
        Bytes[] live = [current.Direct, current.Proxy, current.Total];
        var state = meter.Accumulator.State;
        Bytes[] volume = [state.Direct, meter.ProxyTotal, meter.TotalVolume];
        for (int row = 0; row < 3; row++)
        {
            var rate = live[row].PerSecond(current.Seconds);
            rates[row, 0].Text = (row == 0 ? meter.DirectLive : row == 2 ? meter.Live && meter.DirectLive : meter.Live) ? Bytes.Format(rate.Down, true) : "—";
            rates[row, 1].Text = (row == 0 ? meter.DirectLive : row == 2 ? meter.Live && meter.DirectLive : meter.Live) ? Bytes.Format(rate.Up, true) : "—";
            totals[row, 0].Text = Bytes.Format(volume[row].Down); totals[row, 1].Text = Bytes.Format(volume[row].Up); totals[row, 2].Text = Bytes.Format(volume[row].Total);
        }
        chart.Samples = meter.History; chart.Live = meter.Live; chart.InvalidateVisual();
    }
}

public sealed class ConnectionWindow : Window
{
    public ConnectionWindow(MeterService meter)
    {
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Title = "RouteMeter · 直连参考 API 设置"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(Theme.Label("直连参考 · Clash Verge API", 18));
        var auto = new CheckBox { Content = "自动读取 Clash Verge 本机接口", IsChecked = meter.Settings.Automatic, Margin = new Thickness(0, 18, 0, 14) }; panel.Children.Add(auto);
        panel.Children.Add(Theme.Label("本机 API 地址", 12, Theme.Muted));
        var address = new TextBox { Text = meter.Settings.Endpoint, Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 12), Background = Theme.Card, Foreground = Theme.Text, BorderBrush = Theme.Line }; panel.Children.Add(address);
        panel.Children.Add(Theme.Label("API 密钥（Secret）", 12, Theme.Muted));
        var secret = new PasswordBox { Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 12), Background = Theme.Card, Foreground = Theme.Text, BorderBrush = Theme.Line };
        try { secret.Password = SecretProtection.Unprotect(meter.Settings.ProtectedSecret); } catch (Exception) { secret.Password = ""; }
        panel.Children.Add(secret);
        var hint = Theme.Label("自动模式优先使用 Clash Verge 的本机命名管道，无需开启 HTTP API。手动模式支持 127.0.0.1 / localhost 或 pipe:管道名称。", 11, Theme.Muted); hint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(hint);
        var error = Theme.Label("", 11, Theme.Amber); error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(0, 10, 0, 10); panel.Children.Add(error);
        void Toggle() { address.IsEnabled = secret.IsEnabled = auto.IsChecked != true; }
        auto.Click += (_, _) => Toggle(); Toggle();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Theme.Button("取消", Close); cancel.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(cancel);
        var save = Theme.Button("保存并连接", () =>
        {
            try
            {
                bool automatic = auto.IsChecked == true;
                if (!automatic) ApiSource.Manual(address.Text, secret.Password);
                var protectedSecret = SecretProtection.Protect(secret.Password);
                meter.Settings.Automatic = automatic; meter.Settings.Endpoint = address.Text.Trim(); meter.Settings.ProtectedSecret = protectedSecret;
                meter.Save(); meter.Reconnect(); Close();
            }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception) { error.Text = ex is ArgumentException ? ex.Message : "无法保存密钥，请重试。"; }
        }); actions.Children.Add(save); panel.Children.Add(actions);
    }
}

public sealed class RelaySetupWindow : Window
{
    public RelaySetupWindow(MeterService meter)
    {
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Title = "RouteMeter · 接入 Clash Verge"; Width = 510; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(Theme.Label("接入独立计量转发器", 20));
        void Paragraph(string text, Brush color)
        {
            var label = Theme.Label(text, 12, color); label.TextWrapping = TextWrapping.Wrap; label.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(label);
        }
        Paragraph("1. 先把 RouteMeter 放在固定目录。下面可开启后台转发器开机启动，保证下次登录后代理仍能连通。", Theme.Text);
        var startup = new CheckBox { Content = "后台转发器开机启动", IsChecked = Storage.RelayStartup, Margin = new Thickness(0, 12, 0, 0) };
        startup.Click += (_, _) =>
        {
            try { Storage.RelayStartup = startup.IsChecked == true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { startup.IsChecked = Storage.RelayStartup; MessageBox.Show(this, "无法更新开机启动设置。", "RouteMeter"); }
        }; panel.Children.Add(startup);
        Paragraph("2. 复制本机接入脚本，粘贴到 Clash Verge 的「订阅 → 全局扩展脚本」，保存并重载配置。脚本内含本机凭据，请勿公开分享。", Theme.Text);
        Paragraph("已有扩展脚本时请合并：保留原来的 main，将 routeMeter 函数加入，并在 main 最后调用 return routeMeter(config)。", Theme.Amber);
        Paragraph("3. 正常访问网页，面板将从「等待接入」变成「代理实测」。脚本通过官方 dialer-proxy 配置接入，保留现有分流规则和出口节点。", Theme.Text);
        Paragraph("已有代理链、网卡绑定等特殊配置时，脚本会报错并停止，不会覆盖它们，需要单独合并。", Theme.Muted);
        Paragraph("关闭面板后转发器继续运行。停用或移动程序前，先移除接入脚本并重载 Clash Verge，再停止后台转发器。", Theme.Muted);
        var status = Theme.Label("", 11, Theme.Mint); status.Margin = new Thickness(0, 14, 0, 10); status.TextWrapping = TextWrapping.Wrap; panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = Theme.Button("复制本机接入脚本", () =>
        {
            try { Clipboard.SetText(RelayRuntime.ProfileScript(meter.RelayOptions)); status.Text = "已复制。请在 Clash Verge 中保存并重载配置。"; }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception) { status.Text = "暂时无法复制，请重试。"; }
        }); copy.Margin = new Thickness(0, 0, 10, 0); actions.Children.Add(copy); actions.Children.Add(Theme.Button("关闭", Close)); panel.Children.Add(actions);
    }
}
