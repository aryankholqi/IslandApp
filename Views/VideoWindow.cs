using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IslandApp.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace IslandApp.Views;

/// <summary>
/// Borderless, non-transparent window holding the YouTube player (WebView2 can't render in a transparent window).
/// The island positions it over its video slot. Hiding it keeps the video playing.
/// </summary>
public sealed class VideoWindow : Window
{
    // Served locally so the YouTube embed gets an https referrer; embeds without one refuse to play (error 153).
    private const string PlayerHost = "https://island.player/";

    // YouTube's own controls (and their logo) are off; this page draws title + close on top and a seek bar below,
    // shown on hover or while paused. Once playing, a transparent shield takes the mouse so YouTube's hover overlays
    // never appear; before that the player stays clickable (e.g. its "sign in to confirm you're not a bot").
    private const string PlayerPage = """
        <!doctype html><html><head><meta charset="utf-8">
        <style>
        html,body{margin:0;width:100%;height:100%;background:#000;overflow:hidden;color:#fff;user-select:none;
          font:13px "Segoe UI Variable Text","Segoe UI",sans-serif}
        #p,#shield{position:absolute;inset:0;width:100%;height:100%}
        #shield{display:none;cursor:pointer}
        .ui{position:absolute;left:0;right:0;opacity:0;pointer-events:none;transition:opacity .2s}
        body:not(.ready) #top,body.ready:hover .ui,body.paused .ui{opacity:1;pointer-events:auto}
        #top{top:0;display:flex;align-items:center;gap:10px;padding:12px 14px 22px 18px;background:linear-gradient(#000b,#0000)}
        #title{flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;unicode-bidi:plaintext}
        #close{flex:none;width:24px;height:24px;border:0;border-radius:12px;background:#fff3;color:#fff;font-size:11px;cursor:pointer}
        #close:hover{background:#fff5}
        #bottom{bottom:0;padding:22px 18px 12px;background:linear-gradient(#0000,#000b)}
        #track{height:4px;border-radius:2px;background:#fff4;cursor:pointer}
        #track:hover{height:6px}
        #fill{height:100%;width:0;border-radius:2px;background:#f00;pointer-events:none}
        #time{margin-top:6px;font-size:11px;opacity:.85;font-variant-numeric:tabular-nums}
        </style></head><body>
        <div id="p"></div><div id="shield"></div>
        <div id="top" class="ui"><div id="title" dir="auto"></div><button id="close" title="Close">&#x2715;</button></div>
        <div id="bottom" class="ui"><div id="track"><div id="fill"></div></div><div id="time"></div></div>
        <script>
        var player, q = new URLSearchParams(location.search), $ = function (id) { return document.getElementById(id); };
        function fmt(s) {
          s = Math.max(0, Math.floor(s)); var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = ('0' + s % 60).slice(-2);
          return h ? h + ':' + ('0' + m).slice(-2) + ':' + x : m + ':' + x;
        }
        function onYouTubeIframeAPIReady() {
          player = new YT.Player('p', { width: '100%', height: '100%', videoId: q.get('v'),
            playerVars: { autoplay: 1, controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3, rel: 0, playsinline: 1,
                          start: Math.floor(+q.get('t') || 0) },
            events: {
              onReady: function (e) {
                var t = parseFloat(q.get('t')) || 0;
                if (t > 0) e.target.seekTo(t, true); // exact to the fraction; playerVars.start is whole seconds
                e.target.playVideo();
              },
              onStateChange: function (e) {
                if (e.data === YT.PlayerState.PLAYING) {
                  document.body.classList.add('ready');
                  $('shield').style.display = 'block';
                  $('title').textContent = player.getVideoData().title || '';
                }
                document.body.classList.toggle('paused', e.data === YT.PlayerState.PAUSED || e.data === YT.PlayerState.ENDED);
              } } });
        }
        $('shield').onclick = function () {
          player.getPlayerState() === YT.PlayerState.PLAYING ? player.pauseVideo() : player.playVideo();
        };
        $('close').onclick = function () { chrome.webview.postMessage('close'); };
        $('track').onclick = function (e) {
          var d = player && player.getDuration();
          if (d) player.seekTo(d * e.offsetX / this.clientWidth, true);
        };
        setInterval(function () {
          if (!player || !player.getDuration) return;
          var d = player.getDuration() || 0, c = player.getCurrentTime() || 0;
          $('fill').style.width = (d ? c / d * 100 : 0) + '%';
          $('time').textContent = fmt(c) + ' / ' + fmt(d);
        }, 500);
        </script><script src="https://www.youtube.com/iframe_api"></script></body></html>
        """;

    /// <summary>
    /// Any other site: runs in every frame of its page. Each frame stretches its largest video to fill the window (the
    /// top frame also considers big iframes, where the player usually lives) and starts it once, from __START__
    /// seconds unless it's live. The top frame adds the close button, shown on hover.
    /// </summary>
    private const string PageScript = """
        (function () {
          try { // Not inside our own YouTube player page.
            if (location.host === 'island.player') return;
            var anc = location.ancestorOrigins || [];
            for (var i = 0; i < anc.length; i++) if (anc[i].indexOf('island.player') >= 0) return;
          } catch (e) {}
          var isTop = window === window.top, start = __START__, started = false, chosen = null;
          function fill(el) {
            if (chosen) chosen.classList.remove('__island_el');
            var s = el.style, rules = { position: 'fixed', top: '0', left: '0', width: '100vw', height: '100vh',
              'max-width': 'none', 'max-height': 'none', margin: '0', padding: '0', border: '0', transform: 'none',
              // cover, not contain: fill the island edge to edge even when the stream isn't exactly 16:9.
              'z-index': '2147483646', background: '#000', 'object-fit': 'cover' };
            for (var k in rules) s.setProperty(k, rules[k], 'important');
            el.classList.add('__island_el');
            // "fixed" only means the whole window when no ancestor has a transform, filter, contain or similar;
            // players often do, which would shrink the video to the player's box.
            for (var p = el.parentElement; p; p = p.parentElement) {
              ['transform', 'filter', 'backdrop-filter', 'perspective', 'contain', 'will-change', 'container-type']
                .forEach(function (k) { p.style.setProperty(k, k === 'container-type' ? 'normal' : 'none', 'important'); });
              p.style.setProperty('zoom', '1', 'important');
            }
            // The site's own headers and menus sit as high as the video, so hide everything else instead.
            if (!document.getElementById('__island_css')) {
              var css = document.createElement('style');
              css.id = '__island_css';
              css.textContent = 'html,body{overflow:hidden!important;background:#000!important}'
                + 'body *{visibility:hidden!important}'
                + '.__island_el,.__island_el *,#__island_close{visibility:visible!important}';
              (document.head || document.documentElement).appendChild(css);
            }
            if (el.tagName === 'VIDEO') el.controls = true; // the site's own controls are hidden with the rest
          }
          function pick() {
            var best = chosen, area = 0, els = document.querySelectorAll(isTop ? 'video, iframe' : 'video');
            for (var i = 0; i < els.length; i++) {
              var r = els[i].getBoundingClientRect(), a = r.width * r.height;
              if (els[i].tagName === 'IFRAME' && a < 40000) continue; // ads and widgets, not players
              if (a > area) { best = els[i]; area = a; }
            }
            return best;
          }
          setInterval(function () {
            var el = pick();
            if (!el) return;
            if (el !== chosen) { fill(el); chosen = el; }
            if (el.tagName === 'VIDEO') {
              window.__islandVideo = el;
              if (!started && el.readyState > 0) {
                started = true;
                if (start > 0 && isFinite(el.duration) && el.duration > start) el.currentTime = start;
                el.play().catch(function () {});
              }
            }
          }, 700);
          if (!isTop) return;
          document.addEventListener('DOMContentLoaded', function () {
            var css = document.createElement('style');
            css.textContent = '#__island_close{position:fixed;top:10px;right:10px;width:26px;height:26px;border:0;border-radius:13px;'
              + 'background:#0009;color:#fff;font:12px sans-serif;cursor:pointer;z-index:2147483647;opacity:0;transition:opacity .2s}'
              + 'html:hover #__island_close{opacity:1}#__island_close:hover{background:#000c}';
            var close = document.createElement('button');
            close.id = '__island_close'; close.title = 'Close'; close.textContent = '✕';
            close.onclick = function () { window.chrome.webview.postMessage('close'); };
            document.head.appendChild(css);
            document.body.appendChild(close);
          });
        })();
        """;

    private string? _pageScriptId;
    private bool _pageMode;

    /// <summary>The page's close button was pressed.</summary>
    public event EventHandler? CloseRequested;

    private readonly WebView2 _webView = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private Task? _ready;

    public VideoWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        Content = _webView;
        Width = 1;
        Height = 1;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyOverlayStyle(new WindowInteropHelper(this).Handle); // never steals focus from the user's work
    }

    protected override void OnClosed(EventArgs e)
    {
        _webView.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Places the window over a screen rectangle (physical pixels), rounded to match the island.</summary>
    public void Place(int x, int y, int width, int height, int radius)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.PlaceWindow(hwnd, x, y, width, height);
        NativeMethods.RoundCorners(hwnd, width, height, radius);
    }

    /// <summary>Plays a YouTube video id through YouTube's player, or a page address with its video filling the window.</summary>
    public async Task PlayAsync(string source, bool isYouTube, TimeSpan start)
    {
        try
        {
            await (_ready ??= InitializeAsync());
            var core = _webView.CoreWebView2;
            var seconds = start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            _pageMode = !isYouTube;
            if (isYouTube)
            {
                core.Navigate($"{PlayerHost}?v={Uri.EscapeDataString(source)}&t={seconds}");
                return;
            }

            // The start time is baked into the script, so swap it for each video.
            if (_pageScriptId is not null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_pageScriptId);
            }

            _pageScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript.Replace("__START__", seconds, StringComparison.Ordinal));
            core.Navigate(source);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _ready = null; // try again next time
            System.Diagnostics.Debug.WriteLine($"Video player unavailable: {ex.Message}");
        }
    }

    /// <summary>Stops playback (and sound) by leaving the player page.</summary>
    public void Stop()
    {
        if (_webView.CoreWebView2 is { } core)
        {
            core.Navigate("about:blank");
        }
    }

    public async Task<TimeSpan?> GetPositionAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            // YouTube's player, else the page's video when it's in the top frame and not live (a live position can't be resumed).
            var json = await _webView.CoreWebView2.ExecuteScriptAsync("""
                (function () {
                  if (typeof player !== 'undefined' && player && player.getCurrentTime) return player.getCurrentTime();
                  var v = window.__islandVideo;
                  return v && isFinite(v.duration) ? v.currentTime : null;
                })()
                """);
            return JsonSerializer.Deserialize<double?>(json) is { } seconds && double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private async Task InitializeAsync()
    {
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder,
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.AddWebResourceRequestedFilter(PlayerHost + "*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            var page = new MemoryStream(Encoding.UTF8.GetBytes(PlayerPage));
            e.Response = environment.CreateWebResourceResponse(page, 200, "OK", "Content-Type: text/html; charset=utf-8");
        };

        core.WebMessageReceived += (_, e) =>
        {
            if (e.WebMessageAsJson == "\"close\"")
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        // Sign-in opens in WebView2's own popup so the login lands in this profile: YouTube asks anonymous viewers
        // (especially on VPN addresses) to "sign in to confirm you're not a bot". YouTube's other links go to the user's
        // browser; other sites' popups are dropped (mostly ads).
        core.NewWindowRequested += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Host.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Handled = true;
            if (!_pageMode)
            {
                OpenInBrowser(e.Uri);
            }
        };
    }

    private static void OpenInBrowser(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http")
        {
            NativeMethods.AllowAnyForeground();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
