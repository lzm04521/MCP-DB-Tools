using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using McpDbTools.Server;
using Microsoft.Extensions.Hosting;

namespace McpDbTools.Server.Hosting;

/// <summary>
/// 系统托盘宿主：单 exe 同时承载 MCP/Admin Web 服务与托盘 UI。
/// <para>
/// 主线程跑 WinForms 消息循环（<see cref="Application.Run"/>），Web 宿主由 Program 在后台 Task 启动。
/// 托盘菜单负责"打开管理页"与"退出"。退出走 <see cref="IHostApplicationLifetime.StopApplication"/>，
/// Web 宿主优雅停止（含审计队列排空，ShutdownTimeout=15s）后，<see cref="IHostApplicationLifetime.ApplicationStopped"/>
/// 回调调用 <see cref="Application.Exit"/> 退出消息循环。
/// </para>
/// <para>
/// 图标暂用 <see cref="SystemIcons.Application"/> 系统占位，后续替换品牌 .ico（见实施文档阶段 1）。
/// </para>
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly int _port;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;

    internal TrayHost(IHostApplicationLifetime lifetime, int port)
    {
        _lifetime = lifetime;
        _port = port;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开管理页(&A)", null, (_, _) => OpenAdmin());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add($"关于 McpDbTools(&B)  v{AppVersion.Current}", null, (_, _) => ShowAbout());
        _menu.Items.Add("重启服务(&R)", null, (_, _) => Restart());
        _menu.Items.Add("退出(&X)", null, (_, _) => Exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            // NotifyIcon.Text 上限 63 字符
            Text = $"McpDbTools v{AppVersion.Current}",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenAdmin();

        // 退出路径：用户点"退出" → StopApplication → Web 宿主优雅停止 → ApplicationStopped → Exit 消息循环。
        // Application.Exit 线程安全，可从 ApplicationStopped 的后台回调直接调用。
        _lifetime.ApplicationStopped.Register(() => Application.Exit());
    }

    /// <summary>在主线程跑 WinForms 消息循环。返回表示已 <see cref="Application.Exit"/>。</summary>
    internal void Run() => Application.Run();

    /// <summary>用系统默认浏览器打开 Admin UI。打开失败不阻断（"关于"项含完整地址）。</summary>
    private void OpenAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_port}/admin") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 忽略：用户可手动访问 URL
        }
    }

    /// <summary>从嵌入资源加载品牌图标（app.ico），失败回退系统默认图标。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            Assembly asm = typeof(TrayHost).Assembly;
            // 枚举资源名找 app.ico（不依赖确切逻辑名前缀），找不到则回退系统图标
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return SystemIcons.Application;
            }
            using Stream? stream = asm.GetManifestResourceStream(name);
            return stream is not null ? new Icon(stream) : SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"McpDbTools v{AppVersion.Current}\n\n" +
            $"Admin UI: http://127.0.0.1:{_port}/admin\n" +
            $"MCP:      http://127.0.0.1:{_port}/mcp",
            "关于 McpDbTools",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>触发优雅退出：通知 Web 宿主停止，由 ApplicationStopped 回调完成消息循环退出。</summary>
    private void Exit() => _lifetime.StopApplication();

    /// <summary>重启：拉起新实例（延迟，等当前退出释放端口）后停当前实例。用于端口变更后应用新配置。</summary>
    private void Restart() => RestartHelper.RestartAndExit(_lifetime);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
