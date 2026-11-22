using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CaboCam.ViewModels;
using CaboCam.Views;

namespace CaboCam;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.AskHostPort = initial =>
        {
            var dlg = new HostPromptDialog(initial) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        };

        vm.IosApp.AskLogin = initial =>
        {
            ShowWindowForPrompt();
            var dlg = new AppleIdLoginDialog(initial) { Owner = this };
            return dlg.ShowDialog() == true ? (dlg.Email, dlg.Password) : null;
        };
        vm.IosApp.AskCode = _ =>
        {
            ShowWindowForPrompt();
            var dlg = new TwoFactorDialog { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Code : null;
        };
        vm.IosApp.PickIpa = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha o CaboCam .ipa",
                Filter = "App iOS (*.ipa)|*.ipa",
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };

        // Track slider drags so incoming phone state does not fight the user.
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => { if (SliderKey(e.OriginalSource) is { } k) _vm.BeginDrag(k); }));
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => { if (SliderKey(e.OriginalSource) is { } k) _vm.EndDrag(k); }));
    }

    private void ShowWindowForPrompt()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private static string? SliderKey(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null && d is not Slider) d = VisualTreeHelper.GetParent(d);
        return (d as Slider)?.Tag as string;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource bmp) return;
        double aw = PreviewImage.ActualWidth, ah = PreviewImage.ActualHeight;
        if (aw <= 0 || ah <= 0 || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return;

        // Stretch=Uniform letterboxes the frame: map the click to the displayed frame rectangle.
        double scale = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);
        double dw = bmp.PixelWidth * scale, dh = bmp.PixelHeight * scale;
        double ox = (aw - dw) / 2, oy = (ah - dh) / 2;

        var p = e.GetPosition(PreviewImage);
        double nx = (p.X - ox) / dw, ny = (p.Y - oy) / dh;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return;

        _vm.SetPointOfInterest(nx, ny);
        ShowTapRing(p);
    }

    private void ShowTapRing(Point p)
    {
        Canvas.SetLeft(TapRing, p.X - TapRing.Width / 2);
        Canvas.SetTop(TapRing, p.Y - TapRing.Height / 2);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(250) };
        TapRing.BeginAnimation(OpacityProperty, null);
        TapRing.Opacity = 1;
        TapRing.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(1.4, 1.0, TimeSpan.FromMilliseconds(250));
        TapScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        TapScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }
}
