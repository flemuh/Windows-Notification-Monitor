using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinForms = System.Windows.Forms;

namespace ChromeNotificationWatcher;

public partial class MainWindow : Window
{
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly DispatcherTimer _fallbackTimer;
    private readonly WinForms.NotifyIcon _tray;

    private Settings _settings = new();
    private bool _watching;
    private bool _allowExit;
    private string _configPath = "";

    public MainWindow()
    {
        InitializeComponent();

        _configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        LoadSettings();

        _fallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ReconcileEverySeconds))
        };
        _fallbackTimer.Tick += async (_, _) => await SyncNotificationsAsync();

        _tray = new WinForms.NotifyIcon
        {
            Text = "Chrome Notification Watcher",
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true
        };

        var trayMenu = new WinForms.ContextMenuStrip();
        trayMenu.Items.Add("Abrir", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        trayMenu.Items.Add("Testar alerta", null, (_, _) =>
            Dispatcher.BeginInvoke(new Action(async () =>
                await FireAlertAsync(
                    "TEST ALERT",
                    "Teste manual pelo ícone da bandeja",
                    "ChromeNotificationWatcher"))));
        trayMenu.Items.Add("Sair", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        Loaded += MainWindow_Loaded;

        Log("Chrome Notification Watcher");
        Log($"Filtro do app: {_settings.ChromeAppNameContains}");
        Log($"Reconciliação fallback: {_settings.ReconcileEverySeconds}s");
        Log($"NTFY env: {_settings.NtfyTopicEnvVar}");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await StartWatcherAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _fallbackTimer.Stop();

        if (_watching)
        {
            try { _listener.NotificationChanged -= Listener_NotificationChanged; }
            catch { }
        }

        _tray.Visible = false;
        _tray.Dispose();
        _http.Dispose();

        base.OnClosing(e);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _settings = new Settings();
                return;
            }

            var json = File.ReadAllText(_configPath);
            _settings = JsonSerializer.Deserialize<Settings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new Settings();
        }
        catch (Exception ex)
        {
            _settings = new Settings();
            Log($"CONFIG ERROR: {ex.Message}");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
        => await StartWatcherAsync();

    private async Task StartWatcherAsync()
    {
        StartButton.IsEnabled = false;

        try
        {
            if (_watching)
            {
                StatusText.Text = "Watcher já está ativo.";
                return;
            }

            LoadSettings();
            Log("Solicitando acesso às notificações do Windows...");

            // Must be called from the UI thread.
            var access = await _listener.RequestAccessAsync();

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                StatusText.Text =
                    $"Acesso às notificações: {access}. " +
                    "Libere o acesso nas Configurações do Windows e clique em iniciar.";
                Log($"NOTIFICATION ACCESS: {access}");
                return;
            }

            Log("NOTIFICATION ACCESS: ALLOWED");

            await BaselineAsync();

            // Primary native event path.
            _listener.NotificationChanged += Listener_NotificationChanged;

            // Redundant reconciliation through the same official Windows API.
            _fallbackTimer.Interval = TimeSpan.FromSeconds(
                Math.Max(1, _settings.ReconcileEverySeconds));
            _fallbackTimer.Start();

            _watching = true;
            StatusText.Text =
                $"ATIVO — observando notificações cujo app contém " +
                $"“{_settings.ChromeAppNameContains}”.";

            Log("WATCHER READY");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao iniciar: {ex.Message}";
            Log($"START ERROR: {ex}");
        }
        finally
        {
            if (!_watching)
                StartButton.IsEnabled = true;
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            await FireAlertAsync(
                "TEST ALERT",
                "Teste manual de alarme do PC + ntfy",
                "ChromeNotificationWatcher");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{_configPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"CONFIG OPEN ERROR: {ex.Message}");
        }
    }

    private void Listener_NotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(
            new Action(async () => await SyncNotificationsAsync()));
    }

    private async Task BaselineAsync()
    {
        var notifications =
            await _listener.GetNotificationsAsync(NotificationKinds.Toast);

        foreach (var notification in notifications)
            _seen.TryAdd(BuildKey(notification), notification.CreationTime);

        Log($"BASELINE pronta: {notifications.Count} notificação(ões) existente(s).");
    }

    private async Task SyncNotificationsAsync()
    {
        if (!await _syncLock.WaitAsync(0))
            return;

        try
        {
            if (_listener.GetAccessStatus() !=
                UserNotificationListenerAccessStatus.Allowed)
            {
                StatusText.Text = "Acesso às notificações foi revogado/bloqueado.";
                Log("NOTIFICATION ACCESS REVOKED");
                return;
            }

            var notifications =
                await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            foreach (var notification in notifications.OrderBy(x => x.CreationTime))
            {
                var key = BuildKey(notification);

                if (!_seen.TryAdd(key, notification.CreationTime))
                    continue;

                try
                {
                    await ProcessNotificationAsync(notification);
                }
                catch (Exception ex)
                {
                    Log($"NOTIFICATION ERROR id={notification.Id}: {ex.Message}");
                }
            }

            PruneSeen();
        }
        catch (Exception ex)
        {
            Log($"SYNC ERROR: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task ProcessNotificationAsync(UserNotification notification)
    {
        string appName;

        try
        {
            appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
        }
        catch
        {
            appName = "";
        }

        if (!appName.Contains(
                _settings.ChromeAppNameContains,
                StringComparison.OrdinalIgnoreCase))
            return;

        var (title, body) = ExtractText(notification);

        if (!MatchesOptionalTextFilter(title, body))
        {
            Log($"IGNORED Chrome notification id={notification.Id}: filtro de texto.");
            return;
        }

        Log("");
        Log(new string('=', 70));
        Log($"NEW CHROME NOTIFICATION id={notification.Id}");
        Log($"App: {appName}");
        Log($"Title: {title}");
        Log($"Body: {body}");
        Log(new string('=', 70));

        await FireAlertAsync(title, body, appName);
    }

    private (string Title, string Body) ExtractText(UserNotification notification)
    {
        try
        {
            var binding = notification.Notification?
                .Visual?
                .GetBinding(KnownNotificationBindings.ToastGeneric);

            if (binding is null)
                return ("Chrome notification", "");

            var elements = binding.GetTextElements();

            var title = elements.FirstOrDefault()?.Text?.Trim();

            var body = string.Join(
                Environment.NewLine,
                elements.Skip(1)
                    .Select(x => x.Text?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

            return (
                string.IsNullOrWhiteSpace(title)
                    ? "Chrome notification"
                    : title!,
                body
            );
        }
        catch (Exception ex)
        {
            Log($"TEXT READ ERROR: {ex.Message}");
            return ("Chrome notification", "");
        }
    }

    private bool MatchesOptionalTextFilter(string title, string body)
    {
        var filters = _settings.TitleOrBodyContains ?? Array.Empty<string>();

        if (filters.Length == 0)
            return true;

        var combined = $"{title}\n{body}";

        return filters.Any(filter =>
            !string.IsNullOrWhiteSpace(filter) &&
            combined.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private async Task FireAlertAsync(string title, string body, string appName)
    {
        Log("PC ALARM disparado");
        _ = Task.Run(PlayAlarmLoop);
        await SendPhoneBurstAsync(title, body, appName);
    }

    private void PlayAlarmLoop()
    {
        try
        {
            var path = _settings.AlarmFile;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"PC ALARM ERROR: arquivo não encontrado: {path}");
                return;
            }

            var end = DateTime.UtcNow.AddSeconds(
                Math.Max(1, _settings.AlarmSeconds));

            using var player = new SoundPlayer(path);

            while (DateTime.UtcNow < end)
            {
                player.PlaySync();
                Thread.Sleep(200);
            }
        }
        catch (Exception ex)
        {
            Log($"PC ALARM ERROR: {ex.Message}");
        }
    }

    private async Task SendPhoneBurstAsync(
        string title,
        string body,
        string appName)
    {
        var topic =
            Environment.GetEnvironmentVariable(
                _settings.NtfyTopicEnvVar,
                EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(_settings.NtfyTopicEnvVar);

        topic = topic?.Trim();

        if (string.IsNullOrWhiteSpace(topic))
        {
            Log($"PHONE ERROR: {_settings.NtfyTopicEnvVar} não configurado");
            return;
        }

        var repeat = Math.Max(1, _settings.PhoneRepeatCount);

        for (var number = 1; number <= repeat; number++)
        {
            await SendPhoneOnceAsync(
                topic, title, body, appName, number, repeat);

            if (number < repeat)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        Math.Max(1, _settings.PhoneRepeatGapSeconds)));
            }
        }
    }

    private async Task SendPhoneOnceAsync(
        string topic,
        string title,
        string body,
        string appName,
        int number,
        int repeat)
    {
        try
        {
            var url =
                $"{_settings.NtfyBaseUrl.TrimEnd('/')}/" +
                $"{Uri.EscapeDataString(topic)}";

            var message =
                $"Chrome notification\n" +
                $"App: {appName}\n" +
                $"Title: {title}\n" +
                $"{body}\n\nOPEN TASK NOW";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(message))
            };

            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain")
                {
                    CharSet = "utf-8"
                };

            request.Headers.TryAddWithoutValidation(
                "Title", $"TRITON ALERT ({number}/{repeat})");
            request.Headers.TryAddWithoutValidation("Priority", "5");
            request.Headers.TryAddWithoutValidation(
                "Tags", "rotating_light,warning");

            using var cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var response =
                await _http.SendAsync(request, cts.Token);

            response.EnsureSuccessStatusCode();

            Log($"PHONE alerta enviado ({number}/{repeat})");
        }
        catch (Exception ex)
        {
            Log($"PHONE ERROR: {ex.Message}");
        }
    }

    private static string BuildKey(UserNotification notification)
    {
        string appName;

        try
        {
            appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
        }
        catch
        {
            appName = "";
        }

        return $"{appName}|{notification.Id}|{notification.CreationTime.UtcTicks}";
    }

    private void PruneSeen()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

        foreach (var pair in _seen)
        {
            if (pair.Value < cutoff)
                _seen.TryRemove(pair.Key, out _);
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.WriteLine(line);

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "ChromeNotificationWatcher");

            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "watcher.log"),
                line + Environment.NewLine);
        }
        catch { }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }));
    }

    private sealed class Settings
    {
        public string ChromeAppNameContains { get; set; } = "Google Chrome";
        public string[] TitleOrBodyContains { get; set; } = Array.Empty<string>();
        public string AlarmFile { get; set; } = @"C:\Windows\Media\Alarm01.wav";
        public int AlarmSeconds { get; set; } = 30;
        public int PhoneRepeatCount { get; set; } = 3;
        public int PhoneRepeatGapSeconds { get; set; } = 8;
        public int ReconcileEverySeconds { get; set; } = 2;
        public string NtfyBaseUrl { get; set; } = "https://ntfy.sh";
        public string NtfyTopicEnvVar { get; set; } = "NTFY_TOPIC";
    }
}
