using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Passload.Core;

namespace Passload;

internal sealed class InternetSharing(Grid container) : IAsyncDisposable
{
    private readonly ShareCatalog _catalog = new();
    private readonly Dictionary<string, TaskCompletionSource> _starting = new();
    private readonly HashSet<string> _offline = new();
    private WebView2? _view;
    private TransferServer? _source;
    private Task<TransferServer>? _reader;
    private Uri? _engine;
    private bool _booted;
    private DateTimeOffset? _idleSince;
    public Share[] Active => _catalog.Active.Where(s => !_starting.ContainsKey(s.Token)).ToArray();
    public bool IsOffline(string token) => _offline.Contains(token);
    public static Uri Viewer { get; } = ReadViewer();

    private static Uri ReadViewer()
    {
        var configured = Environment.GetEnvironmentVariable("PASSLOAD_VIEWER_URL");
        if (Uri.TryCreate(configured, UriKind.Absolute, out var url) && url.UserInfo == "" && url.AbsolutePath.EndsWith('/') &&
            url.Query == "" && url.Fragment == "" && (url.Scheme == "https" || (url.Scheme == "http" && url.Host == "127.0.0.1"))) return url;
        return new Uri("https://mertioz.github.io/passload/recevoir/");
    }

    public static bool IsBrowserLink(string text, out Uri? uri) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) && uri.Scheme == Viewer.Scheme &&
        uri.Authority == Viewer.Authority && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.AbsolutePath.TrimEnd('/') == Viewer.AbsolutePath.TrimEnd('/') &&
        Regex.IsMatch(uri.Fragment, @"^#p2\.[A-Za-z0-9_-]{87}\.[A-Za-z0-9_-]{43}\.\d{10,13}$");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');

    public async Task<Share> CreateAsync(string[] paths, TimeSpan duration, bool allowRelay, CancellationToken stop)
    {
        _idleSince = null;
        var files = await Task.Run(() => SharedFile.Select(paths), stop);
        await EnsureEngineAsync(stop);
        stop.ThrowIfCancellationRequested();
        using var owner = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = owner.ExportParameters(false).Q;
        var room = Base64Url([4, .. point.X!, .. point.Y!]);
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var until = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var link = new Uri(Viewer, $"#p2.{room}.{key}.{until}");
        var share = _catalog.Add(files, duration, ShareScope.Internet, Viewer, "", "Internet", publicLink:link);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _starting[share.Token] = connected;
        try
        {
            Post(new {op="start", token=share.Token, room, owner=Base64Url(owner.ExportPkcs8PrivateKey()), key, until, relay=allowRelay});
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(17), stop);
            return share;
        }
        catch
        {
            Revoke(share.Token);
            throw;
        }
        finally { _starting.Remove(share.Token); }
    }

    public async Task<Uri> ReaderUriAsync(Uri invitation, CancellationToken stop)
    {
        var own = _catalog.Active.FirstOrDefault(s => s.Link.AbsoluteUri == invitation.AbsoluteUri);
        if (own is not null && _source is not null) return new Uri($"http://127.0.0.1:{_source.Port}/s/{own.Token}");
        var start = _reader ??= TransferServer.StartAsync(new ShareCatalog(), 0, [], loopbackOnly:true, cancellation:stop, browserEngine:true);
        try
        {
            var reader = await start;
            return new Uri($"http://127.0.0.1:{reader.Port}/recevoir/{invitation.Fragment}");
        }
        catch { if (ReferenceEquals(start,_reader)) _reader = null; throw; }
    }

    private async Task EnsureEngineAsync(CancellationToken stop)
    {
        if (_booted) return;
        await ReleaseEngineAsync();
        _source = await TransferServer.StartAsync(_catalog, 0, [], loopbackOnly:true, cancellation:stop, browserEngine:true);
        _engine = new Uri($"http://127.0.0.1:{_source.Port}/engine/host");
        var view = new WebView2 {Width=1, Height=1, HorizontalAlignment=System.Windows.HorizontalAlignment.Left, IsHitTestVisible=false, Focusable=false};
        _view = view;
        container.Children.Insert(0, view);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passload", "WebView");
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await view.EnsureCoreWebView2Async(env);
            stop.ThrowIfCancellationRequested();
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = !string.Equals(e.Uri, _engine.AbsoluteUri, StringComparison.Ordinal);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += (_, e) =>
            {
                if (_engine is null || !string.Equals(e.Source, _engine.AbsoluteUri, StringComparison.Ordinal)) return;
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var msg = doc.RootElement;
                var token = msg.GetProperty("token").GetString() ?? "";
                switch (msg.GetProperty("op").GetString())
                {
                    case "boot": started.TrySetResult(); break;
                    case "online": _offline.Remove(token); if (_starting.TryGetValue(token, out var begin)) begin.TrySetResult(); break;
                    case "offline": _offline.Add(token); break;
                    case "failed":
                        if (_starting.TryGetValue(token, out var failure)) failure.TrySetException(new IOException(msg.GetProperty("detail").GetString()));
                        break;
                }
            };
            core.ProcessFailed += (_, e) =>
            {
                if (!ReferenceEquals(_view, view)) return;
                if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
                _booted = false;
                var error = new IOException("Le moteur de transfert s’est arrêté. Recréez le lien.");
                started.TrySetException(error);
                foreach (var waiter in _starting.Values) waiter.TrySetException(error);
                _catalog.EndAll();
            };
            core.Navigate(_engine.AbsoluteUri);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(18), stop);
            _booted = true;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            await ReleaseEngineAsync();
            throw new IOException("Installez Microsoft Edge WebView2 pour créer un lien Internet depuis ce PC.", ex);
        }
        catch { await ReleaseEngineAsync(); throw; }
    }

    private void Post(object message) => _view?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    public void Revoke(string token)
    {
        _catalog.Revoke(token);
        _offline.Remove(token);
        if (_booted) Post(new {op="stop", token});
    }
    public async Task CollectAsync(bool creating)
    {
        _catalog.Collect();
        if (_offline.Count != 0) _offline.IntersectWith(_catalog.Active.Select(s => s.Token));
        if (creating || _view is null || _catalog.Active.Length != 0) { _idleSince = null; return; }
        _idleSince ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _idleSince > TimeSpan.FromSeconds(15)) await ReleaseEngineAsync();
    }
    private async Task ReleaseEngineAsync()
    {
        _booted = false;
        if (_view is { } view)
        {
            _view = null;
            container.Children.Remove(view);
            view.Dispose();
        }
        if (_source is { } source) { _source = null; await source.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        _catalog.EndAll();
        await ReleaseEngineAsync();
        if (_reader is { } start && !start.IsFaulted && !start.IsCanceled)
        {
            try { var reader = await start; await reader.DisposeAsync(); }
            catch (OperationCanceledException) { }
        }
    }
}
