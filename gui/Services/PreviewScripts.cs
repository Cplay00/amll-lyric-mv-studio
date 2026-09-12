using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AmllMvStudio.Services;

/// <summary>集中生成可单独语法检查的 WebView2 播放控制脚本。</summary>
public static class PreviewScripts
{
    public static string ResolveWebRoot(string projectRoot)
    {
        string distRoot = Path.Combine(projectRoot, "dist");
        return File.Exists(Path.Combine(distRoot, "index.html")) ? distRoot : projectRoot;
    }

    /// <summary>解码 WebView2 对脚本返回值额外施加的一层 JSON 编码。</summary>
    internal static string? DecodeExecuteScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        using JsonDocument document = JsonDocument.Parse(raw);
        return document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()
            : document.RootElement.GetRawText();
    }

    /// <summary>播放/暂停走桥接层：LyricPlayer 依赖 playing 状态驱动间奏动画，绕过桥接会丢相位。</summary>
    public static string TogglePlay() =>
        "(function(){const b=window.__amllMv;const a=document.querySelector('audio');" +
        "if(!a)return null;if(a.paused){if(b&&typeof b.play==='function')void b.play();else void a.play();return 'play';}" +
        "if(b&&typeof b.pause==='function')b.pause();else a.pause();return 'pause';})()";

    public static string SeekBy(double deltaSeconds)
    {
        string delta = deltaSeconds.ToString(CultureInfo.InvariantCulture);
        return "(function(){const b=window.__amllMv;const a=document.querySelector('audio');if(!a)return null;" +
               $"const t=a.currentTime+({delta});const clamped=Math.max(0,Number.isFinite(a.duration)?Math.min(t,a.duration):t);" +
               "if(b&&typeof b.seek==='function')b.seek(Math.round(clamped*1000));" +
               "else a.currentTime=clamped;return a.currentTime;})()";
    }

    public static string SeekTo(double targetSeconds)
    {
        string target = targetSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return "(function(){const b=window.__amllMv;const a=document.querySelector('audio');if(!a)return null;" +
               $"const clamped=Math.max(0,Number.isFinite(a.duration)?Math.min({target},a.duration):{target});" +
               "if(b&&typeof b.seek==='function')b.seek(Math.round(clamped*1000));" +
               "else a.currentTime=clamped;return a.currentTime;})()";
    }

    public static IReadOnlyList<string> BuildQuery(
        AppState state, LocalAssetServer server, int? previewWidth = null, int? previewHeight = null)
    {
        var query = new List<string> { "auto=1", "chrome=0", "autoplay=0", "preview=1" };
        Add("audio", server.Mount("/__mv/audio" + Path.GetExtension(state.AudioPath!), state.AudioPath!));
        if (!string.IsNullOrWhiteSpace(state.LyricPath))
            Add("lyric", server.Mount("/__mv/lyric" + Path.GetExtension(state.LyricPath), state.LyricPath));
        if (!string.IsNullOrWhiteSpace(state.CoverPath))
            Add("cover", server.Mount("/__mv/cover" + Path.GetExtension(state.CoverPath), state.CoverPath));

        StagePreset preset = StagePresets.Resolve(
            state.Preset, state.CustomStageWidth, state.CustomStageHeight);
        Add("title", state.Title); Add("artist", state.Artist); Add("album", state.Album);
        Add("fontFamily", state.FontFamily); Add("fontScale", F(state.FontScale));
        Add("fontWeight", state.FontWeight.ToString(CultureInfo.InvariantCulture));
        Add("secondaryMode", state.SecondaryMode); Add("showSongInfo", B(state.ShowSongInfo));
        Add("showArtist", B(state.ShowArtist)); Add("showAlbum", B(state.ShowAlbum));
        Add("showArtwork", B(state.ShowArtwork)); Add("showControls", B(state.ShowControls));
        Add("artworkScale", F(state.ArtworkScale)); Add("lyricAlign", state.LyricAlign);
        Add("lyricOffset", F(state.LyricOffset)); Add("lyricWidth", F(state.LyricWidth));
        Add("lyricHeight", F(state.LyricHeight));
        Add("infoOffsetX", F(state.InfoOffsetX)); Add("infoOffsetY", F(state.InfoOffsetY));
        Add("lyricFade", F(state.LyricFade));
        Add("backgroundDim", F(state.BackgroundDim));
        Add("backgroundSaturation", F(state.BackgroundSaturation));
        Add("backgroundBlur", F(state.BackgroundBlur)); Add("flowSpeed", F(state.FlowSpeed));
        Add("renderScale", F(state.RenderScale)); Add("preset", state.Preset);
        Add("fps", state.Fps.ToString(CultureInfo.InvariantCulture));
        Add("width", preset.Width.ToString(CultureInfo.InvariantCulture));
        Add("height", preset.Height.ToString(CultureInfo.InvariantCulture));
        return query;

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string B(bool value) => value ? "true" : "false";
        void Add(string key, string value) => query.Add($"{key}={Uri.EscapeDataString(value)}");
    }

    public static string ApplyOptions(AppState state)
    {
        StagePreset preset = StagePresets.Resolve(
            state.Preset, state.CustomStageWidth, state.CustomStageHeight);
        var patch = new
        {
            presetId = state.Preset,
            stageWidth = preset.Width,
            stageHeight = preset.Height,
            fps = state.Fps,
            flowSpeed = state.FlowSpeed,
            renderScale = state.RenderScale,
            title = state.Title,
            artist = state.Artist,
            album = state.Album,
            fontFamily = state.FontFamily,
            fontScale = state.FontScale,
            fontWeight = state.FontWeight,
            secondaryMode = state.SecondaryMode,
            showSongInfo = state.ShowSongInfo,
            showArtist = state.ShowArtist,
            showAlbum = state.ShowAlbum,
            showArtwork = state.ShowArtwork,
            showControls = state.ShowControls,
            artworkScale = state.ArtworkScale,
            lyricAlign = state.LyricAlign,
            lyricOffset = state.LyricOffset,
            lyricWidth = state.LyricWidth,
            lyricHeight = state.LyricHeight,
            infoOffsetX = state.InfoOffsetX,
            infoOffsetY = state.InfoOffsetY,
            lyricFade = state.LyricFade,
            backgroundDim = state.BackgroundDim,
            backgroundSaturation = state.BackgroundSaturation,
            backgroundBlur = state.BackgroundBlur,
        };
        string json = JsonSerializer.Serialize(patch, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
        });
        return "(function(){const bridge=window.__amllMv;" +
               "if(!bridge||typeof bridge.setOptions!=='function')return JSON.stringify({ok:false,reason:'bridge'});" +
               $"bridge.setOptions({json});return JSON.stringify({{ok:true}});}})()";
    }

    public static string Diagnose() =>
        "(function(){const root=document.getElementById('root');const bridge=window.__amllMv;" +
        "return JSON.stringify({root:!!root,rendered:!!(root&&root.childElementCount)," +
        "bridge:!!bridge,setOptions:typeof bridge?.setOptions==='function'," +
        "ready:bridge?.ready===true,readiness:bridge?.readiness??null," +
        "error:bridge?.error??null,title:document.title||''});})()";
}
