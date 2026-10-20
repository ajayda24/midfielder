using Midfielder.App.Ui;
using Midfielder.Core.Crypto;
using QRCoder;

namespace Midfielder.App;

/// <summary>Shows the pairing QR code. Created on demand and fully disposed on close (no idle UI in memory).</summary>
internal sealed class PairingForm : Form
{
    private readonly Bitmap _qr;

    public PairingForm(PairingInvite invite)
    {
        Text = "Pair your phone";
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Surface;
        ClientSize = new Size(440, 600);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(invite.ToUri(), QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        byte[] pngBytes = png.GetGraphic(8, [0x17, 0x4F, 0x42, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF]);
        using (var ms = new MemoryStream(pngBytes))
        using (var decoded = Image.FromStream(ms))
        {
            _qr = new Bitmap(decoded); // independent of the stream
        }

        var title = new Label
        {
            Text = "Scan with Midfielder on your phone",
            Font = Theme.Title(15f),
            ForeColor = Theme.Text,
            Dock = DockStyle.Top,
            Height = 56,
            TextAlign = ContentAlignment.BottomCenter,
        };
        var picture = new PictureBox
        {
            Image = _qr,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Top,
            Height = 360,
            Padding = new Padding(24),
        };
        var help = new Label
        {
            Text = "On the phone: Midfielder → Pair with your PC, then point the camera here.\n\n" +
                   $"Check that the phone shows the same fingerprint:\n{invite.Fingerprint.Display()}\n\n" +
                   "The code works once and expires in 5 minutes.",
            Font = Theme.Body(9.5f),
            ForeColor = Theme.Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            Padding = new Padding(20, 8, 20, 12),
        };
        Controls.Add(help);
        Controls.Add(picture);
        Controls.Add(title);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _qr.Dispose();
        base.Dispose(disposing);
    }
}
