using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Midfielder.Core.Crypto;
using QRCoder;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>The pairing QR code in a Fluent window, with the fingerprint and a countdown to expiry.</summary>
internal sealed class PairingWindow : Fluent.FluentWindow
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _expires = DateTime.Now.AddMinutes(5);

    public PairingWindow(PairingInvite invite)
    {
        WindowSpeed.Attach(this, "Pairing window");
        WpfApp.Ensure();
        Title = "Pair your phone";
        Icon = AppIcon.Image(32);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowBackdropType = Fluent.WindowBackdropType.Mica;
        ExtendsContentIntoTitleBar = true;
        FontFamily = Fx.Font;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(invite.ToUri(), QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png.GetGraphic(10, [0x17, 0x4F, 0x42, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF])))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();

        var countdown = Fx.Caption("");
        countdown.HorizontalAlignment = HorizontalAlignment.Center;
        var qr = Fx.Card(new Image { Source = bmp, Width = 300, Height = 300 }, 16, lift: false);
        qr.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Fx.Text("Scan with Midfielder on your phone", 20, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var how = Fx.Caption("On the phone: Midfielder → Pair with your PC, then point the camera at this code.", wrap: true);
        how.TextAlignment = TextAlignment.Center;
        var fp = Fx.Text(invite.Fingerprint.Display(), 13, color: Fx.PrimaryDark);
        fp.FontFamily = Fx.Mono;
        fp.HorizontalAlignment = HorizontalAlignment.Center;
        var fpLabel = Fx.Caption("The phone should show the same fingerprint:");
        fpLabel.HorizontalAlignment = HorizontalAlignment.Center;
        var body = Fx.Stack(14, title, how, qr, fpLabel, fp, countdown);
        body.Margin = new Thickness(28, 8, 28, 28);
        fp.Margin = new Thickness(0, 4, 0, 0);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Fluent.TitleBar { Title = "Pair your phone", ShowMaximize = false, ShowMinimize = false, ShowHelp = false });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        _timer.Tick += (_, _) => Tick(countdown);
        Tick(countdown);
        _timer.Start();
        Loaded += (_, _) => Anim.Enter(body);
    }

    private void Tick(TextBlock countdown)
    {
        var left = _expires - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            countdown.Text = "This code has expired. Close and open Pair a phone again.";
            countdown.Foreground = Fx.Danger;
            _timer.Stop();
            return;
        }
        countdown.Text = $"Works once · expires in {left.Minutes}:{left.Seconds:00}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
