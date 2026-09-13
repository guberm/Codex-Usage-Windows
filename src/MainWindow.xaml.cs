using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexUsage;
public partial class MainWindow : Window
{
    private readonly Storage storage;
    private readonly CodexApi api;
    private readonly bool demo;
    private readonly string? smokeOutput;
    private readonly Forms.NotifyIcon tray = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? login;
    private Settings settings = new();
    private Usage? usage;
    private bool busy, signedIn, expanded, dragging, closing, pendingReset, recovery;
    private Point? dragStart;
    private DateTimeOffset lastAttempt;
    private string? error;
    private string? trayPercent;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public MainWindow(Storage storage, bool demo, string? smokeOutput)
    {
        this.storage = storage; this.demo = demo; this.smokeOutput = smokeOutput;
        api = new(storage);
        InitializeComponent();
        try { settings = storage.Read<Settings>("settings.json") ?? new(); var session = storage.LoadSession(); signedIn = session != null; if (session != null) { usage = storage.Read<Usage>("usage.json"); pendingReset = storage.HasPendingReset(session.AccountId); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException) { error = "Could not load saved data. Sign out, then sign in again."; recovery = true; }
        Topmost = settings.Pinned; PinnedCheck.IsChecked = settings.Pinned;
        NotifyCheck.IsChecked = settings.Notify;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) StartupCheck.IsChecked = key?.GetValue("CodexUsage") != null;
        ApplyTheme(IsLightTheme());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => { if (IsVisible) Hide(); else ShowPanel(); });
        menu.Items.Add("Refresh now", null, async (_, _) => await Refresh());
        menu.Items.Add("Move near taskbar", null, (_, _) => { Dock(); Show(); SaveSettings(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { if (IsVisible && expanded) { SetExpanded(false); } else ShowPanel(); } };
        tray.BalloonTipClicked += (_, _) => ShowPanel();
        UpdateTray("—"); tray.Visible = true;
        var context = new ContextMenu();
        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); context.Items.Add(item); }
        Item("Refresh", async () => await Refresh()); Item("Move near taskbar", () => { Dock(); SaveSettings(); }); Item("Hide to tray", Hide); Item("Exit", Close);
        Handle.ContextMenu = context;
        Loaded += OnLoaded;
        timer.Tick += async (_, _) => { ApplyTheme(IsLightTheme()); Render(); if (DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromMinutes(15)) await Refresh(); };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Closed += (_, _) => {
            closing = true; lifetime.Cancel(); login?.Cancel(); timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
            try { SaveSettings(); } catch (IOException) { /* No credentials or usage are modified during shutdown. */ }
            lifetime.Dispose();
        };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (settings.Left is double x && settings.Top is double y && double.IsFinite(x) && double.IsFinite(y)) { Left = x; Top = y; KeepVisible(); } else Dock();
        if (demo) { usage = DemoUsage(); signedIn = true; }
        Render();
        if (smokeOutput != null) { await RunSmoke(); return; }
        if (!signedIn) SetExpanded(true);
        timer.Start();
        await Refresh();
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionUnlock) QueueRefresh(); }
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) QueueRefresh(); }
    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) QueueRefresh(); }
    private void OnDisplayChanged(object? sender, EventArgs e) { if (!closing) Dispatcher.BeginInvoke(KeepVisible); }
    private void QueueRefresh() { if (!closing) Dispatcher.BeginInvoke(async () => await Refresh()); }
    private void Dock() { Left = SystemParameters.WorkArea.Right - Width - 18; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12; }
    private void KeepVisible()
    {
        // WPF coordinates are device independent; screen coordinates are physical pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = Forms.Screen.FromPoint(new Drawing.Point((int)(Left * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY)));
        var area = screen.WorkingArea;
        var left = area.Left / dpi.DpiScaleX; var top = area.Top / dpi.DpiScaleY;
        Left = Math.Clamp(Left, left, Math.Max(left, area.Right / dpi.DpiScaleX - ActualWidth));
        Top = Math.Clamp(Top, top, Math.Max(top, area.Bottom / dpi.DpiScaleY - ActualHeight));
    }
    public void ShowPanel() { Show(); SetExpanded(true); Activate(); }
    private void SetExpanded(bool value)
    {
        var oldRight = Left + ActualWidth; var oldBottom = Top + ActualHeight;
        expanded = value; Details.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Width = value ? 340 : 190; Chevron.Text = value ? "⌃" : "⌄";
        Details.MaxHeight = Math.Max(150, Math.Min(650, SystemParameters.WorkArea.Height - 80));
        UpdateLayout();
        Left = oldRight - ActualWidth; Top = oldBottom - ActualHeight;
        KeepVisible();
    }
    private void SaveSettings()
    {
        // Persist the compact position even if the details panel is open.
        settings = settings with { Left = Left + ActualWidth - 190, Top = Top + ActualHeight - 36, Pinned = Topmost };
        storage.Save("settings.json", settings);
    }
    private void HandleDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); dragging = false; Handle.Focus(); }
    private void HandleMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || dragging) return;
        var now = e.GetPosition(this);
        if (Math.Abs(start.X - now.X) + Math.Abs(start.Y - now.Y) < 5) return;
        dragging = true; DragMove(); dragStart = null; KeepVisible(); TrySaveSettings();
    }
    private void HandleUp(object sender, MouseButtonEventArgs e) { if (!dragging) SetExpanded(!expanded); dragStart = null; }
    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key is Key.Enter or Key.Space) { SetExpanded(!expanded); e.Handled = true; } }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) SetExpanded(false); }
    private void PinChanged(object sender, RoutedEventArgs e) { Topmost = PinnedCheck.IsChecked == true; TrySaveSettings(); }
    private void NotifyChanged(object sender, RoutedEventArgs e) { settings = settings with { Notify = NotifyCheck.IsChecked == true }; TrySaveSettings(); }
    private void TrySaveSettings() { try { SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StatusLabel.Text = "Could not save preferences."; } }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (demo) { StartupCheck.IsChecked = false; return; }
        try {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartupCheck.IsChecked == true) key.SetValue("CodexUsage", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("CodexUsage", false);
        } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { StartupCheck.IsChecked = false; StatusLabel.Text = "Windows did not allow changing startup settings."; }
    }
    private void HideClick(object sender, RoutedEventArgs e) { SetExpanded(false); Hide(); }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();
    private async Task Refresh()
    {
        if (busy || !signedIn || demo || closing) return;
        busy = true; lastAttempt = DateTimeOffset.UtcNow; Render();
        try { await FetchAndRender(); }
        catch (Exception e) { error = FriendlyError(e); }
        finally { busy = false; if (!closing) Render(); }
    }
    private async Task FetchAndRender()
    {
        var next = await api.Fetch(lifetime.Token);
        storage.Save("usage.json", next);
        var delta = Usage.Change(usage?.Windows.FirstOrDefault()?.Remaining, next.Windows[0].Remaining);
        usage = next; error = null;
        if (settings.Notify && delta != null) tray.ShowBalloonTip(5000, "Codex usage changed", $"{next.Windows[0].Remaining}% remaining ({delta:+0;-0}%). Resets in {Usage.Countdown(next.Windows[0].ResetAt, DateTimeOffset.UtcNow)}.", Forms.ToolTipIcon.Info);
    }
    private static string FriendlyError(Exception e) => e switch {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Session rejected. Sign out and sign in again.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Too many requests. Try again later.",
        HttpRequestException { StatusCode: not null } h => $"Service returned HTTP {(int)h.StatusCode!}. Try again later.",
        HttpRequestException => "Offline or service unavailable. Last successful reading is shown.",
        OperationCanceledException => "Request cancelled or timed out. Try again.",
        CryptographicException => "Windows could not unlock saved credentials. Sign out and sign in again.",
        IOException or UnauthorizedAccessException => "Could not save local data. Check available disk space and permissions.",
        FormatException or JsonException or InvalidOperationException => "Unexpected service response. Refresh or sign in again.",
        _ => "The operation failed. Please try again."
    };
    private void Render()
    {
        if (closing) return;
        var selected = usage?.Windows.ElementAtOrDefault(Math.Clamp(settings.WindowIndex, 0, Math.Max(0, usage.Windows.Count - 1)));
        var stale = usage != null && (error != null || DateTimeOffset.UtcNow - usage.FetchedAt > TimeSpan.FromMinutes(16));
        Percent.Text = selected == null ? "Sign in" : $"{selected.Remaining}%";
        Countdown.Text = selected == null ? "" : (stale ? "! " : "↻ ") + Usage.Countdown(selected.ResetAt, DateTimeOffset.UtcNow);
        Handle.ToolTip = selected == null ? "Click to sign in with ChatGPT" : $"{selected.Name}: {selected.Remaining}% remaining\n{(stale ? "STALE · " : "")}{error ?? "Click for details · drag to move"}";
        UpdateTray(selected == null ? "—" : stale ? "!" : selected.Remaining.ToString());
        tray.Text = selected == null ? "Codex Usage · Sign in" : $"Codex · {selected.Remaining}% left · {Usage.Countdown(selected.ResetAt, DateTimeOffset.UtcNow)}{(stale ? " · stale" : "")}";
        PlanLabel.Text = demo ? "DEMO DATA" : usage?.Plan.ToUpperInvariant() ?? "";
        StatusLabel.Text = error ?? (busy ? "Refreshing…" : usage != null ? $"{(stale ? "Stale · " : "")}Updated {usage.FetchedAt.LocalDateTime:g}" : "Sign in securely with your ChatGPT account.");
        if (signedIn) AccountLabel.Text = demo ? "Preview · no account connected" : "Connected to ChatGPT";
        CreditsLabel.Text = usage == null ? "" : $"{usage.Resets} banked resets" + (usage.Balance == null ? "" : $" · Credit balance: {usage.Balance}");
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.IsEnabled = !busy;
        foreach (var b in new[] { RefreshButton, ResetButton, SignOutButton }) { b.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed; b.IsEnabled = !busy && !demo; }
        SignOutButton.Visibility = signedIn || recovery ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.IsEnabled = !busy && !demo && (usage?.Resets > 0 || pendingReset);
        ResetButton.Content = pendingReset ? "Retry reset" : "Use reset";
        LimitCards.Children.Clear();
        if (usage == null) return;
        for (int i = 0; i < usage.Windows.Count; i++)
        {
            var index = i; var w = usage.Windows[i];
            var panel = new StackPanel();
            var row = new DockPanel();
            var amount = new TextBlock { Text = $"{w.Remaining}%", FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            DockPanel.SetDock(amount, System.Windows.Controls.Dock.Right); row.Children.Add(amount);
            row.Children.Add(new TextBlock { Text = w.Name, TextTrimming = TextTrimming.CharacterEllipsis }); panel.Children.Add(row);
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = w.Remaining, Height = 4, Margin = new Thickness(0, 9, 0, 7), Foreground = Brush(w.Remaining <= 10 ? "#FF947F" : "#56BFA0"), Background = (Brush)Resources["LineBrush"], BorderThickness = new Thickness(0) });
            panel.Children.Add(new TextBlock { Text = w.ResetAt is long at ? $"Resets {DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime:g} · {Usage.Countdown(at, DateTimeOffset.UtcNow)}" : "Reset time unavailable", FontSize = 11, Foreground = (Brush)Resources["MutedBrush"], TextWrapping = TextWrapping.Wrap });
            var button = new System.Windows.Controls.Button { Content = panel, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10), ToolTip = "Show this limit in the floating window", BorderThickness = new Thickness(0) };
            button.Click += (_, _) => { settings = settings with { WindowIndex = index }; TrySaveSettings(); Render(); };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{w.Name}, {w.Remaining}% remaining. Select for compact display");
            LimitCards.Children.Add(button);
        }
    }
    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (busy || demo) return;
        busy = true; error = null; login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); Render();
        try {
            var code = await api.StartLogin(login.Token);
            DeviceCodeText.Text = code.Code; LoginPanel.Visibility = Visibility.Visible;
            StatusLabel.Text = "Waiting for browser authorization…";
            OpenBrowser();
            await api.FinishLogin(code, login.Token);
            signedIn = true; recovery = false; pendingReset = false; usage = null; await FetchAndRender();
        } catch (Exception ex) { error = FriendlyError(ex); }
        finally { login.Dispose(); login = null; busy = false; LoginPanel.Visibility = Visibility.Collapsed; DeviceCodeText.Clear(); if (!closing) Render(); }
    }
    private void OpenBrowser() { try { Process.Start(new ProcessStartInfo("https://auth.openai.com/codex/device") { UseShellExecute = true }); } catch { StatusLabel.Text = "Open https://auth.openai.com/codex/device in your browser."; } }
    private void OpenLoginBrowser(object sender, RoutedEventArgs e) => OpenBrowser();
    private void CopyCode(object sender, RoutedEventArgs e) { try { System.Windows.Clipboard.SetText(DeviceCodeText.Text); } catch (COMException) { StatusLabel.Text = "Clipboard is busy. Select and copy the code manually."; } }
    private void CancelLogin(object sender, RoutedEventArgs e) => login?.Cancel();
    private void SignOut(object sender, RoutedEventArgs e)
    {
        if (busy || demo) return;
        try { storage.ClearSession(); signedIn = false; recovery = false; pendingReset = false; usage = null; error = null; AccountLabel.Text = "Your limits, always in view."; }
        catch (Exception ex) { error = FriendlyError(ex); }
        Render();
    }
    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        if (busy || !signedIn || demo) return;
        busy = true; Render();
        try {
            await FetchAndRender(); Render();
            if (usage!.Resets < 1 && !pendingReset) { error = "No banked resets are available."; return; }
            var message = pendingReset ? "Retry the pending reset with the same request ID? If it was already redeemed, no additional credit will be spent." : "Use one banked reset? This spends one reset credit and resets the eligible usage windows.";
            if (System.Windows.MessageBox.Show(this, message, "Confirm usage reset", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            pendingReset = true;
            var result = await api.ConsumeReset(lifetime.Token);
            pendingReset = false;
            error = null;
            System.Windows.MessageBox.Show(this, result switch { "reset" => "Usage reset completed.", "already_redeemed" => "This request was already redeemed. No additional credit was spent.", "no_credit" => "No reset credit is available.", _ => "No usage window needs a reset." }, "Codex Usage");
            await FetchAndRender();
        } catch (Exception ex) { error = FriendlyError(ex) + " If a reset was sent, retry Use reset safely with the saved request ID."; }
        finally { busy = false; if (!closing) Render(); }
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static bool IsLightTheme() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int n && n == 1; }
    private void ApplyTheme(bool value)
    {
        Resources["SurfaceBrush"] = Brush(value ? "#F5F7FA" : "#191C21"); Resources["TextBrush"] = Brush(value ? "#17212E" : "#F2F5FA");
        Resources["MutedBrush"] = Brush(value ? "#586574" : "#A7B0BD"); Resources["LineBrush"] = Brush(value ? "#CAD2DC" : "#3A404B");
        Resources["CardBrush"] = Brush(value ? "#E9EEF4" : "#242830"); Resources["AccentBrush"] = Brush(value ? "#147D62" : "#7EE3BA");
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private void UpdateTray(string text)
    {
        if (trayPercent == text) return;
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bitmap)) {
            g.Clear(Drawing.Color.FromArgb(29, 42, 42)); g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Drawing.Font("Segoe UI", text.Length > 2 ? 12 : 15, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(157, 242, 210));
            g.DrawString(text, font, brush, new Drawing.RectangleF(0, 0, 32, 32), format);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); var previous = tray.Icon; tray.Icon = (Drawing.Icon)borrowed.Clone(); previous?.Dispose(); }
        finally { DestroyIcon(handle); }
        trayPercent = text;
    }
    private static Usage DemoUsage()
    {
        var now = DateTimeOffset.UtcNow;
        return new("pro", new() { new("Codex · Weekly", 86, now.AddDays(5).AddHours(11).ToUnixTimeSeconds(), 604800), new("Spark · Daily", 74, now.AddHours(8).AddMinutes(24).ToUnixTimeSeconds(), 86400), new("Spark · Weekly", 62, now.AddDays(3).ToUnixTimeSeconds(), 604800) }, 2, "0", now);
    }
    private async Task RunSmoke()
    {
        try {
            Directory.CreateDirectory(smokeOutput!);
            ApplyTheme(false); SetExpanded(false); Render(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Percent.Text != "86%" || Width != 190 || Details.Visibility != Visibility.Collapsed || !tray.Visible) throw new Exception("Compact view / tray");
            Capture("compact.png");
            SetExpanded(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (LimitCards.Children.Count != 3 || Width != 340 || Details.Visibility != Visibility.Visible) throw new Exception("Details");
            Capture("expanded.png");
            ((System.Windows.Controls.Button)LimitCards.Children[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (Percent.Text != "74%") throw new Exception("Limit selection");
            ApplyTheme(true); Render(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("light.png");
            usage = usage! with { FetchedAt = DateTimeOffset.UtcNow.AddHours(-1) }; Render();
            if (!Countdown.Text.StartsWith("! ")) throw new Exception("Stale indicator");
            PinnedCheck.IsChecked = false; PinChanged(this, new()); if (Topmost) throw new Exception("Unpin");
            HideClick(this, new()); if (IsVisible) throw new Exception("Hide"); ShowPanel(); if (!IsVisible) throw new Exception("Restore");
            SetExpanded(false); SaveSettings();
            var saved = storage.Read<Settings>("settings.json"); if (saved?.WindowIndex != 1 || saved.Pinned) throw new Exception("Preferences persistence");
            File.WriteAllText(Path.Combine(smokeOutput!, "smoke-result.txt"), "PASS: compact, expand, three limit cards, select limit, light/dark render, stale indicator, pin, hide/restore, settings persistence, tray.\n");
            Close();
        } catch (Exception e) { File.WriteAllText(Path.Combine(smokeOutput!, "smoke-result.txt"), e.ToString()); System.Windows.Application.Current.Shutdown(1); }
    }
    private void Capture(string name)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(smokeOutput!, name)); encoder.Save(stream);
    }
}
