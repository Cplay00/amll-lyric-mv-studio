using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AmllMvStudio.Services;
using AmllMvStudio.Views;

var tests = new (string Name, Func<Task> Run)[]
{
    ("canonical root boundary", TestCanonicalRootBoundaryAsync),
    ("HTTP range and method handling", TestHttpServerAsync),
    ("HTTP Host authority validation", TestHttpHostValidationAsync),
    ("canonical state contract", TestCanonicalStateContractAsync),
    ("DPI and responsive layout metrics", TestLayoutMetricsAsync),
    ("asset reselection contract", TestAssetReselectionAsync),
    ("preview query and patch contract", TestPreviewQueryAndPatchAsync),
    ("preview readiness contract", TestPreviewReadinessAsync),
    ("about page contracts", TestAboutContractsAsync),
    ("required title contract", TestRequiredTitleAsync),
    ("GUI absolute output resolution", TestGuiOutputResolutionAsync),
    ("system font fallback", TestSystemFontFallbackAsync),
    ("encoder availability and presets", TestEncoderContractsAsync),
    ("audio output compatibility", TestAudioOutputCompatibilityAsync),
    ("renderer argument formatting", TestRenderArgumentsAsync),
    ("preview JavaScript syntax", TestPreviewJavaScriptSyntaxAsync),
    ("runtime layout resolution", TestRuntimeLayoutAsync),
    ("Node CLI cross-layer contract", TestNodeCliContractAsync),
};

int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;

static Task TestCanonicalRootBoundaryAsync()
{
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amll-root"));
    Assert(LocalAssetServer.IsPathWithinRoot(root, root), "root itself should be accepted");
    Assert(LocalAssetServer.IsPathWithinRoot(root, Path.Combine(root, "index.html")), "child path should be accepted");
    Assert(!LocalAssetServer.IsPathWithinRoot(root, root + "-evil" + Path.DirectorySeparatorChar + "index.html"),
        "sibling with common prefix must be rejected");
    Assert(LocalAssetServer.TryParseRange("bytes=2-5", 10, out long start, out long end) && start == 2 && end == 5,
        "normal range parsing failed");
    Assert(!LocalAssetServer.TryParseRange("bytes=10-", 10, out _, out _), "out-of-bounds range must fail");
    return Task.CompletedTask;
}

static async Task TestHttpServerAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "amll-gui-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    await File.WriteAllBytesAsync(Path.Combine(root, "clip.mp4"), Enumerable.Range(0, 10).Select(x => (byte)x).ToArray());
    try
    {
        using var server = new LocalAssetServer(root);
        server.Start();
        using var client = new HttpClient();
        using var ranged = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "/clip.mp4");
        ranged.Headers.Range = new RangeHeaderValue(2, 5);
        using HttpResponseMessage response = await client.SendAsync(ranged);
        Assert(response.StatusCode == HttpStatusCode.PartialContent, "valid range should return 206");
        Assert(response.Content.Headers.ContentType?.MediaType == "video/mp4", "mp4 MIME must be video/mp4");
        Assert((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 2, 3, 4, 5 }), "range body mismatch");
        Assert(response.Headers.Contains("X-Content-Type-Options"), "security headers missing");
        Assert(response.Headers.TryGetValues("Cross-Origin-Resource-Policy", out IEnumerable<string>? corp) && corp.Single() == "same-origin",
            "CORP must restrict resources to same origin");
        Assert(!response.Headers.Contains("Access-Control-Allow-Origin"), "wildcard CORS header must not be emitted");

        using var invalid = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "/clip.mp4");
        invalid.Headers.TryAddWithoutValidation("Range", "bytes=20-");
        using HttpResponseMessage invalidResponse = await client.SendAsync(invalid);
        Assert(invalidResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "invalid range should return 416");
        Assert(invalidResponse.Content.Headers.ContentRange?.Length == 10, "416 must report total length");

        using HttpResponseMessage post = await client.PostAsync(server.BaseUrl + "/clip.mp4", new ByteArrayContent([]));
        Assert(post.StatusCode == HttpStatusCode.MethodNotAllowed, "POST should return 405");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task TestHttpHostValidationAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "amll-host-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "local asset");
    try
    {
        using var server = new LocalAssetServer(root);
        server.Start();

        string missingHost = await SendRawHttpRequestAsync(server.Port, "GET / HTTP/1.1\r\nConnection: close\r\n\r\n");
        Assert(missingHost.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal),
            "HTTP/1.1 requests without Host must be rejected before content is served");

        foreach (string request in new[]
        {
            $"GET / HTTP/1.1\r\nHost: evil.example:{server.Port}\r\nConnection: close\r\n\r\n",
            $"GET / HTTP/1.1\r\nHost: localhost:{server.Port}\r\nConnection: close\r\n\r\n",
            $"GET / HTTP/1.1\r\nHost: [::1]:{server.Port}\r\nConnection: close\r\n\r\n",
            $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}:80\r\nConnection: close\r\n\r\n",
            $"GET / HTTP/1.1\r\nHost : 127.0.0.1:{server.Port}\r\nConnection: close\r\n\r\n",
            $"GET / HTTP/1.1\r\nHost: evil.example:{server.Port}\r\nHost: 127.0.0.1:{server.Port}\r\nConnection: close\r\n\r\n",
        })
        {
            string rejected = await SendRawHttpRequestAsync(server.Port, request);
            Assert(rejected.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal),
                $"alternate or malformed Host authority must be rejected: {request}");
            Assert(!rejected.Contains("local asset", StringComparison.Ordinal),
                $"rejected Host authority must not receive local content: {request}");
        }

        string accepted = await SendRawHttpRequestAsync(server.Port,
            $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nConnection: close\r\n\r\n");
        Assert(accepted.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) &&
            accepted.EndsWith("local asset", StringComparison.Ordinal),
            "the exact IPv4 loopback authority published by BaseUrl must be served");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task<string> SendRawHttpRequestAsync(int port, string request)
{
    using var client = new TcpClient(AddressFamily.InterNetwork);
    await client.ConnectAsync(IPAddress.Loopback, port);
    await using NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    await stream.FlushAsync();
    using var reader = new StreamReader(stream, Encoding.ASCII);
    return await reader.ReadToEndAsync();
}

static Task TestCanonicalStateContractAsync()
{
    var state = new AppState();
    Assert(state.SecondaryMode == "both", "SecondaryMode default must be both");
    Assert(state.LyricWidth == 54, "LyricWidth default must be percent 54");
    Assert(state.LyricOffset == 0, "LyricOffset default must be 0 px");
    Assert(state.BackgroundSaturation == 1.2, "BackgroundSaturation default must be ratio 1.2");
    Assert(state.BrowserGpu == "auto", "BrowserGpu default must be auto");
    Assert(state.AudioCodec == "aac", "AudioCodec default must be aac");
    Assert(state.Head == 0 && state.Tail == 1200, "timing defaults must match the renderer contract");

    state.Head = RenderContracts.MaxTimingPaddingMs + 1;
    state.Tail = -1;
    state.SecondaryMode = "translated";
    state.LyricWidth = 101;
    state.LyricOffset = -700;
    state.LyricFade = 0.5;
    state.BackgroundSaturation = double.NaN;
    state.FontScale = 3;
    state.FontWeight = 50;
    state.FlowSpeed = 8;
    state.BrowserGpu = "true";
    state.AudioCodec = "libopus";
    Assert(state.SecondaryMode == "both", "invalid SecondaryMode must normalize to both");
    Assert(state.LyricWidth == 100 && state.LyricOffset == -600, "lyric ranges must clamp to canonical bounds");
    Assert(state.LyricHeight == 100 && state.InfoOffsetX == 0 && state.InfoOffsetY == 0,
        "lyric height and info offsets must clamp to canonical bounds");
    Assert(state.LyricFade == 0.3, "LyricFade must clamp to 0..0.3");
    Assert(state.ShowArtist && state.ShowAlbum, "artist and album toggles must default to on");
    Assert(state.BackgroundSaturation == 1.2, "non-finite saturation must normalize");
    Assert(state.FontScale == 2 && state.FontWeight == 100 && state.FlowSpeed == 5,
        "visible GUI ranges must clamp to the shared contract");
    Assert(state.BrowserGpu == "auto" && state.AudioCodec == "aac", "invalid enum values must not enter state");
    Assert(state.Head == RenderContracts.MaxTimingPaddingMs && state.Tail == 0, "timing fields must clamp to the Node CLI range");
    Assert(state.CustomStageWidth == 1920 && state.CustomStageHeight == 1080,
        "custom stage sizes must default to 1920x1080");
    state.CustomStageWidth = 1000; state.CustomStageHeight = 1081;
    Assert(state.CustomStageWidth == 1000 && state.CustomStageHeight == 1080,
        "custom stage sizes must clamp into range and drop to even pixels");
    state.CustomStageWidth = 9999;
    Assert(state.CustomStageWidth == 1920, "out-of-range custom widths must fall back to the default");
    state.Preset = "custom";
    Assert(state.Preset == "custom", "the custom stage preset must be accepted");
    Assert(StagePresets.EffectiveDimensions("custom", 1000, 1080) == (1000, 1080),
        "custom presets must resolve to the user-entered dimensions");
    Assert(StagePresets.EffectiveDimensions("1080p", 1000, 1080) == (1920, 1080),
        "standard presets must ignore custom dimensions");
    return Task.CompletedTask;
}

static Task TestLayoutMetricsAsync()
{
    foreach ((uint dpi, int expectedWidth, int expectedHeight) in new[]
    {
        (96u, 1440, 940),
        (120u, 1800, 1175),
        (144u, 2160, 1410),
        (168u, 2520, 1645),
        (192u, 2880, 1880),
    })
    {
        (int width, int height) = GuiLayoutMetrics.CalculateInitialWindowSize(
            1440, 940, dpi, 4000, 2400, 80);
        Assert(width == expectedWidth && height == expectedHeight,
            $"DPI {dpi} must convert DIP to physical pixels");
    }

    Assert(GuiLayoutMetrics.CalculateInitialWindowSize(1440, 940, 192, 1500, 1000, 80) == (1420, 920),
        "initial size must clamp to the work area margin");
    Assert(GuiLayoutMetrics.CalculateInitialWindowSize(1440, 940, 0, 4000, 2400, 80) == (1440, 940),
        "zero DPI must safely fall back to 96 DPI");
    Assert(GuiLayoutMetrics.ShouldStackMainLayout(979.9) && !GuiLayoutMetrics.ShouldStackMainLayout(980),
        "main layout breakpoint mismatch");
    Assert(GuiLayoutMetrics.ShouldUseCompactLayout(619.9) && !GuiLayoutMetrics.ShouldUseCompactLayout(620),
        "button breakpoint mismatch");
    Assert(GuiLayoutMetrics.CategoryLayoutBreakpoint == 520,
        "category layout breakpoint must keep normal-width panels in two columns");
    double normalParameterWidth = GuiLayoutMetrics.GetParameterPanelWidth(1440, false);
    Assert(normalParameterWidth > GuiLayoutMetrics.CategoryLayoutBreakpoint,
        "the normal 1440 DIP window must use two category columns");
    Assert(GuiLayoutMetrics.GetParameterPanelWidth(1000, true) == 800,
        "stacked parameter panel must occupy 80 percent");
    Assert(GuiLayoutMetrics.GetParameterPanelWidth(2000, false) == 960,
        "wide parameter panel must respect the 960 DIP maximum");
    Assert(GuiLayoutMetrics.GetParameterPanelWidth(-1, true) == 0,
        "invalid negative layout width must not produce a negative panel width");

    string exportXaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "gui", "Views", "ExportPage.xaml"));
    string exportCode = File.ReadAllText(Path.Combine(FindProjectRoot(), "gui", "Views", "ExportPage.xaml.cs"));
    Assert(exportXaml.Contains("<ScrollViewer x:Name=\"LogScroll\"", StringComparison.Ordinal),
        "LogScroll must remain an internally scrolling region");
    Assert(exportXaml.Contains("x:Name=\"LogScroll\" MaxHeight=\"300\"", StringComparison.Ordinal),
        "LogScroll must start height-bounded so stacked layouts never grow infinitely");
    Assert(!System.Text.RegularExpressions.Regex.IsMatch(exportXaml, "x:Name=\"TaskPane\"[^>]*MaxHeight=\""),
        "TaskPane must stretch to the configuration column height in the wide layout without a static MaxHeight");
    Assert(exportCode.Contains("TaskPane.MaxHeight = stacked ? StackedTaskPaneMaxHeight : double.NaN", StringComparison.Ordinal) &&
        exportCode.Contains("LogScroll.MaxHeight = stacked ? StackedLogMaxHeight : double.NaN", StringComparison.Ordinal) &&
        exportCode.Contains("StackedTaskPaneMaxHeight = 820", StringComparison.Ordinal) &&
        exportCode.Contains("StackedLogMaxHeight = 300", StringComparison.Ordinal),
        "stacked layout must restore bounded task pane and log heights");
    Assert(exportCode.Contains("Math.Max(PageScrollViewer.ViewportHeight, 480)", StringComparison.Ordinal),
        "wide layout must clamp MainLayout height to the scroll viewport so the task pane aligns with the configuration column");
    Assert(!exportXaml.Contains("x:Name=\"ParameterPanel\" HorizontalAlignment=\"Center\"", StringComparison.Ordinal),
        "the parameter panel must fill the configuration column width in the wide layout");
    return Task.CompletedTask;
}

static Task TestAssetReselectionAsync()
{
    var state = new AppState { AudioPath = @"D:\Music\song.flac" };
    int changed = 0;
    int assetsChanged = 0;
    state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.AudioPath)) changed++; };
    state.AssetsChanged += (_, _) => assetsChanged++;
    state.NotifyAssetReselected(@"d:\music\SONG.flac");
    Assert(changed == 1 && assetsChanged == 1, "same-path reselection must explicitly notify preview listeners");
    state.NotifyAssetReselected(@"D:\Music\other.flac");
    Assert(changed == 1 && assetsChanged == 1, "untracked asset path must not notify");
    return Task.CompletedTask;
}

static async Task TestPreviewQueryAndPatchAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "amll-preview-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string audio = Path.Combine(root, "song.wav");
    await File.WriteAllBytesAsync(audio, [0]);
    try
    {
        using var server = new LocalAssetServer(root);
        var state = new AppState
        {
            AudioPath = audio, Title = "标题 & patch", Artist = "", Album = "", Preset = "vertical",
            FontFamily = "Segoe UI, sans-serif", FontScale = 2, FontWeight = 900, FlowSpeed = 5,
        };
        IReadOnlyList<string> query = PreviewScripts.BuildQuery(state, server);
        Assert(query.Contains("preset=vertical"), "preview query must include preset");
        Assert(query.Contains("title=%E6%A0%87%E9%A2%98%20%26%20patch"), "preview query must URL-encode title");
        Assert(query.Any(item => item == "showArtist=true"), "preview query must include showArtist");
        Assert(query.Any(item => item == "showAlbum=true"), "preview query must include showAlbum");
        Assert(query.Any(item => item == "lyricFade=0.08"), "preview query must include default lyricFade");
        Assert(query.Any(item => item == "lyricHeight=100"), "preview query must include the lyric area height");
        Assert(query.Any(item => item == "infoOffsetX=0" || item == "infoOffsetY=0"),
            "preview query must include info-block offsets");
        Assert(query.Any(item => item.StartsWith("width=", StringComparison.Ordinal)) &&
            query.Any(item => item.StartsWith("height=", StringComparison.Ordinal)),
            "preview query must carry explicit stage dimensions");
        Assert(!query.Any(item => item.StartsWith("tail=", StringComparison.Ordinal)), "export-only fields must stay out of preview query");
        string patch = PreviewScripts.ApplyOptions(state);
        Assert(patch.Contains("window.__amllMv", StringComparison.Ordinal) && patch.Contains("setOptions", StringComparison.Ordinal),
            "visual updates must call the setOptions bridge");
        Assert(patch.Contains("\"presetId\":\"vertical\"", StringComparison.Ordinal), "patch must map Preset to presetId");
        Assert(patch.Contains("\"stageWidth\":1080", StringComparison.Ordinal) && patch.Contains("\"stageHeight\":1920", StringComparison.Ordinal),
            "patch must carry the effective stage dimensions");
        Assert(patch.Contains("\"showArtist\":true", StringComparison.Ordinal) && patch.Contains("\"lyricFade\":0.08", StringComparison.Ordinal),
            "patch must carry artist toggle and lyric fade");
        Assert(patch.Contains("\"lyricHeight\":100", StringComparison.Ordinal) && patch.Contains("\"infoOffsetX\":0", StringComparison.Ordinal),
            "patch must carry lyric height and info offsets");
        Assert(!patch.Contains("audioCodec", StringComparison.Ordinal) && !patch.Contains("tail", StringComparison.Ordinal),
            "export-only fields must stay out of live patch");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static Task TestPreviewReadinessAsync()
{
    string payload = "{\"rendered\":true,\"bridge\":true,\"setOptions\":true,\"ready\":false," +
        "\"readiness\":{\"assetsLoaded\":true,\"audioMetaLoaded\":true,\"fontLoaded\":true," +
        "\"stageLayoutReady\":true,\"backgroundReady\":false}}";
    string webViewRaw = JsonSerializer.Serialize(payload);
    Assert(PreviewScripts.DecodeExecuteScriptResult(webViewRaw) == payload,
        "ExecuteScriptAsync JSON string results must be decoded exactly once");
    Assert(PreviewScripts.DecodeExecuteScriptResult("null") is null, "JavaScript null must decode to null");
    using JsonDocument pendingDocument = JsonDocument.Parse(payload);
    Assert(!PreviewReadiness.IsReady(pendingDocument.RootElement),
        "an initialized bridge must not be accepted before the real ready flag");
    Assert(PreviewReadiness.Describe(pendingDocument.RootElement).Contains("backgroundReady", StringComparison.Ordinal),
        "pending readiness must identify backgroundReady without weakening it");

    using JsonDocument readyDocument = JsonDocument.Parse(payload.Replace("\"ready\":false", "\"ready\":true")
        .Replace("\"backgroundReady\":false", "\"backgroundReady\":true"));
    Assert(PreviewReadiness.IsReady(readyDocument.RootElement), "fully ready bridge must be accepted");
    Assert(PreviewReadiness.GetRetryDelay(0) == TimeSpan.FromMilliseconds(100) &&
        PreviewReadiness.GetRetryDelay(4) == TimeSpan.FromMilliseconds(500),
        "startup diagnostics must retry quickly first and then back off");
    string diagnosticScript = PreviewScripts.Diagnose();
    Assert(diagnosticScript.Contains("ready:bridge?.ready===true", StringComparison.Ordinal) &&
        diagnosticScript.Contains("readiness:bridge?.readiness??null", StringComparison.Ordinal),
        "diagnostics must expose the real bridge readiness contract");

    string previewSource = File.ReadAllText(Path.Combine(FindProjectRoot(), "gui", "Views", "PreviewPage.xaml.cs"));
    Assert(previewSource.Contains("TryRecoverPageReadinessAsync(navigationGeneration, navigationId)", StringComparison.Ordinal),
        "polling must retry readiness after the bounded startup diagnostic window");
    Assert(previewSource.Contains("PreviewReadiness.IsReady(readiness)", StringComparison.Ordinal),
        "readiness recovery must preserve the shared readiness contract");
    Assert(previewSource.Contains("if (!IsCurrentNavigation(navigationGeneration, navigationId) || diagnostic is null) return;",
            StringComparison.Ordinal),
        "readiness recovery must reject stale navigation results after awaiting diagnostics");
    Assert(previewSource.Contains("string? error = PreviewReadiness.GetError(readiness);", StringComparison.Ordinal) &&
        previewSource.Contains("string.Equals(error, _reportedReadinessError, StringComparison.Ordinal)", StringComparison.Ordinal),
        "periodic readiness recovery must surface and deduplicate frontend errors");
    Assert(previewSource.Contains("_reportedReadinessError = null;", StringComparison.Ordinal),
        "readiness error deduplication must reset for each navigation");
    Assert(previewSource.Contains("ReadinessRecoveryInterval = TimeSpan.FromSeconds(2)", StringComparison.Ordinal) &&
        previewSource.Contains("DateTimeOffset.UtcNow < _nextReadinessRecoveryAt", StringComparison.Ordinal) &&
        previewSource.Contains("_nextReadinessRecoveryAt = DateTimeOffset.MaxValue;", StringComparison.Ordinal),
        "post-timeout readiness recovery must be throttled and reset for each navigation");
    Assert(previewSource.Contains("HasPreviewStarted(lastDiagnostic)", StringComparison.Ordinal) &&
        !previewSource.Contains("finalRendered", StringComparison.Ordinal) &&
        !previewSource.Contains("finalBridge", StringComparison.Ordinal) &&
        !previewSource.Contains("finalSetter", StringComparison.Ordinal),
        "startup-tail status must use the shared helper without duplicated readiness locals");
    Assert(System.Text.RegularExpressions.Regex.IsMatch(previewSource,
            @"private void EnterReadinessRecovery\(long navigationGeneration, ulong navigationId, string message\).*?" +
            @"IsCurrentNavigation\(navigationGeneration, navigationId\).*?_pageReady = false;.*?" +
            @"_visualPatchPending = true;.*?_patchTimer\.Stop\(\);.*?" +
            @"_nextReadinessRecoveryAt = DateTimeOffset\.UtcNow \+ ReadinessRecoveryInterval;",
            System.Text.RegularExpressions.RegexOptions.Singleline),
        "patch failure must stop the 80 ms loop and enter navigation-scoped readiness recovery");
    Assert(System.Text.RegularExpressions.Regex.IsMatch(previewSource,
            @"EnterReadinessRecovery\(\s*navigationGeneration,\s*navigationId,",
            System.Text.RegularExpressions.RegexOptions.Singleline) &&
        System.Text.RegularExpressions.Regex.IsMatch(previewSource,
            @"if \(_activeNavigationVisualRevision == _visualPatchRevision\)\s*" +
            @"_visualPatchPending = false;\s*ScheduleVisualPatch\(\);",
            System.Text.RegularExpressions.RegexOptions.Singleline),
        "readiness recovery must retain a failed pending patch and reschedule it after the same navigation is ready");
    return Task.CompletedTask;
}

static Task TestAboutContractsAsync()
{
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amll-about-tests"));
    string withSeparator = root + Path.DirectorySeparatorChar;
    string expectedLicense = Path.Combine(root, "LICENSE.txt");
    Assert(AboutContracts.ResolveLegalFilePath("license", root) == expectedLicense,
        "legal files must resolve when baseDirectory has no trailing separator");
    Assert(AboutContracts.ResolveLegalFilePath("license", withSeparator) == expectedLicense,
        "legal files must resolve when baseDirectory has a trailing separator");
    string relativeRoot = Path.Combine(".", Path.GetFileName(root));
    Assert(AboutContracts.ResolveLegalFilePath("notice", relativeRoot) ==
        Path.Combine(Path.GetFullPath(relativeRoot), "NOTICE.txt"),
        "legal resolution must canonicalize the base directory before checking its boundary");
    Assert(AboutContracts.ResolveLegalFilePath("unknown", root) is null,
        "unknown legal-file keys must be rejected");

    Assert(AboutContracts.IsTrustedProjectUri(new Uri("https://github.com/Cplay00/project")),
        "HTTPS github.com paths on the default port must be trusted");
    foreach (string rejected in new[]
    {
        "http://github.com/Cplay00/project",
        "https://github.com:444/Cplay00/project",
        "https://www.github.com/Cplay00/project",
        "https://github.com",
        "https://github.com/",
        "https://github.com.evil.example/Cplay00/project",
    })
        Assert(!AboutContracts.IsTrustedProjectUri(new Uri(rejected)), $"untrusted project URI accepted: {rejected}");

    Assert(AboutContracts.FormatDisplayVersion(" 1.2.3-beta.1+commit.abcdef ", new Version(9, 9)) == "1.2.3-beta.1",
        "display version must use informational/product version and strip +commit metadata");
    Assert(AboutContracts.FormatDisplayVersion("+commit.abcdef", new Version(1, 2, 3, 4)) == "1.2.3.4",
        "empty informational version must fall back to assembly version");
    Assert(!string.IsNullOrWhiteSpace(AboutContracts.GetDisplayVersion()),
        "current assembly display version must be available dynamically");
    return Task.CompletedTask;
}

static Task TestRequiredTitleAsync()
{
    Assert(!AppState.IsTitleValid(null) && !AppState.IsTitleValid("   "), "blank title must be rejected");
    Assert(AppState.IsTitleValid("Song"), "non-empty title must be accepted");
    return Task.CompletedTask;
}

static Task TestGuiOutputResolutionAsync()
{
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amll-output-contract"));
    string audio = Path.Combine(root, "Music", "bad:name.flac");
    Assert(RenderContracts.TryResolveGuiOutputPath(null, root, audio, 1920, 1080, 60,
        out string? generated, out string? generatedError), generatedError ?? "blank output must resolve");
    Assert(generated == Path.Combine(root, "out", "bad_name-1920x1080-60fps.mp4"),
        "blank GUI output must use the Node-compatible absolute default name");
    Assert(RenderContracts.TryResolveGuiOutputPath(Path.Combine("exports", "mv.mkv"), root, audio, 1920, 1080, 60,
        out string? relative, out _), "relative output must resolve against ProjectRoot");
    Assert(relative == Path.Combine(root, "exports", "mv.mkv") && Path.IsPathFullyQualified(relative),
        "relative GUI output must canonicalize to one absolute path");
    string absolute = Path.Combine(root, "absolute.mp4");
    Assert(RenderContracts.TryResolveGuiOutputPath(absolute, root, audio, 1920, 1080, 60,
        out string? resolvedAbsolute, out _) && resolvedAbsolute == absolute,
        "absolute output must remain the canonical request path");
    string invalid = "bad" + '\0' + "path.mp4";
    Assert(!RenderContracts.TryResolveGuiOutputPath(invalid, root, audio, 1920, 1080, 60, out _, out string? error) &&
        error?.Contains("输出路径无效", StringComparison.Ordinal) == true,
        "GetFullPath failures must become user-facing errors");
    return Task.CompletedTask;
}

static Task TestSystemFontFallbackAsync()
{
    IReadOnlyList<string> fallback = SystemFontCatalog.GetFontFamilies(() => throw new ExternalException("probe"));
    Assert(fallback.Count > 0 && fallback.Contains("Microsoft YaHei UI"), "font enumeration failure must return fallback families");
    IReadOnlyList<string> filtered = SystemFontCatalog.GetFontFamilies(() => ["@Vertical", "Segoe UI", "segoe ui", " Arial "]);
    Assert(filtered.SequenceEqual(["Arial", "Segoe UI"]), "font catalog must filter @ aliases, trim, deduplicate and sort");
    return Task.CompletedTask;
}

static Task TestEncoderContractsAsync()
{
    string json = "{\"encoders\":[{\"name\":\"auto\",\"compiled\":true,\"available\":true,\"supportedPresets\":[]},{\"name\":\"libx264\",\"compiled\":true,\"available\":false,\"failureReason\":\"probe failed\",\"supportedPresets\":[\"medium\"]},{\"name\":\"h264_nvenc\",\"compiled\":true,\"available\":true,\"supportedPresets\":[\"p1\",\"p4\",\"p7\"],\"defaultPreset\":\"p4\"},{\"name\":\"h264_qsv\",\"compiled\":true,\"available\":true,\"supportedPresets\":[\"medium\"],\"defaultPreset\":\"medium\"}]}";
    Dictionary<string, bool> found = RenderRunner.ParseEncoderAvailability(json);
    Assert(found["auto"] && !found["libx264"] && found["h264_qsv"], "probe availability flags must be preserved");
    Dictionary<string, EncoderProbeResult> capabilities = RenderRunner.ParseEncoderCapabilities(json);
    Assert(capabilities["libx264"].Compiled && capabilities["libx264"].FailureReason == "probe failed",
        "compiled and failureReason fields must be preserved");
    Assert(capabilities["h264_qsv"].SupportedPresets.SequenceEqual(["medium"]), "supported presets must be preserved");
    Assert(capabilities["h264_nvenc"].DefaultPreset == "p4", "probe defaultPreset must be parsed");
    Assert(RenderContracts.GetSupportedEncoderPresets("h264_nvenc", capabilities["h264_nvenc"].SupportedPresets)
        .SequenceEqual(["p1", "p4", "p7"]), "GUI presets must be the canonical/probed intersection without p2");
    Assert(RenderRunner.ParseEncoderAvailability(" V....D hevc_amf AMD").Count == 1,
        "legacy text must not be treated as a successful real encode probe");
    string[] all = ["auto", "libx264", "libx265", "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf"];
    Assert(all.SequenceEqual(RenderContracts.Encoders), "KnownEncoders must contain the complete canonical order");
    Assert(RenderContracts.GetEncoderPresets("libx265").SequenceEqual(["ultrafast", "fast", "medium", "slow", "veryslow"]), "software presets mismatch");
    Assert(RenderContracts.GetEncoderPresets("h264_nvenc").SequenceEqual(["p1", "p2", "p3", "p4", "p5", "p6", "p7"]), "NVENC presets mismatch");
    Assert(RenderContracts.GetEncoderPresets("h264_nvenc", false).SequenceEqual(["fast", "medium", "slow"]), "legacy NVENC mapping mismatch");
    Assert(RenderContracts.GetEncoderPresets("hevc_qsv").SequenceEqual(["veryfast", "medium", "veryslow"]), "QSV presets mismatch");
    Assert(RenderContracts.GetEncoderPresets("h264_amf").SequenceEqual(["speed", "balanced", "quality"]), "AMF presets mismatch");
    return Task.CompletedTask;
}

static Task TestAudioOutputCompatibilityAsync()
{
    Assert(RenderContracts.GetAudioOutputError(null, "flac", 0, 0)?.Contains("MP4", StringComparison.Ordinal) == true,
        "default MP4 must reject FLAC audio");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mp4", "flac", 0, 0) is not null,
        "explicit MP4 must reject FLAC audio");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "flac", 250, 1200) is null,
        "MKV must permit FLAC audio with timing padding");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "copy", 0, 1)?.Contains("片头或片尾", StringComparison.Ordinal) == true,
        "copy with positive tail must be rejected");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "copy", 1, 0)?.Contains("片头或片尾", StringComparison.Ordinal) == true,
        "copy with positive head must be rejected");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "copy", 0, 0) is null,
        "copy with zero timing padding must remain selectable");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mov", "copy", 0, 0) is not null,
        "MOV must reject source-audio copy because only MKV is allowed");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mp4", "copy", 0, 0)?.Contains("仅支持 MKV", StringComparison.Ordinal) == true,
        "MP4 must reject FLAC source copy before frame capture");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.avi", "aac", 0, 0) is not null,
        "unsupported output containers must be rejected before rendering");
    Assert(RenderContracts.AudioCopyOffsetWarning.Contains("偏移", StringComparison.Ordinal),
        "copy warning must mention offset failure risk");
    AssertThrows(() => RenderRunner.ValidateRequest(CanonicalRequest(audioCodec: "flac")),
        "RenderRunner must enforce default MP4/FLAC compatibility");
    AssertThrows(() => RenderRunner.ValidateRequest(CanonicalRequest(audioCodec: "copy", head: 0)),
        "RenderRunner must reject copy with the default positive tail");
    AssertThrows(() => RenderRunner.ValidateRequest(CanonicalRequest(audioCodec: "copy", head: 1, tail: 0)),
        "RenderRunner must reject copy with a positive head");
    return Task.CompletedTask;
}

static Task TestRenderArgumentsAsync()
{
    var request = CanonicalRequest();
    IReadOnlyList<string> args = RenderRunner.BuildArguments("render-mv.mjs", request, @"D:\runtime\ffmpeg.exe", @"D:\runtime\msedge.exe", ["p1", "p4", "p7"]);
    Assert(ValueOf(args, "--head") == "250", "head duration argument mismatch");
    Assert(ValueOf(args, "--tail") == "1200", "tail duration argument mismatch");
    Assert(ValueOf(args, "--font-scale") == "1.25", "floating point must use invariant decimal separator");
    Assert(ValueOf(args, "--lyric-offset") == "-12.5", "negative floating argument mismatch");
    Assert(ValueOf(args, "--lyric-width") == "62", "lyric width must use percent units");
    Assert(ValueOf(args, "--show-controls") == "false", "boolean argument mismatch");
    Assert(ValueOf(args, "--browser-gpu") == "off", "BrowserGpu must be a tri-state string");
    Assert(ValueOf(args, "--ffmpeg") == @"D:\runtime\ffmpeg.exe", "absolute ffmpeg path missing");
    Assert(ValueOf(args, "--browser") == @"D:\runtime\msedge.exe", "absolute browser path missing");
    string[] required = ["--head", "--tail", "--title", "--artist", "--album", "--font-family", "--font-scale", "--font-weight",
        "--secondary-mode", "--show-song-info", "--show-artist", "--show-album", "--show-artwork", "--show-controls", "--artwork-scale",
        "--lyric-align", "--lyric-offset", "--lyric-width", "--lyric-height", "--info-offset-x", "--info-offset-y",
        "--lyric-fade", "--background-dim", "--background-saturation",
        "--background-blur", "--encoder", "--encoder-preset", "--quality", "--audio-codec", "--audio-bitrate", "--browser-gpu"];
    foreach (string flag in required) Assert(args.Contains(flag), $"missing CLI flag {flag}");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(lyricFade: 0.5), supportedPresets: ["p4"]), "out-of-range LyricFade must be rejected");
    Assert(ValueOf(args, "--lyric-fade") == "0.12", "lyric fade argument mismatch");

    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(lyricWidth: 0.62), supportedPresets: ["p4"]), "fractional LyricWidth must be rejected");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(encoderPreset: "ultrafast"), supportedPresets: ["p4"]), "illegal NVENC preset must be rejected");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(encoderPreset: "p2"), supportedPresets: ["p1", "p4", "p7"]),
        "preset absent from the selected encoder probe must be rejected before capture");
    RenderRequest WithChanges(double? lyricWidth = null, string? encoder = null, string? encoderPreset = null,
        string? audioCodec = null, int? head = null, int? tail = null, double? lyricFade = null, string? outputPath = null) =>
        CanonicalRequest(lyricWidth ?? request.LyricWidth, encoder ?? request.Encoder, encoderPreset ?? request.EncoderPreset,
            audioCodec ?? request.AudioCodec, head ?? request.Head, tail ?? request.Tail, lyricFade ?? request.LyricFade,
            outputPath ?? request.OutputPath ?? @"D:\Music\song.mp4");
    var auto = WithChanges(encoder: "auto", encoderPreset: "");
    Assert(!RenderRunner.BuildArguments("render-mv.mjs", auto).Contains("--encoder-preset"), "auto must not emit an ambiguous preset");
    var copy = WithChanges(audioCodec: "copy", head: 0, tail: 0, outputPath: @"D:\Music\song.mkv");
    Assert(!RenderRunner.BuildArguments("render-mv.mjs", copy, supportedPresets: ["p4"]).Contains("--audio-bitrate"), "copy must not emit AAC bitrate");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(outputPath: "relative.mp4"), supportedPresets: ["p4"]),
        "relative GUI request output must be rejected");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(outputPath: ""), supportedPresets: ["p4"]),
        "blank GUI request output must be rejected after GUI resolution");

    var customSized = CanonicalRequest(stageWidth: 1080, stageHeight: 1920);
    IReadOnlyList<string> sizedArgs = RenderRunner.BuildArguments("render-mv.mjs", customSized, @"D:\runtime\ffmpeg.exe", @"D:\runtime\msedge.exe", ["p1", "p4", "p7"]);
    Assert(ValueOf(sizedArgs, "--width") == "1080" && ValueOf(sizedArgs, "--height") == "1920",
        "custom stage dimensions must be forwarded to the renderer");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", CanonicalRequest(stageWidth: 1081, stageHeight: 1080), supportedPresets: ["p4"]),
        "odd custom stage dimensions must be rejected before capture");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", CanonicalRequest(stageWidth: 128, stageHeight: 720), supportedPresets: ["p4"]),
        "custom stage dimensions below the GUI minimum must be rejected");
    return Task.CompletedTask;
}

static async Task TestPreviewJavaScriptSyntaxAsync()
{
    string? node = RenderRunner.FindNodeExecutable();
    if (node is null) throw new InvalidOperationException("Node executable not found");
    var scriptState = new AppState { Title = "Patch", AudioPath = @"D:\Music\song.flac" };
    foreach (string script in new[]
             {
                 PreviewScripts.SeekBy(-5), PreviewScripts.SeekBy(5), PreviewScripts.SeekTo(12.5),
                 PreviewScripts.ApplyOptions(scriptState), PreviewScripts.Diagnose(),
             })
    {
        ProcessResult result = await RunAsync(node, ["--check", "-"], script);
        Assert(result.ExitCode == 0, $"invalid JavaScript: {result.StdErr}");
    }
}

static Task TestRuntimeLayoutAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "amll-layout-" + Guid.NewGuid().ToString("N"));
    string app = Path.Combine(root, "app");
    string content = Path.Combine(app, "runtime", "content");
    string node = Path.Combine(app, "runtime", "node", "node.exe");
    Directory.CreateDirectory(Path.Combine(content, "scripts"));
    Directory.CreateDirectory(Path.Combine(content, "dist"));
    string browser = Path.Combine(app, "runtime", "browsers", "chromium-123", "chrome-win", "chrome.exe");
    string packagedFfmpeg = Path.Combine(app, "runtime", "ffmpeg", "ffmpeg.exe");
    string stagedFfmpeg = Path.Combine(root, "project", "installer", "staging", "runtime", "ffmpeg", "ffmpeg.exe");
    string legacyFfmpeg = Path.Combine(root, "project", "node_modules", "@ffmpeg-installer", "win32-x64", "ffmpeg.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(node)!);
    Directory.CreateDirectory(Path.GetDirectoryName(browser)!);
    Directory.CreateDirectory(Path.GetDirectoryName(packagedFfmpeg)!);
    Directory.CreateDirectory(Path.GetDirectoryName(stagedFfmpeg)!);
    Directory.CreateDirectory(Path.GetDirectoryName(legacyFfmpeg)!);
    File.WriteAllText(Path.Combine(content, "scripts", "render-mv.mjs"), "");
    File.WriteAllText(Path.Combine(content, "index.html"), "");
    File.WriteAllText(Path.Combine(content, "dist", "index.html"), "");
    File.WriteAllText(node, "");
    File.WriteAllText(browser, "");
    File.WriteAllText(packagedFfmpeg, "");
    File.WriteAllText(stagedFfmpeg, "");
    File.WriteAllText(legacyFfmpeg, "");
    try
    {
        Assert(RuntimePaths.ResolveContentRoot(app) == content, "published content root mismatch");
        Assert(PreviewScripts.ResolveWebRoot(content) == Path.Combine(content, "dist"),
            "preview web root must prefer dist");
        Assert(RuntimePaths.FindNodeExecutable(app) == node, "bundled runtime/node/node.exe must win");
        Assert(RuntimePaths.FindFfmpegExecutable(Path.Combine(root, "project"), app) == packagedFfmpeg,
            "packaged runtime/ffmpeg must win over staging and npm fallback");
        File.Delete(packagedFfmpeg);
        Assert(RuntimePaths.FindFfmpegExecutable(Path.Combine(root, "project"), app) == stagedFfmpeg,
            "development installer/staging/runtime/ffmpeg must win over npm fallback");
        Assert(RuntimePaths.FindBrowserExecutable(app) == browser,
            "recursive runtime/browsers browser must win over system installations");
    }
    finally { Directory.Delete(root, recursive: true); }
    return Task.CompletedTask;
}

static async Task TestNodeCliContractAsync()
{
    string root = FindProjectRoot();
    string script = Path.Combine(root, "scripts", "render-mv.mjs");
    string audio = Path.Combine(root, "dist", "demo", "demo.wav");
    string? node = RenderRunner.FindNodeExecutable();
    string? ffmpeg = RuntimePaths.FindFfmpegExecutable(root);
    string? browser = RuntimePaths.FindBrowserExecutable();
    Assert(node is not null && File.Exists(script) && File.Exists(audio), "Node CLI smoke prerequisites missing");
    var smokeRequest = CloneWithAudio(CanonicalRequest(54, "auto", ""), audio);
    var args = new List<string>(RenderRunner.BuildArguments(script, smokeRequest, ffmpeg, browser))
    {
        "--duration", "25",
    };
    int progressIndex = args.IndexOf("--progress-json");
    if (progressIndex >= 0) args.RemoveAt(progressIndex);
    ProcessResult result = await RunAsync(node!, args, null, TimeSpan.FromSeconds(1));
    string output = result.StdOut + result.StdErr;
    Assert(!output.Contains("参数错误", StringComparison.Ordinal), $"Node rejected GUI arguments: {output}");
    Assert(result.Started, "Node CLI did not start");
}

static RenderRequest CanonicalRequest(
    double lyricWidth = 62,
    string encoder = "h264_nvenc",
    string encoderPreset = "p4",
    string audioCodec = "aac",
    int head = 250,
    int tail = 1200,
    double lyricFade = 0.12,
    string outputPath = @"D:\Music\song.mp4",
    int? stageWidth = null,
    int? stageHeight = null) => new()
{
    AudioPath = @"D:\Music\song.flac", OutputPath = outputPath, Title = "Track", Artist = "Artist", Album = "Album",
    FontFamily = "Noto Sans CJK SC", FontScale = 1.25, FontWeight = 700,
    SecondaryMode = "both", ShowSongInfo = true, ShowArtist = false, ShowAlbum = true, ShowArtwork = false, ShowControls = false,
    ArtworkScale = 0.85, LyricAlign = "center", LyricOffset = -12.5, LyricWidth = lyricWidth, LyricFade = lyricFade,
    BackgroundDim = 0.35, BackgroundSaturation = 1.1, BackgroundBlur = 24.5,
    FlowSpeed = 2.5, RenderScale = 0.75, Head = head, Tail = tail, Encoder = encoder, EncoderPreset = encoderPreset,
    Quality = 17, AudioCodec = audioCodec, AudioBitrate = "320k", BrowserGpu = "off",
    StageWidth = stageWidth, StageHeight = stageHeight,
};

static RenderRequest CloneWithAudio(RenderRequest source, string audioPath) => new()
{
    AudioPath = audioPath, LyricPath = source.LyricPath, CoverPath = source.CoverPath, OutputPath = source.OutputPath,
    Title = source.Title, Artist = source.Artist, Album = source.Album, FontFamily = source.FontFamily,
    FontScale = source.FontScale, FontWeight = source.FontWeight, SecondaryMode = source.SecondaryMode,
    ShowSongInfo = source.ShowSongInfo, ShowArtist = source.ShowArtist, ShowAlbum = source.ShowAlbum,
    ShowArtwork = source.ShowArtwork, ShowControls = source.ShowControls,
    ArtworkScale = source.ArtworkScale, LyricAlign = source.LyricAlign, LyricOffset = source.LyricOffset,
    LyricWidth = source.LyricWidth, LyricFade = source.LyricFade, BackgroundDim = source.BackgroundDim,
    BackgroundSaturation = source.BackgroundSaturation, BackgroundBlur = source.BackgroundBlur,
    Preset = source.Preset, Fps = source.Fps, FlowSpeed = source.FlowSpeed, RenderScale = source.RenderScale,
    Head = source.Head, Tail = source.Tail, Encoder = source.Encoder, EncoderPreset = source.EncoderPreset, Quality = source.Quality,
    AudioCodec = source.AudioCodec, AudioBitrate = source.AudioBitrate, BrowserGpu = source.BrowserGpu,
};

static string FindProjectRoot()
{
    string? directory = AppContext.BaseDirectory;
    for (int i = 0; i < 10 && directory is not null; i++)
    {
        if (File.Exists(Path.Combine(directory, "scripts", "render-mv.mjs"))) return directory;
        directory = Directory.GetParent(directory)?.FullName;
    }
    throw new DirectoryNotFoundException("AMLL project root not found");
}

static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> args, string? stdin, TimeSpan? stopAfter = null)
{
    var psi = new ProcessStartInfo(executable) { RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in args) psi.ArgumentList.Add(arg);
    using var process = new Process { StartInfo = psi };
    process.Start();
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    if (stdin is not null) { await process.StandardInput.WriteAsync(stdin); process.StandardInput.Close(); }
    if (stopAfter is null) await process.WaitForExitAsync();
    else
    {
        Task completed = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(stopAfter.Value));
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    return new ProcessResult(true, process.ExitCode, await stdout, await stderr);
}

static string ValueOf(IReadOnlyList<string> args, string flag)
{
    int index = -1;
    for (int i = 0; i < args.Count; i++) if (args[i] == flag) { index = i; break; }
    return index >= 0 && index + 1 < args.Count ? args[index + 1] : "";
}

static void AssertThrows(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record ProcessResult(bool Started, int ExitCode, string StdOut, string StdErr);
