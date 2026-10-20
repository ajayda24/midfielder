using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Midfielder.Core;
using Midfielder.Core.Clip;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Core.Transfer;
using Midfielder.Protocol;

namespace Midfielder.App;

/// <summary>
/// The whole Windows app: a tray icon and no windows until you ask for one. Clipboard changes arrive as
/// window messages, network traffic as async I/O completions; between events the process is idle and runs
/// under EcoQoS (efficiency mode).
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int ProgressUpdateMs = 500;

    private readonly SynchronizationContext _ui;
    private readonly Control _uiInvoker = new();
    private readonly AppStorage _storage = new();
    private readonly Settings _settings;
    private readonly DeviceIdentity _identity;
    private readonly PersistentTrustStore _trust;
    private readonly MidfielderNode _node;
    private readonly ClipSync _clipSync;
    private readonly ClipboardMonitor _monitor = new();
    private volatile ClipboardWriter _writer = new();
    private readonly System.Windows.Forms.Timer _clipDebounce = new() { Interval = 60 };
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly LanDiscovery _discovery = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, bool> _clipboardTransfers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Fingerprint, string> _peerHosts = new();
    private readonly List<string> _pendingSends = [];
    private readonly int _port;
    private PairingForm? _pairingForm;
    private readonly object _applyGate = new();
    private Task _applyChain = Task.CompletedTask;
    private int _activeTransfers;
    private long _lastProgressUiMs;

    public TrayContext(string[] args)
    {
        // A control created (handle and all) on this thread: BeginInvoke on it always runs on the UI thread.
        _uiInvoker.CreateControl();
        _ = _uiInvoker.Handle;
        _ui = new InvokerContext(_uiInvoker);
        AppLog.Info($"Midfielder starting ({Environment.OSVersion})");
        _settings = _storage.LoadSettings();
        _identity = _storage.LoadOrCreateIdentity();
        _trust = new PersistentTrustStore(_storage);
        _node = new MidfielderNode(_identity, Environment.MachineName, DeviceKind.Windows, _trust, new Events(this));
        _clipSync = new ClipSync(_identity.DeviceId, BroadcastAsync, ApplyRemoteValueTaskAsync) { Enabled = _settings.SyncClipboard };

        try
        {
            _port = _node.StartListening(_settings.Port);
        }
        catch (SocketException)
        {
            _port = _node.StartListening(0); // preferred port busy; mDNS tells the phone where we are
        }
        AppLog.Info($"Listening on port {_port}; device id {_identity.DeviceId}; {_trust.All().Count} paired device(s)");
        try
        {
            _discovery.Advertise(_identity.DeviceId, Environment.MachineName, _port);
        }
        catch (Exception e)
        {
            AppLog.Error("mDNS advertising failed (phone will use its last known address)", e);
        }

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Midfielder",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowPairingIfNoDevices();
        _node.ConnectedPeersChanged += () => _ui.Post(_ => UpdateStatus(), null);

        _monitor.Changed += (_, _) =>
        {
            _clipDebounce.Stop();
            _clipDebounce.Start(); // apps often update the clipboard several times in a row; read once
        };
        _clipDebounce.Tick += (_, _) =>
        {
            _clipDebounce.Stop();
            OnLocalClipboardChanged();
        };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _ = SendToPipe.ServeAsync(lines => _ui.Post(_ => OnPipeLines(lines), null), _cts.Token);

        if (_settings.StartWithWindows) TryRun(() => Shell.SetAutostart(true));
        TryRun(Shell.EnsureSendToShortcut);
        if (args.Contains("--send")) _pendingSends.AddRange(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)));

        UpdateStatus();
        NativeMethods.SetEfficiencyMode(true);
        if (!args.Contains("--background")) ShowPairingIfNoDevices();
    }

    // ---------------------------------------------------------------------------------------------
    // Menu & status
    // ---------------------------------------------------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var sync = new ToolStripMenuItem("Sync clipboard") { Checked = _settings.SyncClipboard, CheckOnClick = true };
        sync.CheckedChanged += (_, _) =>
        {
            _settings.SyncClipboard = sync.Checked;
            _clipSync.Enabled = sync.Checked;
            _storage.SaveSettings(_settings);
        };
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = autostart.Checked;
            TryRun(() => Shell.SetAutostart(autostart.Checked));
            _storage.SaveSettings(_settings);
        };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Send files to phone…", null, (_, _) => PickAndSendFiles());
        menu.Items.Add("Open received files", null, (_, _) => Shell.OpenFolder(_settings.ReceiveFolder));
        menu.Items.Add("Open log", null, (_, _) => Shell.OpenFile(AppLog.Path));
        menu.Items.Add(sync);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pair a phone…", null, (_, _) => ShowPairing());
        menu.Items.Add("Forget paired devices", null, (_, _) => ForgetDevices());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void UpdateStatus()
    {
        var peers = _node.ConnectedPeers;
        string status = peers.Count > 0
            ? "Connected: " + string.Join(", ", peers.Select(p => p.Name))
            : _trust.All().Count > 0 ? "Waiting for your phone…" : "Not paired yet";
        _statusItem.Text = status;
        SetTrayText(status);
    }

    private void SetTrayText(string text) => _tray.Text = text.Length > 127 ? text[..127] : text;

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) => _tray.ShowBalloonTip(3_000, title, text, icon);

    // ---------------------------------------------------------------------------------------------
    // Pairing
    // ---------------------------------------------------------------------------------------------

    private void ShowPairingIfNoDevices()
    {
        if (_trust.All().Count == 0) ShowPairing();
    }

    private void ShowPairing()
    {
        if (_pairingForm is { IsDisposed: false })
        {
            _pairingForm.Activate();
            return;
        }
        var invite = _node.OpenPairing(LanDiscovery.LocalAddresses(_port));
        _pairingForm = new PairingForm(invite);
        _pairingForm.FormClosed += (_, _) =>
        {
            _node.ClosePairing();
            _pairingForm?.Dispose();
            _uiInvoker.Dispose();
            _pairingForm = null;
        };
        _pairingForm.Show();
    }

    private void OnPaired(PairedDevice device)
    {
        _pairingForm?.Close();
        Notify("Paired", $"{device.Name} is now connected to this PC.");
        UpdateStatus();
    }

    private void ForgetDevices()
    {
        if (MessageBox.Show("Forget all paired devices? They will need to scan a new code.", "Midfielder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var d in _trust.All()) _node.Unpair(d.Fingerprint);
        UpdateStatus();
    }

    // ---------------------------------------------------------------------------------------------
    // Clipboard
    // ---------------------------------------------------------------------------------------------

    private void OnLocalClipboardChanged()
    {
        if (!_clipSync.Enabled) return;
        ClipContent? content;
        try
        {
            content = WindowsClipboard.Read();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException or OutOfMemoryException or IOException)
        {
            AppLog.Error("Could not read the local clipboard", e);
            return; // unreadable or oversized content: skip it rather than crash
        }
        if (content is null) return;

        if (!ClipSync.IsInlineSize(content))
        {
            // Big screenshots go as a clipboard-purpose transfer instead of one huge frame.
            AppLog.Info($"Local copy: {Describe(content)} (sending as a transfer)");
            if (content is ImageClip big) SendClipboardImage(big);
            return;
        }
        var result = _clipSync.OnLocalChange(content);
        AppLog.Info($"Local copy: {Describe(content)} -> {result}");
        if (result == ClipResult.Sent && _node.ConnectedPeers.Count == 0) KnockPhones();
    }

    private ValueTask BroadcastAsync(ClipboardItem item)
    {
        int peers = _node.BroadcastClipboard(item);
        AppLog.Info($"Clipboard sent to {peers} device(s)");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Puts a received clip on the Windows clipboard via the dedicated writer thread. Never waits more than 5 s:
    /// a write that hangs (another program holding the clipboard) is logged, and the writer thread is replaced
    /// so later clips aren't stuck behind it.
    /// </summary>
    private async Task ApplyRemoteAsync(ClipContent content, ClipboardItem item)
    {
        _monitor.SuppressFor(TimeSpan.FromMilliseconds(1500));
        var write = _writer.WriteAsync(content);
        try
        {
            long ms = await write.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _monitor.IgnoreCurrentSequence();
            AppLog.Info($"Applied to Windows clipboard: {Describe(content)} ({ms} ms)");
        }
        catch (TimeoutException)
        {
            AppLog.Error("Writing the Windows clipboard has not finished after 5 s; another program may be holding it. Starting a fresh writer.");
            var stuck = _writer;
            _writer = new ClipboardWriter();
            _ = write.ContinueWith(t =>
            {
                AppLog.Info(t.IsFaulted ? $"Late clipboard write failed: {t.Exception?.GetBaseException().Message}" : "Late clipboard write finished");
                stuck.Dispose();
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            AppLog.Error("Writing the Windows clipboard failed", e);
        }
    }

    private ValueTask ApplyRemoteValueTaskAsync(ClipContent content, ClipboardItem item) => new(ApplyRemoteAsync(content, item));

    private void SendClipboardImage(ImageClip image)
    {
        foreach (var peer in _node.ConnectedPeers)
        {
            _ = RunTransferAsync(() => _node.SendFilesAsync(peer.Fingerprint, [new BytesFileSource("clipboard.png", image.Bytes.ToArray(), "image/png")], TransferPurpose.Clipboard));
        }
    }

    private void KnockPhones()
    {
        foreach (var device in _trust.All())
        {
            if (_peerHosts.TryGetValue(device.Fingerprint, out string? host)) _discovery.Knock(host, _identity.DeviceId, _port);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------------------------------

    private void PickAndSendFiles()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog { Multiselect = true, Title = "Send to phone" };
        if (dialog.ShowDialog() == DialogResult.OK) SendFiles(dialog.FileNames);
    }

    private void OnPipeLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 1 && lines[0] == "--show")
        {
            ShowPairingIfNoDevices();
            return;
        }
        SendFiles(lines.Where(File.Exists).ToList());
    }

    private void SendFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var connected = _node.ConnectedPeers;
        var peer = connected.Count > 0 ? connected[0] : null;
        if (peer is null)
        {
            _pendingSends.AddRange(paths);
            KnockPhones();
            Notify("Phone not connected", "The files will be sent as soon as your phone connects.");
            return;
        }
        List<IFileSource> sources;
        try
        {
            sources = paths.Take(Limits.MaxFilesPerOffer).Select(p => (IFileSource)new PathFileSource(p)).ToList();
        }
        catch (IOException e)
        {
            Notify("Can't send", e.Message, ToolTipIcon.Warning);
            return;
        }
        _ = RunTransferAsync(async () =>
        {
            var result = await _node.SendFilesAsync(peer.Fingerprint, sources).ConfigureAwait(false);
            _ui.Post(_ => Notify(result is TransferResult.Completed ? "Sent" : "Not sent",
                result is TransferResult.Completed ? $"{sources.Count} file(s) sent to {peer.Name}" : Describe(result)), null);
            return result;
        });
    }

    private async Task<IReadOnlyList<IFileSink>?> DecideOfferAsync(PairedDevice from, TransferOffer offer)
    {
        if (offer.Purpose == TransferPurpose.Clipboard)
        {
            if (offer.Files.Count != 1 || offer.Files[0].Size > 64 * 1024 * 1024) return null;
            _clipboardTransfers[offer.TransferId] = true;
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Midfielder", "clipboard")).FullName;
            return [new DirectoryFileSink(dir, offer.TransferId, "clipboard.png")];
        }
        ulong total = offer.Files.Aggregate(0UL, (sum, f) => sum + f.Size);
        Directory.CreateDirectory(_settings.ReceiveFolder);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_settings.ReceiveFolder))!);
        if ((ulong)Math.Max(0, drive.AvailableFreeSpace) < total + (256UL * 1024 * 1024))
        {
            _ui.Post(_ => Notify("Not enough space", $"{from.Name} tried to send {offer.Files.Count} file(s).", ToolTipIcon.Warning), null);
            return null;
        }
        if (!_settings.AutoAcceptFiles)
        {
            bool ok = await OnUiAsync(() => MessageBox.Show($"{from.Name} wants to send {offer.Files.Count} file(s). Accept?", "Midfielder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes).ConfigureAwait(false);
            if (!ok) return null;
        }
        BeginTransfer();
        return offer.Files.Select((f, i) => (IFileSink)new DirectoryFileSink(_settings.ReceiveFolder, $"{offer.TransferId}-{i}", f.Name)).ToList();
    }

    private void OnTransferFinished(string transferId, PairedDevice from, TransferResult result)
    {
        AppLog.Info($"Transfer {transferId[..8]} from {from.Name}: {(result is TransferResult.Completed c ? $"{c.Locations.Count} file(s) received" : Describe(result))}");
        if (_clipboardTransfers.TryRemove(transferId, out _))
        {
            if (result is TransferResult.Completed { Locations.Count: 1 } done)
            {
                string path = done.Locations[0];
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ApplyRemoteAsync(new ImageClip(await File.ReadAllBytesAsync(path).ConfigureAwait(false)), new ClipboardItem()).ConfigureAwait(false);
                    }
                    catch (IOException e)
                    {
                        AppLog.Error("Reading a received clipboard image failed", e);
                    }
                    finally
                    {
                        TryRun(() => File.Delete(path));
                    }
                });
            }
            return;
        }
        EndTransfer();
        _ui.Post(_ =>
        {
            SetTrayText("Midfielder");
            UpdateStatus();
            if (result is TransferResult.Completed done)
            {
                Notify("Received", $"{done.Locations.Count} file(s) from {from.Name} in {_settings.ReceiveFolder}");
            }
            else
            {
                Notify("Transfer failed", Describe(result), ToolTipIcon.Warning);
            }
        }, null);
    }

    private void OnProgress(long transferred, long total)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgressUiMs) < ProgressUpdateMs && transferred < total) return;
        Interlocked.Exchange(ref _lastProgressUiMs, now);
        int pct = total == 0 ? 100 : (int)(transferred * 100 / total);
        _ui.Post(_ => SetTrayText($"Midfielder – transferring {pct}%"), null);
    }

    private async Task RunTransferAsync(Func<Task<TransferResult>> transfer)
    {
        BeginTransfer();
        try
        {
            await transfer().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _ui.Post(_ => Notify("Transfer failed", e.Message, ToolTipIcon.Warning), null);
        }
        finally
        {
            EndTransfer();
            _ui.Post(_ => UpdateStatus(), null);
        }
    }

    /// <summary>Full speed while bytes are moving; back to efficiency mode when idle.</summary>
    private void BeginTransfer()
    {
        if (Interlocked.Increment(ref _activeTransfers) == 1) NativeMethods.SetEfficiencyMode(false);
    }

    private void EndTransfer()
    {
        if (Interlocked.Decrement(ref _activeTransfers) <= 0)
        {
            Interlocked.Exchange(ref _activeTransfers, 0);
            NativeMethods.SetEfficiencyMode(true);
        }
    }

    private static string Describe(ClipContent c) => c switch
    {
        TextClip t => $"text, {t.Text.Length} chars{(c.Sensitive ? ", sensitive" : "")}",
        ImageClip i => $"image, {i.Bytes.Length / 1024} KB",
        _ => c.GetType().Name,
    };

    private static string Describe(TransferResult r) => r switch
    {
        TransferResult.Failed f => f.Reason,
        TransferResult.Cancelled => "Cancelled",
        _ => "Done",
    };

    // ---------------------------------------------------------------------------------------------
    // Connections & power
    // ---------------------------------------------------------------------------------------------

    private void OnPeerConnected(PeerSession session)
    {
        AppLog.Info($"Connected: {session.Peer.Name} from {session.Remote}");
        _peerHosts[session.Peer.Fingerprint] = session.Remote.Host;
        if (_clipSync.CatchUpItem() is { } latest) session.TrySend(new Envelope { Clipboard = latest });
        _ui.Post(_ =>
        {
            UpdateStatus();
            if (_pendingSends.Count > 0)
            {
                var paths = _pendingSends.ToList();
                _pendingSends.Clear();
                SendFiles(paths);
            }
        }, null);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _discovery.Reannounce();
        foreach (var peer in _node.ConnectedPeers) _node.Session(peer.Fingerprint)?.Probe();
        KnockPhones();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _discovery.Reannounce();

    // ---------------------------------------------------------------------------------------------

    private Task OnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return true;
    });

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }, null);
        return tcs.Task;
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _cts.Cancel();
            _clipDebounce.Dispose();
            _monitor.Dispose();
            _writer.Dispose();
            _clipSync.Dispose();
            _discovery.Dispose();
            _node.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _identity.Dispose();
            _pairingForm?.Dispose();
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Posts to the UI thread through a control that is guaranteed to have a window handle.</summary>
    private sealed class InvokerContext(Control control) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!control.IsDisposed) control.BeginInvoke(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!control.IsDisposed) control.Invoke(d, state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>Node callbacks (thread-pool threads) forwarded to the context.</summary>
    private sealed class Events(TrayContext app) : INodeEvents
    {
        public void OnPaired(PairedDevice device) => app._ui.Post(_ => app.OnPaired(device), null);

        public void OnPeerConnected(PeerSession session) => app.OnPeerConnected(session);

        public void OnPeerDisconnected(PairedDevice device, Exception? cause)
        {
            AppLog.Info($"Disconnected: {device.Name} ({cause?.Message ?? "closed"})");
            app._ui.Post(_ => app.UpdateStatus(), null);
        }

        /// <summary>
        /// Applying needs the UI thread. Run it on its own so the connection keeps reading even if the clipboard
        /// is busy; applies stay in arrival order.
        /// </summary>
        public ValueTask OnClipboardAsync(PairedDevice from, ClipboardItem item)
        {
            AppLog.Info($"Clipboard received from {from.Name}: {item.Kind}, {item.Size} bytes{(item.CatchUp ? ", catch-up" : "")}");
            lock (app._applyGate)
            {
                app._applyChain = app._applyChain.ContinueWith(async _ =>
                {
                    try
                    {
                        var result = await app._clipSync.OnRemoteAsync(item).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        AppLog.Info($"Clipboard from {from.Name} -> {result}");
                    }
                    catch (Exception e)
                    {
                        AppLog.Error("Handling a received clipboard item failed", e);
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IReadOnlyList<IFileSink>?> OnTransferOfferAsync(PairedDevice from, TransferOffer offer, CancellationToken ct) =>
            await app.DecideOfferAsync(from, offer).ConfigureAwait(false);

        public void OnTransferProgress(string transferId, long transferred, long total)
        {
            if (!app._clipboardTransfers.ContainsKey(transferId)) app.OnProgress(transferred, total);
        }

        public void OnTransferFinished(string transferId, PairedDevice from, TransferResult result) => app.OnTransferFinished(transferId, from, result);
    }
}
