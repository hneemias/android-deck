using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PhoneBox.Agent;

/// <summary>System tray icon: status, settings, logs, start with Windows, quit.</summary>
public sealed class Tray : IHostedService
{
    private readonly string SettingsUrl;

    private readonly IHostApplicationLifetime _life;
    private readonly AdbReverse _adb;
    private Thread? _thread;
    private ApplicationContext? _ctx;
    private Control? _invoker;
    private NotifyIcon? _icon;
    private System.Windows.Forms.Timer? _pollTimer;

    public Tray(IHostApplicationLifetime life, AdbReverse adb, AgentEndpoint endpoint)
    {
        SettingsUrl = endpoint.SettingsUrl;
        _life = life;
        _adb = adb;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { _invoker?.BeginInvoke(() => { _pollTimer?.Stop(); _pollTimer?.Dispose(); if (_icon != null) _icon.Visible = false; _ctx?.ExitThread(); }); } catch { }
        return Task.CompletedTask;
    }

    private void Run()
    {
        Application.EnableVisualStyles();
        // Invisible control used to marshal calls onto this UI thread (its handle is created here).
        var host = new Control();
        _ = host.Handle;
        _invoker = host;
        _ctx = new ApplicationContext();

        using var iconStream = AppPaths.Resource("phonebox.ico");
        var status = new ToolStripMenuItem { Enabled = false };
        var autostart = new ToolStripMenuItem("Iniciar com o Windows");
        autostart.Click += (_, _) => { ToggleAutostart(); autostart.Checked = File.Exists(AppPaths.StartupShortcut); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"CelularBox v{AppPaths.Version}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Abrir Configurações…", null, (_, _) => OpenUrl(SettingsUrl)) { Font = new Font(menu.Font, FontStyle.Bold) });
        // cancelled while Windows is ending the session (no adb is started then, for at most a minute): nothing to show
        menu.Items.Add("Abrir painel no celular (USB)", null, async (_, _) => { try { await _adb.OpenPanelAsync(); } catch (OperationCanceledException) { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Editar configurações (config.json)", null, (_, _) => Open("notepad.exe", AppPaths.ConfigPath));
        menu.Items.Add("Visualizar logs", null, (_, _) => Open("notepad.exe", AppPaths.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Fechar", null, (_, _) => _life.StopApplication());

        void UpdateStatus()
        {
            var (menuText, tipText) = GetStatusTexts();
            status.Text = menuText;
            if (_icon != null) _icon.Text = tipText;
            autostart.Checked = File.Exists(AppPaths.StartupShortcut);
        }

        menu.Opening += (_, _) => UpdateStatus();

        _icon = new NotifyIcon
        {
            Icon = iconStream is null ? SystemIcons.Application : new Icon(iconStream, 32, 32),
            Text = "CelularBox — Aguardando celular...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenUrl(SettingsUrl);

        _adb.StateChanged += () =>
        {
            try { host.BeginInvoke(UpdateStatus); } catch { }
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _pollTimer.Tick += (_, _) => UpdateStatus();
        _pollTimer.Start();

        UpdateStatus();

        Application.Run(_ctx);
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static string Truncate(string text, int max = 63) => text.Length > max ? text[..max] : text;

    private (string MenuText, string TooltipText) GetStatusTexts()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var recentPhones = PhoneRegistry.All()
            .Where(p => now - p.LastSeen < 30_000)
            .ToList();

        var usbConnected = _adb.Connected;
        var wifiPhones = recentPhones.Where(p => p.Via == "Wi-Fi").ToList();

        if (usbConnected && wifiPhones.Count > 0)
        {
            var wifiName = wifiPhones[0].Model;
            return ($"🟢 Celular conectado (USB e Wi-Fi: {wifiName})",
                    Truncate($"CelularBox — Conectado (USB e Wi-Fi: {wifiName})"));
        }
        if (usbConnected)
        {
            var dev = string.IsNullOrWhiteSpace(_adb.Devices) ? "Dispositivo" : _adb.Devices;
            return ($"🟢 Celular conectado via USB ({dev})",
                    Truncate($"CelularBox — Conectado (USB: {dev})"));
        }
        if (wifiPhones.Count > 0)
        {
            var phone = wifiPhones[0];
            return ($"🟢 Celular conectado via Wi-Fi ({phone.Model})",
                    Truncate($"CelularBox — Conectado (Wi-Fi: {phone.Model})"));
        }

        return ("⚪ Aguardando celular...", "CelularBox — Aguardando celular...");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void Open(string app, string path)
    {
        try { Process.Start(new ProcessStartInfo(app, $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Adds/removes a Startup-folder shortcut (only when the user clicks the menu item).</summary>
    private static void ToggleAutostart()
    {
        try
        {
            if (File.Exists(AppPaths.StartupShortcut)) { File.Delete(AppPaths.StartupShortcut); return; }
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(AppPaths.StartupShortcut);
            lnk.TargetPath = Environment.ProcessPath;
            lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            lnk.Description = "CelularBox - Painel e Controle";
            lnk.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível alterar o atalho de inicialização: " + ex.Message, "CelularBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
