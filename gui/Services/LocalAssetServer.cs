using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AmllMvStudio.Services;

/// <summary>
/// 极简本地 HTTP 服务，用于把「已构建的工作台页面」与「用户选择的素材」放在同一源下，
/// 供 WebView2 预览。使用 TcpListener 而非 HttpListener，避免非管理员权限下的
/// URL ACL 保留要求。
///
/// 支持 Range 请求——HTML5 audio 拖动进度条依赖它，否则预览无法定位。
/// </summary>
public sealed class LocalAssetServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _routes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rootDir;
    private readonly string _rootPrefix;
    private Task? _acceptLoop;
    private int _started;
    private int _disposed;

    public int Port { get; }

    public LocalAssetServer(string rootDir)
    {
        _rootDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        _rootPrefix = _rootDir + Path.DirectorySeparatorChar;

        // 端口 0 让系统分配空闲端口，避免与开发服务器或其它实例冲突
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>把某个本地文件挂载到指定 URL 路径下</summary>
    public string Mount(string urlPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!urlPath.StartsWith('/') || urlPath.Contains('?', StringComparison.Ordinal) ||
            urlPath.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("挂载路径必须是无查询参数的绝对 URL 路径。", nameof(urlPath));

        _routes[urlPath] = Path.GetFullPath(filePath);
        return urlPath;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using NetworkStream stream = client.GetStream();

                var (method, rawPath, headers) = await ReadRequestAsync(stream);
                if (method is null) return;

                // 仅接受应用实际发布的 authority，阻断缺失 Host、DNS rebinding 与备用环回名称。
                string expectedAuthority = $"127.0.0.1:{Port}";
                if (!headers.TryGetValue("host", out string? host) ||
                    !string.Equals(host, expectedAuthority, StringComparison.Ordinal))
                {
                    await WriteStatusAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("403 Forbidden"), headOnly: false);
                    return;
                }

                bool isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                if (!isHead && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("405 Method Not Allowed"), headOnly: false,
                        extraHeaders: "Allow: GET, HEAD\r\n");
                    return;
                }

                string path;
                try
                {
                    path = Uri.UnescapeDataString(rawPath.Split('?', 2)[0]);
                }
                catch (UriFormatException)
                {
                    await WriteStatusAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("400 Bad Request"), headOnly: isHead);
                    return;
                }

                string? filePath = ResolveFile(path);
                if (filePath is null)
                {
                    await WriteStatusAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("404 Not Found: " + path), headOnly: isHead);
                    return;
                }

                var info = new FileInfo(filePath);
                long total = info.Length;
                string contentType = GuessContentType(filePath);

                // 解析 Range 头（仅支持单区间，满足音视频拖动即可）
                long start = 0;
                long end = total - 1;
                bool isRange = false;
                if (headers.TryGetValue("range", out string? rangeHeader))
                {
                    if (!TryParseRange(rangeHeader, total, out start, out end))
                    {
                        await WriteStatusAsync(stream, 416, "Range Not Satisfiable", "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("416 Range Not Satisfiable"), headOnly: isHead,
                            extraHeaders: $"Content-Range: bytes */{total}\r\nAccept-Ranges: bytes\r\n");
                        return;
                    }
                    isRange = true;
                }

                long length = end - start + 1;
                var header = new StringBuilder();
                if (isRange)
                {
                    header.Append("HTTP/1.1 206 Partial Content\r\n");
                    header.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
                }
                else
                {
                    header.Append("HTTP/1.1 200 OK\r\n");
                }
                header.Append($"Content-Type: {contentType}\r\n");
                header.Append($"Content-Length: {length}\r\n");
                header.Append("Accept-Ranges: bytes\r\n");
                AppendSecurityHeaders(header);
                header.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));

                if (!isHead)
                {
                    await using var file = new FileStream(
                        filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        bufferSize: 64 * 1024, useAsync: true);
                    file.Seek(start, SeekOrigin.Begin);
                    await CopyBytesAsync(file, stream, length, _cts.Token);
                }

                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 浏览器提前断开连接属于正常现象（例如切换素材），静默忽略
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalAssetServer] {ex}");
            }
        }
    }

    private static async Task CopyBytesAsync(Stream source, Stream target, long count, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), token);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
    }

    internal static bool TryParseRange(string rangeHeader, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (total <= 0 || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        string spec = rangeHeader[6..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal)) return false;
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;

        string startText = spec[..dash].Trim();
        string endText = spec[(dash + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix);
            return true;
        }

        if (!long.TryParse(startText, out start) || start < 0 || start >= total) return false;
        if (endText.Length == 0) return true;
        if (!long.TryParse(endText, out end) || end < start) return false;
        end = Math.Min(end, total - 1);
        return true;
    }

    internal static bool IsPathWithinRoot(string rootDir, string candidate) =>
        string.Equals(candidate, rootDir, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string? ResolveFile(string urlPath)
    {
        // 显式挂载的素材优先
        if (_routes.TryGetValue(urlPath, out string? mounted))
            return File.Exists(mounted) ? mounted : null;

        string relative = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0) relative = "index.html";

        string candidate = Path.GetFullPath(Path.Combine(_rootDir, relative));
        if (!string.Equals(candidate, _rootDir, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) ? candidate : null;
    }

    private static async Task<(string? Method, string Path, Dictionary<string, string> Headers)> ReadRequestAsync(
        NetworkStream stream)
    {
        var buffer = new byte[8192];
        var accumulated = new StringBuilder();
        int headerEnd = -1;

        // 读超时：慢连接不允许无限占用线程池任务（绑定 Loopback，仅防御本机异常进程）
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (headerEnd < 0)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return (null, string.Empty, new Dictionary<string, string>());
            }
            catch (IOException)
            {
                return (null, string.Empty, new Dictionary<string, string>());
            }
            if (read <= 0) return (null, string.Empty, new Dictionary<string, string>());
            accumulated.Append(Encoding.ASCII.GetString(buffer, 0, read));
            headerEnd = accumulated.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (accumulated.Length > 64 * 1024) return (null, string.Empty, new Dictionary<string, string>());
        }

        string text = accumulated.ToString();
        string[] lines = text.Split("\r\n");
        if (lines.Length == 0) return (null, string.Empty, new Dictionary<string, string>());

        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2) return (null, string.Empty, new Dictionary<string, string>());

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string rawName = lines[i][..colon];
            string name = rawName.Trim();
            string value = lines[i][(colon + 1)..].Trim();
            if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase) &&
                (rawName.Length != name.Length || headers.ContainsKey(name)))
            {
                headers["host"] = string.Empty;
                continue;
            }
            headers[name] = value;
        }

        return (parts[0], parts[1], headers);
    }

    private static async Task WriteStatusAsync(
        NetworkStream stream, int code, string reason, string contentType, byte[] body,
        bool headOnly = false, string extraHeaders = "")
    {
        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {code} {reason}\r\n");
        header.Append($"Content-Type: {contentType}\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        header.Append(extraHeaders);
        AppendSecurityHeaders(header);
        header.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));
        if (!headOnly) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static void AppendSecurityHeaders(StringBuilder header)
    {
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Cross-Origin-Resource-Policy: same-origin\r\n");
        header.Append("X-Content-Type-Options: nosniff\r\n");
        header.Append("Referrer-Policy: no-referrer\r\n");
        header.Append("Content-Security-Policy: default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self' data: blob:; frame-ancestors 'none'\r\n");
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".ttml" or ".xml" => "application/xml; charset=utf-8",
            ".lrc" or ".yrc" or ".qrc" or ".lys" or ".lyl" or ".eslrc" or ".alrc" or ".lqe"
                => "text/plain; charset=utf-8",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[LocalAssetServer] 关闭监听器失败：{ex.Message}");
        }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
