using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Velopack.Sources;

namespace McpDbTools.Server.Admin;

/// <summary>
/// 注入 <c>GithubSource</c> 的定制下载器，修正 Velopack 默认 <see cref="HttpClientFileDownloader"/>
/// 在国内直连 GitHub 场景的三个问题（行为依据 Velopack 1.2.0 源码核对）：
/// <list type="bullet">
/// <item>每个请求 new 一个 HttpClient，无 TLS 连接复用——一次检查要串行下载最近 10 个 release 的
/// releases.{channel}.json，逐个握手被成倍放大。</item>
/// <item>默认超时 30 分钟——网络"半死不活"时单请求即可挂满 30 分钟才报错。</item>
/// <item>任何下载失败后用全小写 URL 重试一次——GitHub asset URL 大小写敏感，小写变体必 404，纯浪费往返。</item>
/// </list>
/// <para>
/// 修正方式：全部请求共享一个 <see cref="SocketsHttpHandler"/> 连接池（每请求仍独立 HttpClient 实例，
/// 保持请求头互不污染）；检查类小请求（releases API 与 releases.win.json）超时 clamp 到
/// <see cref="CheckTimeoutMinutes"/>；整包下载（<c>DownloadFile</c>，几十 MB nupkg）保持调用方超时不动。
/// </para>
/// </summary>
public class VelopackFileDownloader : HttpClientFileDownloader
{
    /// <summary>检查类请求超时上限（分钟）= 15 秒，对齐 UpdateChecker 拉 release notes 的 ReleaseHttp 超时。</summary>
    internal const double CheckTimeoutMinutes = 0.25;

    // 共享连接池：一次检查内的十余个小请求复用 TLS 连接；定期回收兼顾 DNS 变化（检查每小时一次，5 分钟足够）
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    /// <summary>检查类小请求超时 clamp：取调用方超时与 15s 上限的较小值。纯函数供单测。</summary>
    internal static double ClampCheckTimeout(double requestedMinutes)
        => Math.Min(requestedMinutes, CheckTimeoutMinutes);

    public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        => base.DownloadString(url, headers, ClampCheckTimeout(timeout));

    public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        => base.DownloadBytes(url, headers, ClampCheckTimeout(timeout));

    // GitHub asset URL 区分大小写，小写变体必然 404；短路该兜底，失败只打一次
    protected override Task<T> TryDownloadThenLowercase<T>(Func<string, Task<T>> downloadFunc, string url)
        => downloadFunc(url);

    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
    {
        // 固定共享 handler（连接复用）；独立 HttpClient 实例保证 DefaultRequestHeaders 按请求隔离
        var client = new HttpClient(SharedHandler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        foreach (var header in headers ?? new Dictionary<string, string>())
        {
            client.DefaultRequestHeaders.Add(header.Key, header.Value);
        }
        client.Timeout = TimeSpan.FromMinutes(timeout);
        return client;
    }
}
