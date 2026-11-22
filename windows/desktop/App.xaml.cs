using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CaboCam.Services;
using CaboCam.ViewModels;
using Forms = System.Windows.Forms;

namespace CaboCam;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsStore? _settings;
    private VirtualCameraService? _vcam;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Exceção não tratada (AppDomain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Exceção de tarefa não observada", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _singleInstance = new Mutex(true, @"Local\CaboCam.Desktop.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("O CaboCam já está em execução (veja o ícone na bandeja do sistema).", "CaboCam",
                MessageBoxButton.OK, MessageBoxImage.Information);
            IsExiting = true;
            Shutdown();
            return;
        }

        Log.Cleanup();
        Log.Info("CaboCam iniciando");

        CaboCam.Native.NativeMethods.EnableNativeLog();

        _settings = new SettingsStore();
        _settings.Load();

        _vcam = new VirtualCameraService();
        _vm = new MainViewModel(_settings, _vcam);
        _window = new MainWindow(_vm);

        CreateTrayIcon();

        if (_settings.Current.StartMinimized)
        {
            _window.ShowInTaskbar = true;
            _window.WindowState = WindowState.Minimized;
        }
        _window.Show();
        if (_settings.Current.StartMinimized) _window.Hide();

        _vm.Start();
        // Creating the virtual camera can take a moment; keep the UI responsive.
        Task.Run(() => _vcam.Register());
    }

    private void CreateTrayIcon()
    {
        _trayIcon = BuildIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mostrar", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApplication());

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "CaboCam",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static Icon BuildIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/CaboCam.ico"));
            if (res is not null)
            {
                using var s = res.Stream;
                return new Icon(s, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // fall back to the drawn icon below
        }

        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var body = new SolidBrush(Color.FromArgb(59, 130, 246));
                g.FillEllipse(body, 1, 1, 30, 30);
                using var ring = new SolidBrush(Color.FromArgb(15, 17, 21));
                g.FillEllipse(ring, 8, 8, 16, 16);
                using var lens = new SolidBrush(Color.White);
                g.FillEllipse(lens, 12, 12, 8, 8);
            }
            var handle = bmp.GetHicon();
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private bool _trayHintShown;

    /// <summary>
    /// The window was closed to the tray: the process (and the "CaboCam" camera) keeps running.
    /// Say so once per run, so it is clear how to remove the camera.
    /// </summary>
    public void ShowTrayHintOnce()
    {
        if (_trayHintShown || _tray is null) return;
        _trayHintShown = true;
        try
        {
            _tray.ShowBalloonTip(6000, "CaboCam continua aberto",
                "A câmera virtual continua disponível enquanto o CaboCam roda na bandeja. Para encerrar e remover a câmera, clique com o botão direito aqui → Sair.",
                Forms.ToolTipIcon.Info);
        }
        catch { /* notifications disabled */ }
    }

    private void ExitApplication()
    {
        IsExiting = true;
        _window?.Close();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Exceção não tratada (UI)", e.Exception);
        e.Handled = true;
        if (_vm is not null) _vm.LastError = "Erro inesperado: " + e.Exception.Message + " (veja " + Log.Directory + ")";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _vm?.Dispose(); } catch (Exception ex) { Log.Error("Dispose VM", ex); }
        try { _vcam?.Dispose(); } catch (Exception ex) { Log.Error("Dispose vcam", ex); }
        try { _settings?.Dispose(); } catch (Exception ex) { Log.Error("Dispose settings", ex); }
        try { _singleInstance?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstance?.Dispose();
        Log.Info("CaboCam encerrado");
        base.OnExit(e);
    }
}
