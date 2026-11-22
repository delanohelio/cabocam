using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CaboCam.Services;
using CaboCam.Usbmux;

namespace CaboCam.ViewModels;

/// <summary>"App do iPhone" card: renews (re-signs + reinstalls) the iOS app with the user's Apple ID.</summary>
public sealed class IosAppViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan WarnBefore = TimeSpan.FromDays(2);

    private readonly Dispatcher _ui;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _cts;

    public IosAppViewModel(SettingsStore settings)
    {
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _settings = settings;

        InstallLatestCommand = new RelayCommand(() => _ = RunInstallAsync(fromFile: false), () => !IsBusy);
        InstallFromFileCommand = new RelayCommand(() => _ = RunInstallAsync(fromFile: true), () => !IsBusy);
        LoginCommand = new RelayCommand(() => _ = RunLoginAsync(), () => !IsBusy);
        LogoutCommand = new RelayCommand(() => _ = RunLogoutAsync(), () => !IsBusy && IsLoggedIn);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RevealDevModeCommand = new RelayCommand(() => _ = RunRevealDevModeAsync(), () => !IsBusy);
        RefreshReleasesCommand = new RelayCommand(() => _ = LoadReleasesAsync(), () => !IsBusy);
        ToggleHelpCommand = new RelayCommand(() => ShowHelp = !ShowHelp);
        // First run (nothing installed yet): show the steps up front.
        _showHelp = settings.Current.IosSignedAt is null;

        _clock = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMinutes(30) };
        _clock.Tick += (_, _) => RefreshExpiry();
        RefreshExpiry();
    }

    public void Start()
    {
        _clock.Start();
        _ = LoadReleasesAsync();
        if (!IosAppSigner.IsAvailable)
        {
            AccountText = "plumesign.exe não está instalado junto do CaboCam.";
            return;
        }
        _ = RefreshAccountAsync();
    }

    // ---- set by the window -----------------------------------------------------------
    /// <summary>Asks for Apple ID e-mail and password; null if cancelled.</summary>
    public Func<string, (string Email, string Password)?>? AskLogin { get; set; }
    /// <summary>Asks for the 2FA code (argument = prompt text); null if cancelled.</summary>
    public Func<string, string?>? AskCode { get; set; }
    /// <summary>Lets the user pick a local .ipa; null if cancelled.</summary>
    public Func<string?>? PickIpa { get; set; }

    // ---- commands ----------------------------------------------------------------------
    public RelayCommand InstallLatestCommand { get; }
    public RelayCommand InstallFromFileCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand LogoutCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RevealDevModeCommand { get; }
    public RelayCommand ToggleHelpCommand { get; }

    /// <summary>Step-by-step for Developer Mode and "Untrusted Developer" (shown after an install).</summary>
    private bool _showHelp;
    public bool ShowHelp
    {
        get => _showHelp;
        set { if (Set(ref _showHelp, value)) OnPropertyChanged(nameof(HelpButtonText)); }
    }
    public string HelpButtonText => _showHelp ? "Ocultar ajuda" : "Ajuda";
    public RelayCommand RefreshReleasesCommand { get; }

    // ---- bindable state ----------------------------------------------------------------
    private string _expiryText = "";
    public string ExpiryText { get => _expiryText; private set => Set(ref _expiryText, value); }

    private Brush _expiryBrush = Brushes.Gray;
    public Brush ExpiryBrush { get => _expiryBrush; private set => Set(ref _expiryBrush, value); }

    private bool _needsRenewal;
    public bool NeedsRenewal { get => _needsRenewal; private set => Set(ref _needsRenewal, value); }

    private string _accountText = "Verificando Apple ID…";
    public string AccountText { get => _accountText; private set => Set(ref _accountText, value); }

    private bool _isLoggedIn;
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set { if (Set(ref _isLoggedIn, value)) RaiseCommands(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ProgressIndeterminate));
            RaiseCommands();
        }
    }

    private string _activity = "";
    public string Activity { get => _activity; private set { Set(ref _activity, value); OnPropertyChanged(nameof(HasActivity)); } }
    public bool HasActivity => _activity.Length > 0;

    private string _error = "";
    public string Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    private int _percent;
    public int Percent
    {
        get => _percent;
        private set { if (Set(ref _percent, value)) OnPropertyChanged(nameof(ProgressIndeterminate)); }
    }

    public bool ProgressIndeterminate => _isBusy && _percent == 0;

    private void RaiseCommands()
    {
        InstallLatestCommand.RaiseCanExecuteChanged();
        InstallFromFileCommand.RaiseCanExecuteChanged();
        LoginCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RevealDevModeCommand.RaiseCanExecuteChanged();
        RefreshReleasesCommand.RaiseCanExecuteChanged();
    }

    // =============================================================================
    // Expiry
    // =============================================================================

    private void RefreshExpiry()
    {
        var s = _settings.Current;
        if (s.IosSignedAt is not { } signedAt)
        {
            ExpiryText = "Nenhuma instalação feita pelo CaboCam ainda.";
            ExpiryBrush = Brushes.Gray;
            NeedsRenewal = true;
            return;
        }

        var expires = signedAt + IosAppSigner.FreeProfileLifetime;
        var left = expires - DateTimeOffset.Now;
        var version = string.IsNullOrEmpty(s.IosSignedVersion) ? "" : $" ({s.IosSignedVersion})";
        if (left <= TimeSpan.Zero)
        {
            ExpiryText = $"Expirou em {expires.LocalDateTime:dd/MM HH:mm}{version}. Renove para o app voltar a abrir.";
            ExpiryBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            NeedsRenewal = true;
        }
        else
        {
            var when = left.TotalHours < 24 ? $"{Math.Max(1, (int)left.TotalHours)} h" : $"{(int)left.TotalDays} dia(s)";
            ExpiryText = $"Instalado em {signedAt.LocalDateTime:dd/MM HH:mm}{version}. Expira em {when} ({expires.LocalDateTime:dd/MM HH:mm}).";
            NeedsRenewal = left <= WarnBefore;
            ExpiryBrush = NeedsRenewal ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)) : new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        }
    }

    // =============================================================================
    // Account
    // =============================================================================

    private async Task RefreshAccountAsync()
    {
        try
        {
            var email = await IosAppSigner.GetSelectedAccountAsync(CancellationToken.None);
            IsLoggedIn = email is not null;
            AccountText = email is null ? "Nenhum Apple ID conectado." : $"Apple ID: {email}";
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar conta do plumesign", ex);
            AccountText = "Não foi possível consultar o Apple ID.";
        }
    }

    private async Task<bool> RunLoginAsync()
    {
        var creds = AskLogin?.Invoke("");
        if (creds is null) return false;

        return await Busy("Entrando no Apple ID…", async ct =>
        {
            var log = new Progress<string>(l => Activity = Trim(l));
            await IosAppSigner.LoginAsync(creds.Value.Email, creds.Value.Password,
                prompt => _ui.InvokeAsync(() => AskCode?.Invoke(prompt)).Task, log, ct);
            await RefreshAccountAsync();
        });
    }

    private async Task RunLogoutAsync()
    {
        await Busy("Saindo do Apple ID…", async ct =>
        {
            await IosAppSigner.LogoutAsync(ct);
            await RefreshAccountAsync();
        });
    }

    // =============================================================================
    // Sign + install
    // =============================================================================

    private async Task RunInstallAsync(bool fromFile)
    {
        string? localIpa = null;
        if (fromFile)
        {
            localIpa = PickIpa?.Invoke();
            if (localIpa is null) return;
        }

        // The saved plumesign session is reused; only ask for the Apple ID when there is none
        // (or when signing reports that the session is no longer valid).
        if (!IsLoggedIn) await RefreshAccountAsync();
        if (!IsLoggedIn && !await RunLoginAsync()) return;

        var ok = await TryInstallAsync(localIpa);
        if (!ok && _lastNeedsLogin && await RunLoginAsync())
            await TryInstallAsync(localIpa);
    }

    private bool _lastNeedsLogin;

    private Task<bool> TryInstallAsync(string? localIpa) => Busy("Preparando…", async ct =>
    {
        _lastNeedsLogin = false;
        var log = new Progress<string>(l => Activity = Trim(l));

        var device = await FindDeviceAsync(ct);

        string ipa;
        string version;
        if (localIpa is not null)
        {
            ipa = localIpa;
            version = System.IO.Path.GetFileNameWithoutExtension(localIpa);
        }
        else
        {
            var release = SelectedRelease;
            if (release is null)
            {
                Activity = "Procurando versões no GitHub…";
                await LoadReleasesCoreAsync(ct);
                release = SelectedRelease ?? throw new IosSignException(
                    $"Nenhum Release com .ipa em github.com/{_settings.Current.IosReleaseRepo}.");
            }
            ipa = await IosAppSigner.DownloadAsync(release, log, ct);
            version = release.Tag;
        }

        Activity = "Assinando e instalando no iPhone (mantenha-o desbloqueado)…";
        Percent = 0;
        var pct = new Progress<int>(p => { Percent = p; Activity = $"Instalando no iPhone… {p}%"; });
        try
        {
            await IosAppSigner.SignAndInstallAsync(ipa, device.DeviceId, log, pct, ct);
        }
        catch (IosSignException ex) when (ex.NeedsLogin)
        {
            _lastNeedsLogin = true;
            throw;
        }

        var s = _settings.Current;
        s.IosSignedAt = DateTimeOffset.Now;
        s.IosSignedVersion = version;
        s.IosSignedUdid = device.SerialNumber;
        _settings.SaveSoon();
        RefreshExpiry();
        Activity = "App instalado no iPhone.";
        ShowHelp = true;
    }, keepActivity: true);

    // =============================================================================
    // Releases (version picker)
    // =============================================================================

    public ObservableCollection<IpaRelease> Releases { get; } = new();

    private IpaRelease? _selectedRelease;
    public IpaRelease? SelectedRelease { get => _selectedRelease; set => Set(ref _selectedRelease, value); }

    private async Task LoadReleasesAsync()
    {
        try
        {
            await LoadReleasesCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar Releases", ex);
            if (!IsBusy) Error = ex is IosSignException ? ex.Message : "Não foi possível listar as versões no GitHub.";
        }
    }

    private async Task LoadReleasesCoreAsync(CancellationToken ct)
    {
        var list = await IosAppSigner.GetReleasesAsync(_settings.Current.IosReleaseRepo, ct);
        var keep = SelectedRelease?.Tag;
        Releases.Clear();
        foreach (var r in list) Releases.Add(r);
        // Default = newest; keep an explicit older choice across refreshes.
        SelectedRelease = (keep is null ? null : Releases.FirstOrDefault(r => r.Tag == keep && !r.IsLatest))
                          ?? Releases.FirstOrDefault();
    }

    // =============================================================================
    // Developer Mode
    // =============================================================================

    private Task RunRevealDevModeAsync() => Busy("Pedindo ao iPhone para mostrar o Modo de Desenvolvedor…", async ct =>
    {
        var device = await FindDeviceAsync(ct);
        var err = await Lockdown.RevealDeveloperModeAsync(device, ct);
        if (err is not null) throw new IosSignException("O iPhone recusou: " + err);
        Activity = "Pronto. No iPhone: Ajustes → Privacidade e Segurança → Modo de Desenvolvedor (no fim da lista). " +
                   "Ative, reinicie e confirme \"Ativar\".";
    }, keepActivity: true);

    private async Task<UsbmuxDevice> FindDeviceAsync(CancellationToken ct)
    {
        if (!await UsbmuxClient.IsAvailableAsync(ct))
            throw new IosSignException("Serviço da Apple (Apple Mobile Device Service) não encontrado. Instale o app Dispositivos Apple.");

        var usb = (await UsbmuxClient.ListDevicesAsync(ct)).Where(d => d.IsUsb).ToList();
        if (usb.Count == 0)
            throw new IosSignException("Conecte o iPhone no cabo USB e desbloqueie-o.");

        var preferred = _settings.Current.LastDeviceSerial;
        return usb.FirstOrDefault(d => d.SerialNumber == preferred) ?? usb[0];
    }

    // =============================================================================
    // Helpers
    // =============================================================================

    private async Task<bool> Busy(string start, Func<CancellationToken, Task> work, bool keepActivity = false)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Error = "";
        Activity = start;
        Percent = 0;
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
            if (!keepActivity) Activity = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            Activity = "";
            Error = "Operação cancelada.";
        }
        catch (IosSignException ex)
        {
            Activity = "";
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("App do iPhone: falha", ex);
            Activity = "";
            Error = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
        return false;
    }

    private static string Trim(string line)
    {
        // env_logger prefix: "[2026-11-22T12:00:00Z INFO  plumesign::commands::sign] message"
        var i = line.IndexOf("] ", StringComparison.Ordinal);
        if (line.StartsWith('[') && i > 0) line = line[(i + 2)..];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public void Dispose()
    {
        _clock.Stop();
        _cts?.Cancel();
    }
}
