using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using AmllMvStudio.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("canonical root boundary", TestCanonicalRootBoundaryAsync),
    ("HTTP range and method handling", TestHttpServerAsync),
    ("canonical state contract", TestCanonicalStateContractAsync),
    ("preview query and patch contract", TestPreviewQueryAndPatchAsync),
    ("required title contract", TestRequiredTitleAsync),
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

static Task TestCanonicalStateContractAsync()
{
    var state = new AppState();
    Assert(state.SecondaryMode == "both", "SecondaryMode default must be both");
    Assert(state.LyricWidth == 54, "LyricWidth default must be percent 54");
    Assert(state.LyricOffset == 0, "LyricOffset default must be 0 px");
    Assert(state.BackgroundSaturation == 1.2, "BackgroundSaturation default must be ratio 1.2");
    Assert(state.BrowserGpu == "auto", "BrowserGpu default must be auto");
    Assert(state.AudioCodec == "aac", "AudioCodec default must be aac");

    state.SecondaryMode = "translated";
    state.LyricWidth = 81;
    state.LyricOffset = -700;
    state.LyricFade = 0.5;
    state.BackgroundSaturation = double.NaN;
    state.FontScale = 3;
    state.FontWeight = 50;
    state.FlowSpeed = 8;
    state.BrowserGpu = "true";
    state.AudioCodec = "libopus";
    Assert(state.SecondaryMode == "both", "invalid SecondaryMode must normalize to both");
    Assert(state.LyricWidth == 80 && state.LyricOffset == -600, "lyric ranges must clamp to canonical bounds");
    Assert(state.LyricFade == 0.3, "LyricFade must clamp to 0..0.3");
    Assert(state.ShowArtist && state.ShowAlbum, "artist and album toggles must default to on");
    Assert(state.BackgroundSaturation == 1.2, "non-finite saturation must normalize");
    Assert(state.FontScale == 2 && state.FontWeight == 100 && state.FlowSpeed == 5,
        "visible GUI ranges must clamp to the shared contract");
    Assert(state.BrowserGpu == "auto" && state.AudioCodec == "aac", "invalid enum values must not enter state");
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
        Assert(!query.Any(item => item.StartsWith("tail=", StringComparison.Ordinal)), "export-only fields must stay out of preview query");
        string patch = PreviewScripts.ApplyOptions(state);
        Assert(patch.Contains("window.__amllMv", StringComparison.Ordinal) && patch.Contains("setOptions", StringComparison.Ordinal),
            "visual updates must call the setOptions bridge");
        Assert(patch.Contains("\"presetId\":\"vertical\"", StringComparison.Ordinal), "patch must map Preset to presetId");
        Assert(patch.Contains("\"showArtist\":true", StringComparison.Ordinal) && patch.Contains("\"lyricFade\":0.08", StringComparison.Ordinal),
            "patch must carry artist toggle and lyric fade");
        Assert(!patch.Contains("audioCodec", StringComparison.Ordinal) && !patch.Contains("tail", StringComparison.Ordinal),
            "export-only fields must stay out of live patch");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static Task TestRequiredTitleAsync()
{
    Assert(!AppState.IsTitleValid(null) && !AppState.IsTitleValid("   "), "blank title must be rejected");
    Assert(AppState.IsTitleValid("Song"), "non-empty title must be accepted");
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
    string json = "{\"encoders\":[{\"name\":\"auto\",\"compiled\":true,\"available\":true,\"supportedPresets\":[]},{\"name\":\"libx264\",\"compiled\":true,\"available\":false,\"failureReason\":\"probe failed\",\"supportedPresets\":[\"medium\"]},{\"name\":\"h264_qsv\",\"compiled\":true,\"available\":true,\"supportedPresets\":[\"medium\"]}]}";
    Dictionary<string, bool> found = RenderRunner.ParseEncoderAvailability(json);
    Assert(found["auto"] && !found["libx264"] && found["h264_qsv"], "probe availability flags must be preserved");
    Dictionary<string, EncoderProbeResult> capabilities = RenderRunner.ParseEncoderCapabilities(json);
    Assert(capabilities["libx264"].Compiled && capabilities["libx264"].FailureReason == "probe failed",
        "compiled and failureReason fields must be preserved");
    Assert(capabilities["h264_qsv"].SupportedPresets.SequenceEqual(["medium"]), "supported presets must be preserved");
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
    Assert(RenderContracts.GetAudioOutputError(null, "flac", 0)?.Contains("MP4", StringComparison.Ordinal) == true,
        "default MP4 must reject FLAC audio");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mp4", "flac", 0) is not null,
        "explicit MP4 must reject FLAC audio");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "flac", 1200) is null,
        "MKV must permit FLAC audio");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "copy", 1)?.Contains("片尾留白", StringComparison.Ordinal) == true,
        "copy with positive tail must be rejected");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mkv", "copy", 0) is null,
        "copy with zero tail must remain selectable");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.mov", "copy", 0) is not null,
        "MOV must reject source-audio copy because compatibility is not guaranteed");
    Assert(RenderContracts.GetAudioOutputError(@"D:\out\mv.avi", "aac", 0) is not null,
        "unsupported output containers must be rejected before rendering");
    Assert(RenderContracts.AudioCopyOffsetWarning.Contains("偏移", StringComparison.Ordinal),
        "copy warning must mention offset failure risk");
    AssertThrows(() => RenderRunner.ValidateRequest(CanonicalRequest(audioCodec: "flac")),
        "RenderRunner must enforce default MP4/FLAC compatibility");
    AssertThrows(() => RenderRunner.ValidateRequest(CanonicalRequest(audioCodec: "copy")),
        "RenderRunner must reject copy with the default positive tail");
    return Task.CompletedTask;
}

static Task TestRenderArgumentsAsync()
{
    var request = CanonicalRequest();
    IReadOnlyList<string> args = RenderRunner.BuildArguments("render-mv.mjs", request, @"D:\runtime\ffmpeg.exe", @"D:\runtime\msedge.exe");
    Assert(ValueOf(args, "--font-scale") == "1.25", "floating point must use invariant decimal separator");
    Assert(ValueOf(args, "--lyric-offset") == "-12.5", "negative floating argument mismatch");
    Assert(ValueOf(args, "--lyric-width") == "62", "lyric width must use percent units");
    Assert(ValueOf(args, "--show-controls") == "false", "boolean argument mismatch");
    Assert(ValueOf(args, "--browser-gpu") == "off", "BrowserGpu must be a tri-state string");
    Assert(ValueOf(args, "--ffmpeg") == @"D:\runtime\ffmpeg.exe", "absolute ffmpeg path missing");
    Assert(ValueOf(args, "--browser") == @"D:\runtime\msedge.exe", "absolute browser path missing");
    string[] required = ["--title", "--artist", "--album", "--font-family", "--font-scale", "--font-weight",
        "--secondary-mode", "--show-song-info", "--show-artist", "--show-album", "--show-artwork", "--show-controls", "--artwork-scale",
        "--lyric-align", "--lyric-offset", "--lyric-width", "--lyric-fade", "--background-dim", "--background-saturation",
        "--background-blur", "--encoder", "--encoder-preset", "--quality", "--audio-codec", "--audio-bitrate", "--browser-gpu"];
    foreach (string flag in required) Assert(args.Contains(flag), $"missing CLI flag {flag}");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(lyricFade: 0.5)), "out-of-range LyricFade must be rejected");
    Assert(ValueOf(args, "--lyric-fade") == "0.12", "lyric fade argument mismatch");

    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(lyricWidth: 0.62)), "fractional LyricWidth must be rejected");
    AssertThrows(() => RenderRunner.BuildArguments("render-mv.mjs", WithChanges(encoderPreset: "ultrafast")), "illegal NVENC preset must be rejected");
    var auto = WithChanges(encoder: "auto", encoderPreset: "");
    Assert(!RenderRunner.BuildArguments("render-mv.mjs", auto).Contains("--encoder-preset"), "auto must not emit an ambiguous preset");
    var copy = WithChanges(audioCodec: "copy", tail: 0);
    Assert(!RenderRunner.BuildArguments("render-mv.mjs", copy).Contains("--audio-bitrate"), "copy must not emit AAC bitrate");
    return Task.CompletedTask;

    RenderRequest WithChanges(double? lyricWidth = null, string? encoder = null, string? encoderPreset = null, string? audioCodec = null, int? tail = null, double? lyricFade = null) =>
        CanonicalRequest(lyricWidth ?? request.LyricWidth, encoder ?? request.Encoder, encoderPreset ?? request.EncoderPreset,
            audioCodec ?? request.AudioCodec, tail ?? request.Tail, lyricFade ?? request.LyricFade);
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
    int tail = 1200,
    double lyricFade = 0.12) => new()
{
    AudioPath = @"D:\Music\song.flac", Title = "Track", Artist = "Artist", Album = "Album",
    FontFamily = "Noto Sans CJK SC", FontScale = 1.25, FontWeight = 700,
    SecondaryMode = "both", ShowSongInfo = true, ShowArtist = false, ShowAlbum = true, ShowArtwork = false, ShowControls = false,
    ArtworkScale = 0.85, LyricAlign = "center", LyricOffset = -12.5, LyricWidth = lyricWidth, LyricFade = lyricFade,
    BackgroundDim = 0.35, BackgroundSaturation = 1.1, BackgroundBlur = 24.5,
    FlowSpeed = 2.5, RenderScale = 0.75, Tail = tail, Encoder = encoder, EncoderPreset = encoderPreset,
    Quality = 17, AudioCodec = audioCodec, AudioBitrate = "320k", BrowserGpu = "off",
};

static RenderRequest CloneWithAudio(RenderRequest source, string audioPath) => new()
{
    AudioPath = audioPath, LyricPath = source.LyricPath, CoverPath = source.CoverPath, OutputPath = source.OutputPath,
    Title = source.Title, Artist = source.Artist, Album = source.Album, FontFamily = source.FontFamily,
    FontScale = source.FontScale, FontWeight = source.FontWeight, SecondaryMode = source.SecondaryMode,
    ShowSongInfo = source.ShowSongInfo, ShowArtwork = source.ShowArtwork, ShowControls = source.ShowControls,
    ArtworkScale = source.ArtworkScale, LyricAlign = source.LyricAlign, LyricOffset = source.LyricOffset,
    LyricWidth = source.LyricWidth, BackgroundDim = source.BackgroundDim,
    BackgroundSaturation = source.BackgroundSaturation, BackgroundBlur = source.BackgroundBlur,
    Preset = source.Preset, Fps = source.Fps, FlowSpeed = source.FlowSpeed, RenderScale = source.RenderScale,
    Tail = source.Tail, Encoder = source.Encoder, EncoderPreset = source.EncoderPreset, Quality = source.Quality,
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
