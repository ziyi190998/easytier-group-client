using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyTierGroupClient.Services;

public sealed record AllocInfo(
    string Ip, string NetworkName, string NetworkSecret, IReadOnlyList<string> PeerUrls, int HeartbeatIntervalSec);

public sealed class AllocApiException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    public AllocApiException(int statusCode, string errorCode, string message) : base(message)
        => (StatusCode, ErrorCode) = (statusCode, errorCode);

    public bool IsInvalidCode => StatusCode == 401 && ErrorCode == "invalid_invite_code";
    public bool IsPoolFull => StatusCode == 503 && ErrorCode == "pool_exhausted";
    public bool IsCodeInUse => StatusCode == 409 && ErrorCode == "code_in_use";
}

/// <summary>IP 分配服务客户端。TLS 校验采用证书指纹固定（防中间人截获口令）。</summary>
public sealed class AllocApiClient : IDisposable
{
    private readonly HttpClient _http;

    public AllocApiClient(string baseUrl, string pinnedCertSha256)
    {
        if (string.IsNullOrWhiteSpace(pinnedCertSha256))
            throw new InvalidOperationException("缺少服务端证书指纹配置");

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var sha = SHA256.Create();
                    var hash = Convert.ToHexString(sha.ComputeHash(cert.GetRawCertData()));
                    return string.Equals(hash, pinnedCertSha256, StringComparison.OrdinalIgnoreCase);
                }
            }
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    private static HttpContent JsonBody(params (string Key, string Value)[] fields)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(fields[i].Key).Append("\":")
              .Append(JsonSerializer.Serialize(fields[i].Value));
        }
        sb.Append('}');
        return new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
    }

    private async Task<JsonDocument> PostAsync(string path, (string, string)[] fields, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(path, JsonBody(fields), ct);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "";
            throw new AllocApiException((int)resp.StatusCode, err ?? "", $"接口 {path} 返回 {(int)resp.StatusCode} {err}");
        }
        return doc;
    }

    public async Task<AllocInfo> AllocAsync(string inviteCode, string machineId, CancellationToken ct)
    {
        using var doc = await PostAsync("api/alloc",
            new[] { ("invite_code", inviteCode), ("machine_id", machineId) }, ct);
        var root = doc.RootElement;
        var peers = root.GetProperty("peer_urls").EnumerateArray()
            .Select(p => p.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        return new AllocInfo(
            root.GetProperty("ip").GetString()!,
            root.GetProperty("network_name").GetString()!,
            root.GetProperty("network_secret").GetString()!,
            peers,
            root.TryGetProperty("heartbeat_interval_sec", out var h) ? h.GetInt32() : 30);
    }

    /// <summary>心跳续期。返回 false 表示租约已丢失，需要重新申请 IP。</summary>
    public async Task<bool> HeartbeatAsync(string inviteCode, string ip, string machineId, CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync("api/heartbeat",
                new[] { ("invite_code", inviteCode), ("ip", ip), ("machine_id", machineId) }, ct);
            return true;
        }
        catch (AllocApiException ex) when (ex.StatusCode == 409)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(string inviteCode, string ip, string machineId)
    {
        try
        {
            using var doc = await PostAsync("api/release",
                new[] { ("invite_code", inviteCode), ("ip", ip), ("machine_id", machineId) }, CancellationToken.None);
        }
        catch (Exception)
        {
            // 释放为尽力而为：服务端心跳超时也会自动回收
        }
    }

    public void Dispose() => _http.Dispose();
}
