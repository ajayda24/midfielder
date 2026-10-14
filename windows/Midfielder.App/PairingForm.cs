using Midfielder.Core.Crypto;
using QRCoder;

namespace Midfielder.App;

/// <summary>Shows the pairing QR code. Created on demand and fully disposed on close (no idle UI in memory).</summary>
internal sealed class PairingForm : Form
{
    public PairingForm(PairingInvite invite)
    {
        Text = "Pair your phone with Midfielder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 520);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(invite.ToUri(), QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        byte[] pngBytes = png.GetGraphic(8);
        using var ms = new MemoryStream(pngBytes);

        var picture = new PictureBox
        {
            Image = Image.FromStream(ms),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Top,
            Height = 380,
        };
        var help = new Label
        {
            Text = "Open Midfielder on your phone and tap \"Pair with PC\", then scan this code.\n\n" +
                   $"Make sure the phone shows the same PC fingerprint:\n{invite.Fingerprint.Display()}\n\n" +
                   "The code works once and expires in 5 minutes.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            Padding = new Padding(12),
        };
        Controls.Add(help);
        Controls.Add(picture);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Control c in Controls)
            {
                if (c is PictureBox p) p.Image?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
