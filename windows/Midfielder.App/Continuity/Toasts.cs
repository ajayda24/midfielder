using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Midfielder.Protocol;
using Windows.Foundation.Collections;

namespace Midfielder.App.Continuity;

/// <summary>
/// Windows notifications (Action Center toasts). Phone notifications get a reply box and their buttons; activations
/// (Send, a button, a click) come back through <see cref="Activated"/> on a background thread.
/// </summary>
internal sealed class Toasts : IDisposable
{
    private const string PhoneGroup = "phone";
    private readonly string _iconDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midfielder", "icons");
    private bool _subscribed;

    /// <summary>Arguments and text-box input of a toast the user acted on.</summary>
    public event Action<ToastArguments, ValueSet>? Activated;

    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            Activated?.Invoke(ToastArguments.Parse(e.Argument), e.UserInput);
        }
        catch (Exception ex)
        {
            AppLog.Error("Handling a notification action failed", ex);
        }
    }

    /// <summary>Shows (or updates, same key) a notification from the phone.</summary>
    public void ShowPhoneNotification(NotificationPosted n, string phoneName, bool actions)
    {
        EnsureSubscribed();
        var b = new ToastContentBuilder()
            .AddText(n.Title.Length > 0 ? n.Title : n.AppName, hintMaxLines: 1)
            .AddAttributionText(n.AppName.Length > 0 ? $"{n.AppName} · {phoneName}" : phoneName);
        if (n.Text.Length > 0) b.AddText(n.Text);
        if (IconFile(n) is { } icon) b.AddAppLogoOverride(new Uri(icon), ToastGenericAppLogoCrop.Circle);
        int buttons = 0;
        if (actions && n.CanReply)
        {
            b.AddInputTextBox("reply", placeHolderContent: "Reply");
            b.AddButton(new ToastButton().SetContent("Send").AddArgument("action", "reply").AddArgument("key", n.Key).SetTextBoxId("reply"));
            buttons++;
        }
        if (actions)
        {
            foreach (var button in n.Buttons.Take(3))
            {
                b.AddButton(new ToastButton().SetContent(button.Title).AddArgument("action", "button").AddArgument("key", n.Key).AddArgument("id", button.Id));
                buttons++;
            }
        }
        if (buttons < 5) b.AddButton(new ToastButton().SetContent("Dismiss").AddArgument("action", "dismiss").AddArgument("key", n.Key));
        b.Show(t =>
        {
            t.Tag = Tag(n.Key);
            t.Group = PhoneGroup;
        });
    }

    public static void RemovePhoneNotification(string key)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(Tag(key), PhoneGroup);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Already gone, or Action Center unavailable.
        }
    }

    /// <summary>A plain notification (battery alerts and the like).</summary>
    public void Show(string title, string text, string? tag = null)
    {
        EnsureSubscribed();
        new ToastContentBuilder().AddText(title).AddText(text).Show(t =>
        {
            if (tag is not null) t.Tag = tag;
        });
    }

    /// <summary>Toast tags are limited to 64 characters; phone keys can be longer.</summary>
    private static string Tag(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private string? IconFile(NotificationPosted n)
    {
        if (n.IconPng.IsEmpty || n.AppPackage.Length == 0) return null;
        try
        {
            // One file per app; package names are [a-zA-Z0-9._].
            string safe = new(n.AppPackage.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_').Take(100).ToArray());
            string path = Path.Combine(_iconDir, safe + ".png");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(_iconDir);
                File.WriteAllBytes(path, n.IconPng.ToByteArray());
            }
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_subscribed) ToastNotificationManagerCompat.OnActivated -= OnActivated;
        _subscribed = false;
    }
}
