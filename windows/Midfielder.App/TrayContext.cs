using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Midfielder.Core;
using Midfielder.Core.Clip;
using Midfielder.Core.Crypto;
using Midfielder.Core.Net;
using Midfielder.Core.Transfer;
using Midfielder.App.Continuity;
using Midfielder.App.Ui;
using Midfielder.Protocol;
using System.Drawing;
using System.Windows.Forms;

namespace Midfielder.App;

/// <summary>
/// The whole Windows app: a tray icon and no windows until you ask for one. Clipboard changes arrive as
/// window messages, network traffic as async I/O completions; between events the process is idle and runs
/// under EcoQoS (efficiency mode). The main window (<see cref="MainWindow"/>, WPF) exists only while it is open.
/// </summary>
internal sealed class TrayContext : ApplicationContext, IAppHost
{
    private const int ProgressUpdateMs = 500;

    private readonly SynchronizationContext _ui;
    private readonly Control _uiInvoker = new();
    private readonly AppStorage _storage = new();
    private readonly Settings _settings;
    private readonly DeviceIdentity _identity;
    private readonly PersistentTrustStore _trust;
    private readonly MidfielderNode _node;
    private readonly PcFeatures _features;
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
    private PairingWindow? _pairingForm;
    private MainWindow? _mainForm;
    private readonly HistoryStore _history;
    private readonly TransferTracker _transfers = new();
    private readonly ConcurrentDictionary<string, (TransferOffer Offer, string From)> _incomingOffers = new(StringComparer.Ordinal);
    private readonly List<string> _sendBatch = [];
    private readonly System.Windows.Forms.Timer _sendBatchTimer = new() { Interval = 400 };
    private Action? _balloonAction;
    private readonly object _applyGate = new();
    private Task _applyChain = Task.CompletedTask;
    private readonly Stack<IDisposable> _transferHolds = new();
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
        _features = new PcFeatures(_node, () => _settings.Features, _ui, page => ShowMain(page));
        _features.Changed += () => PhoneChanged?.Invoke();
        _clipSync = new ClipSync(_identity.DeviceId, BroadcastAsync, ApplyRemoteValueTaskAsync) { Enabled = _settings.SyncClipboard };
        _history = new HistoryStore(Path.Combine(_storage.Root, "history.json"));

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
            Icon = AppIcon.Tray(SystemInformation.SmallIconSize),
            Text = "Midfielder",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            (action ?? (() => ShowMain()))();
        };
        _node.ConnectedPeersChanged += () => _ui.Post(_ => UpdateStatus(), null);
        _sendBatchTimer.Tick += (_, _) =>
        {
            _sendBatchTimer.Stop();
            var batch = _sendBatch.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _sendBatch.Clear();
            SendFiles(batch);
        };

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
        // Re-registered on each start so the entry follows the exe if it was moved.
        TryRun(() => Shell.SetContextMenu(_settings.ContextMenu && ModernMenu.Status != ModernMenuStatus.On));
        _ = ModernMenu.RepairIfMovedAsync();
        _pendingSends.AddRange(SendArgs.Expand(SendArgs.Paths(args)));

        TryRun(_features.Apply);
        UpdateStatus();
        Performance.Start();
        AppLog.Info("Tray ready");
        // Started by Windows to deliver a notification action: stay in the tray.
        if (!args.Contains("--background") && !args.Contains("-ToastActivated") && !SendArgs.IsSend(args))
        {
            ShowMain();
            ShowPairingIfNoDevices();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Menu & status
    // ---------------------------------------------------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new MenuRenderer() };
        var sync = new ToolStripMenuItem("Sync clipboard") { Checked = _settings.SyncClipboard, CheckOnClick = true };
        sync.CheckedChanged += (_, _) => SetSyncClipboard(sync.Checked);
        menu.Opening += (_, _) =>
        {
            if (sync.Checked != _settings.SyncClipboard) sync.Checked = _settings.SyncClipboard;
        };
        var open = new ToolStripMenuItem("Open Midfielder", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(open);
        menu.Items.Add("Send files to phone…", null, (_, _) => PickAndSendFiles());
        menu.Items.Add("Received files", null, (_, _) => ShowMain(MainWindow.FilesName));
        menu.Items.Add(sync);
        var ring = new ToolStripMenuItem("Ring my phone", null, (_, _) => RingPhone());
        menu.Items.Add(ring);
        menu.Opening += (_, _) => ring.Visible = _settings.Features.Ring && _features.Phone.CanRing;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    /// <summary>Opens (or brings to front) the main window. Closing it disposes it completely.</summary>
    private void ShowMain(string? page = null)
    {
        if (_mainForm is not null)
        {
            if (page is not null) _mainForm.ShowPage(page);
            if (_mainForm.WindowState == System.Windows.WindowState.Minimized) _mainForm.WindowState = System.Windows.WindowState.Normal;
            _mainForm.Activate();
            return;
        }
        AppLog.Info("Creating main window");
        try
        {
            _mainForm = new MainWindow(this, page ?? MainWindow.HomeName);
            _mainForm.Closed += (_, _) => _mainForm = null;
            _mainForm.Show();
            _mainForm.Activate();
            AppLog.Info("Main window shown");
        }
        catch (Exception e)
        {
            // The tray (clipboard sync, transfers) keeps running even if the window can't be built.
            AppLog.Error("The main window couldn't open", e);
            CloseQuietly(_mainForm);
            _mainForm = null;
            Notify("Midfielder's window couldn't open", "Clipboard sync still works. Details are in the log: " + AppLog.Path, ToolTipIcon.Error);
        }
    }

    private static void CloseQuietly(System.Windows.Window? w)
    {
        try
        {
            w?.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateStatus()
    {
        var peers = _node.ConnectedPeers;
        string status = peers.Count > 0
            ? "Connected: " + string.Join(", ", peers.Select(p => p.Name))
            : _trust.All().Count > 0 ? "Waiting for your phone…" : "Not paired yet";
        _statusItem.Text = status;
        SetTrayText(status);
        StatusChanged?.Invoke();
    }

    private void SetTrayText(string text) => _tray.Text = text.Length > 127 ? text[..127] : text;

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, Action? onClick = null)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(3_000, title, text, icon);
    }

    // ---------------------------------------------------------------------------------------------
    // Pairing
    // ---------------------------------------------------------------------------------------------

    private void ShowPairingIfNoDevices()
    {
        if (_trust.All().Count == 0) ShowPairing();
    }

    private void ShowPairing()
    {
        if (_pairingForm is not null)
        {
            _pairingForm.Activate();
            return;
        }
        AppLog.Info("Opening pairing window");
        try
        {
            var invite = _node.OpenPairing(LanDiscovery.LocalAddresses(_port));
            _pairingForm = new PairingWindow(invite);
            _pairingForm.Closed += (_, _) =>
            {
                _node.ClosePairing();
                _pairingForm = null;
            };
            _pairingForm.Show();
            _pairingForm.Activate();
        }
        catch (Exception e)
        {
            AppLog.Error("The pairing window couldn't open", e);
            CloseQuietly(_pairingForm);
            _pairingForm = null;
            _node.ClosePairing();
            Notify("Pairing window couldn't open", "Details are in the log: " + AppLog.Path, ToolTipIcon.Error);
        }
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
        if (peers > 0) _history.RecordClip(TransferDirection.Sent, DateTime.Now);
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
            ShowMain();
            ShowPairingIfNoDevices();
            return;
        }
        // Explorer's right-click starts one process per selected file: gather them into a single send.
        _sendBatch.AddRange(SendArgs.Expand(lines));
        _sendBatchTimer.Stop();
        _sendBatchTimer.Start();
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
        if (paths.Count > Limits.MaxFilesPerOffer)
        {
            // One offer carries a bounded number of files; send the rest in further batches.
            SendFiles(paths.Take(Limits.MaxFilesPerOffer).ToList());
            SendFiles(paths.Skip(Limits.MaxFilesPerOffer).ToList());
            return;
        }
        List<IFileSource> sources;
        try
        {
            sources = paths.Select(p => (IFileSource)new PathFileSource(p)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notify("Can't send", e.Message, ToolTipIcon.Warning);
            return;
        }
        string id = MidfielderNode.NewTransferId();
        string title = sources.Count == 1 ? sources[0].Name : $"{sources.Count} files";
        _transfers.Start(id, TransferDirection.Sent, title, peer.Name, sources.Sum(s => s.Size));
        AppLog.Info($"Sending {sources.Count} file(s) to {peer.Name}");
        _ = RunTransferAsync(async () =>
        {
            TransferResult result;
            try
            {
                result = await _node.SendFilesAsync(peer.Fingerprint, sources, TransferPurpose.File, id).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                result = new TransferResult.Failed(e.Message);
            }
            OnSendFinished(id, peer, paths, sources, result);
            return result;
        });
    }

    private void OnSendFinished(string id, PairedDevice peer, IReadOnlyList<string> paths, IReadOnlyList<IFileSource> sources, TransferResult result)
    {
        _transfers.Finish(id);
        var status = result switch
        {
            TransferResult.Completed => TransferStatus.Done,
            TransferResult.Cancelled => TransferStatus.Cancelled,
            _ => TransferStatus.Failed,
        };
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _history.Add(sources.Select((s, i) => new TransferRecord($"{id}-{i}", TransferDirection.Sent, s.Name, MimeOf(s.Name), s.Size, peer.Name, now, status, paths[i])));
        if (status == TransferStatus.Done) _history.RecordFiles(TransferDirection.Sent, sources.Count, sources.Sum(s => s.Size), DateTime.Now);
        AppLog.Info($"Send to {peer.Name}: {status}{(result is TransferResult.Failed f ? $" ({f.Reason})" : "")}");
        _ui.Post(_ =>
        {
            if (status == TransferStatus.Done) Notify("Sent", sources.Count == 1 ? $"{sources[0].Name} sent to {peer.Name}" : $"{sources.Count} files sent to {peer.Name}");
            else if (status == TransferStatus.Failed) Notify("Not sent", Describe(result), ToolTipIcon.Warning, () => ShowMain(MainWindow.FilesName));
        }, null);
    }

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

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
        _incomingOffers[offer.TransferId] = (offer, from.Name);
        string title = offer.Files.Count == 1 ? offer.Files[0].Name : $"{offer.Files.Count} files";
        _transfers.Start(offer.TransferId, TransferDirection.Received, title, from.Name, (long)total);
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
        _transfers.Finish(transferId);
        List<TransferRecord> records = [];
        if (result is TransferResult.Completed completed && _incomingOffers.TryRemove(transferId, out var info))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            records = completed.Locations.Select((path, i) =>
            {
                var f = i < info.Offer.Files.Count ? info.Offer.Files[i] : null;
                return new TransferRecord($"{transferId}-{i}", TransferDirection.Received, Path.GetFileName(path), f?.Mime ?? MimeOf(path),
                    f is null ? 0 : (long)f.Size, from.Name, now, TransferStatus.Done, path);
            }).ToList();
            _history.Add(records);
            _history.RecordFiles(TransferDirection.Received, records.Count, records.Sum(r => r.Size), DateTime.Now);
        }
        else
        {
            _incomingOffers.TryRemove(transferId, out _);
        }
        _ui.Post(_ =>
        {
            SetTrayText("Midfielder");
            UpdateStatus();
            if (result is TransferResult.Completed)
            {
                if (records.Count == 1)
                {
                    string path = records[0].Location;
                    Notify("Received", $"{records[0].Name} from {from.Name}. Click to open.", onClick: () => Shell.OpenFile(path));
                }
                else
                {
                    Notify("Received", $"{records.Count} files from {from.Name}. Click to see them.", onClick: () => ShowMain(MainWindow.FilesName));
                }
            }
            else if (result is TransferResult.Failed)
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
        var hold = Performance.Hold();
        lock (_transferHolds) _transferHolds.Push(hold);
    }

    private void EndTransfer()
    {
        IDisposable? hold;
        lock (_transferHolds) _transferHolds.TryPop(out hold);
        hold?.Dispose();
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
    // IAppHost (the main window)
    // ---------------------------------------------------------------------------------------------

    public event Action? StatusChanged;

    public HistoryStore History => _history;
    public TransferTracker Transfers => _transfers;
    public Settings Settings => _settings;
    public IReadOnlyList<PairedDevice> PairedDevices => _trust.All();
    public IReadOnlyList<PairedDevice> ConnectedDevices => _node.ConnectedPeers;
    public string DeviceId => _identity.DeviceId;
    public string Fingerprint => _identity.Fingerprint.Display();
    public int Port => _port;
    public string StatusText => _statusItem.Text ?? "";

    public string? AddressOf(PairedDevice device) => _peerHosts.TryGetValue(device.Fingerprint, out string? host) ? host : null;

    void IAppHost.PickAndSendFiles() => PickAndSendFiles();

    void IAppHost.SendFiles(IReadOnlyList<string> paths) => SendFiles(paths);

    void IAppHost.ShowPairing() => ShowPairing();

    void IAppHost.ForgetDevices() => ForgetDevices();

    public void CancelTransfer(string id) => _node.CancelTransfer(id);

    /// <summary>"Send clipboard now": resend what's on the clipboard even if it was sent before.</summary>
    public void SendClipboardNow()
    {
        ClipContent? content;
        try
        {
            content = WindowsClipboard.Read();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException or OutOfMemoryException or IOException)
        {
            AppLog.Error("Could not read the local clipboard", e);
            content = null;
        }
        if (content is null)
        {
            Notify("Nothing to send", "The clipboard is empty or holds something Midfielder can't sync.");
            return;
        }
        if (_node.ConnectedPeers.Count == 0)
        {
            KnockPhones();
            Notify("Phone not connected", "Midfielder will send your latest copy when the phone connects.");
        }
        if (!ClipSync.IsInlineSize(content))
        {
            if (content is ImageClip big) SendClipboardImage(big);
            return;
        }
        var result = _clipSync.OnLocalChange(content, force: true);
        AppLog.Info($"Send now: {Describe(content)} -> {result}");
    }

    public void SetSyncClipboard(bool on)
    {
        _settings.SyncClipboard = on;
        _clipSync.Enabled = on;
        _storage.SaveSettings(_settings);
    }

    public void SetAutoAccept(bool on)
    {
        _settings.AutoAcceptFiles = on;
        _storage.SaveSettings(_settings);
    }

    public void SetStartWithWindows(bool on)
    {
        _settings.StartWithWindows = on;
        TryRun(() => Shell.SetAutostart(on));
        _storage.SaveSettings(_settings);
    }

    public void SetContextMenu(bool on)
    {
        _settings.ContextMenu = on;
        TryRun(() => Shell.SetContextMenu(on));
        _storage.SaveSettings(_settings);
    }

    // ---- Optional features -------------------------------------------------------------------------

    public event Action? PhoneChanged;

    PhoneState IAppHost.Phone => _features.Phone;

    public void ApplyFeatures()
    {
        _storage.SaveSettings(_settings);
        TryRun(_features.Apply);
    }

    public bool RingPhone()
    {
        bool sent = _features.RingPhone();
        if (!sent) Notify("Couldn't ring your phone", "Turn on Find my phone on both devices, and make sure the phone is connected.", ToolTipIcon.Warning);
        return sent;
    }

    void IAppHost.SendMediaCommand(MediaAction action) => _features.SendMediaCommand(action);

    void IAppHost.ReplyToNotification(string key, string text) => _features.Reply(key, text);

    void IAppHost.DismissNotification(string key) => _features.Dismiss(key);

    ModernMenuStatus IAppHost.ModernMenu => ModernMenu.Status;

    public async Task<string> SetModernMenuAsync(bool on)
    {
        try
        {
            string message = on ? await ModernMenu.EnableAsync().ConfigureAwait(true) : await ModernMenu.DisableAsync().ConfigureAwait(true);
            // Classic entry: hidden while the modern one is active (it would be listed twice), restored when it isn't.
            TryRun(() => Shell.SetContextMenu(_settings.ContextMenu && ModernMenu.Status != ModernMenuStatus.On));
            AppLog.Info($"Windows 11 menu {(on ? "enable" : "disable")}: {message}");
            return message;
        }
        catch (Exception e)
        {
            AppLog.Error("Changing the Windows 11 menu failed", e);
            return "Couldn't change the Windows 11 menu: " + e.Message;
        }
    }

    public void SaveSettings() => _storage.SaveSettings(_settings);

    public void SetReceiveFolder(string path)
    {
        _settings.ReceiveFolder = path;
        _storage.SaveSettings(_settings);
    }

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
            _features.OnPeerConnected(session);
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
            _mainForm?.Close();
            _features.Dispose();
            _sendBatchTimer.Dispose();
            _history.Dispose();
            _clipDebounce.Dispose();
            _monitor.Dispose();
            _writer.Dispose();
            _clipSync.Dispose();
            _discovery.Dispose();
            _node.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _identity.Dispose();
            _pairingForm?.Close();
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
            app._ui.Post(_ =>
            {
                app._features.OnPeerDisconnected();
                app.UpdateStatus();
            }, null);
        }

        public void OnFeature(PeerSession from, Envelope env) => app._ui.Post(_ => app._features.OnMessage(from, env), null);

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
                        if (result == ClipResult.Applied) app._history.RecordClip(TransferDirection.Received, DateTime.Now);
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
            if (app._clipboardTransfers.ContainsKey(transferId)) return;
            app._transfers.Progress(transferId, transferred, total);
            app.OnProgress(transferred, total);
        }

        public void OnTransferFinished(string transferId, PairedDevice from, TransferResult result) => app.OnTransferFinished(transferId, from, result);
    }
}
