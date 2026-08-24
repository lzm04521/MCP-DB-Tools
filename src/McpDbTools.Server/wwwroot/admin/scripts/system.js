/* 系统设置视图（SPA 视图模块）。
   负责：服务端口配置（改后重启生效）、登录自启动开关、Claude Code MCP 注册、应用更新（Velopack）、应用重启。
   数据流：
     - 端口：GET /admin/api/port（runningPort + configPort）→ 输入框 → PUT /admin/api/port → toast 提示重启
     - 自启动：GET /admin/api/autostart → 开关 → PUT /admin/api/autostart
     - MCP 注册：POST /admin/api/register-mcp {scope}（用当前 runningPort）
     - 更新：GET /admin/api/update/status → POST /check → POST /download（期间 500ms 轮询 /status 刷新进度条）→ POST /apply（Velopack 接管重启；轮询 /version 直到版本变化自动刷新页面）
     - 重启：POST /admin/api/restart（confirmAction 确认；旧实例退出，新实例起来）
   只读视图：各 section 独立按钮操作，不使用顶栏"保存配置"（saveLabel 为空，shell 自动隐藏保存按钮）。
   公共能力（toast/confirm/busy）来自 window.adminUi / window.adminApi。 */
(function () {
  const state = {
    runningPort: null,
    configPort: null,
    autostart: false,
    downloading: false, // 本地下载中标志（POST /download 未返回期间，先于服务端 downloadInProgress 置位）
    update: { currentVersion: null, configured: false, installed: false, hasUpdate: false, targetVersion: null, downloaded: false, error: null, downloadInProgress: false, downloadPercent: 0 }
  };
  let el = null; // mount 后填充
  let progressTimer = null; // 下载进度轮询句柄

  function template() {
    return `
      <div class="shell single settings-shell">
        <section class="card settings-card">
          <div class="card-title"><div><h2>服务端口<span class="eyebrow">Port</span></h2></div></div>
          <p class="muted">Admin 管理页与 MCP 服务共用同一端口。修改后需<strong>重启应用</strong>才生效（Kestrel 启动时绑定端口）。</p>
          <div class="settings-row">
            <label class="retention-field">
              <span>当前运行端口</span>
              <input id="sysRunningPort" type="text" readonly />
            </label>
            <label class="retention-field">
              <span>端口（保存后重启生效）</span>
              <input id="sysPortInput" type="number" min="1" max="65535" step="1" placeholder="61123" />
            </label>
            <button id="sysSavePortBtn" type="button" class="button primary">保存端口</button>
          </div>
        </section>

        <section class="card settings-card">
          <div class="card-title"><div><h2>登录自启动<span class="eyebrow">Autostart</span></h2></div></div>
          <p class="muted">勾选后写入注册表 HKCU Run（当前用户登录时启动），取消则移除。无需管理员权限。</p>
          <div class="settings-row">
            <label class="switch-row">
              <input id="sysAutostartToggle" type="checkbox" />
              <span>开机登录后自动启动</span>
            </label>
          </div>
        </section>

        <section class="card settings-card">
          <div class="card-title"><div><h2>Claude Code MCP 注册<span class="eyebrow">MCP</span></h2></div></div>
          <p class="muted">将本服务注册到 Claude Code CLI（<code>claude mcp add</code>），MCP 客户端即可连接。</p>
          <div class="settings-row">
            <label class="retention-field mcp-scope-field">
              <span>作用域</span>
              <select id="sysMcpScope">
                <option value="user">user（当前用户，推荐）</option>
                <option value="local">local（当前项目目录）</option>
                <option value="project">project</option>
              </select>
            </label>
            <button id="sysRegisterMcpBtn" type="button" class="button primary">注册到 Claude Code</button>
          </div>
          <p class="muted hint-inline">注册的 URL（基于当前运行端口）：<code id="sysMcpUrl"></code></p>
        </section>

        <section class="card settings-card">
          <div class="card-title"><div><h2>应用更新<span class="eyebrow">Update</span></h2></div></div>
          <p class="muted">当前版本 <code id="sysVersion">?</code>。通过 Velopack 检查并安装新版本。</p>
          <div class="settings-row">
            <button id="sysCheckUpdateBtn" type="button" class="button primary" disabled>检查更新</button>
            <button id="sysDownloadUpdateBtn" type="button" class="button secondary" disabled>下载</button>
            <button id="sysApplyUpdateBtn" type="button" class="button danger subtle" disabled>安装并重启</button>
          </div>
          <div id="sysUpdateProgress" class="update-progress" hidden>
            <div class="update-progress-track"><div id="sysUpdateProgressFill" class="update-progress-fill"></div></div>
            <span id="sysUpdateProgressText" class="update-progress-text">0%</span>
          </div>
          <p class="muted hint-inline" id="sysUpdateHint"></p>
        </section>

        <section class="card settings-card">
          <div class="card-title"><div><h2>应用控制<span class="eyebrow">Process</span></h2></div></div>
          <p class="muted">重启会先停止当前实例再启动新实例（读取最新端口）；退出请用托盘图标右键菜单。</p>
          <div class="settings-row">
            <button id="sysRestartBtn" type="button" class="button primary">重启服务</button>
          </div>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'sysRunningPort', 'sysPortInput', 'sysSavePortBtn',
      'sysAutostartToggle',
      'sysMcpScope', 'sysRegisterMcpBtn', 'sysMcpUrl',
      'sysVersion', 'sysCheckUpdateBtn', 'sysDownloadUpdateBtn', 'sysApplyUpdateBtn', 'sysUpdateHint',
      'sysUpdateProgress', 'sysUpdateProgressFill', 'sysUpdateProgressText',
      'sysRestartBtn'
    ];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  function bindValues() {
    el.sysRunningPort.value = state.runningPort ?? '';
    el.sysPortInput.value = state.configPort ?? '';
    el.sysAutostartToggle.checked = Boolean(state.autostart);
    el.sysMcpUrl.textContent = state.runningPort ? `http://127.0.0.1:${state.runningPort}/mcp` : '';
  }

  async function loadSystem() {
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.loadConfig(); // 初始化本机会话 cookie
      const port = await window.adminApi.requestJson('/admin/api/port');
      state.runningPort = port.runningPort;
      state.configPort = port.configPort;
      const auto = await window.adminApi.requestJson('/admin/api/autostart');
      state.autostart = Boolean(auto.enabled);
      bindValues();
      await loadUpdate();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  async function savePort() {
    const raw = (el.sysPortInput.value || '').trim();
    const port = Number(raw);
    if (!raw || !Number.isInteger(port) || port < 1 || port > 65535) {
      window.adminUi.showToast('端口必须为 1-65535 的整数。', true);
      return;
    }
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.requestJson('/admin/api/port', {
        method: 'PUT',
        body: JSON.stringify({ port })
      });
      state.configPort = port;
      window.adminUi.showToast(`端口已保存为 ${port}，重启后生效。MCP 客户端需改连 http://127.0.0.1:${port}/mcp`);
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  async function toggleAutostart() {
    const enabled = el.sysAutostartToggle.checked;
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/autostart', {
        method: 'PUT',
        body: JSON.stringify({ enabled })
      });
      state.autostart = Boolean(result.enabled);
      el.sysAutostartToggle.checked = state.autostart;
      window.adminUi.showToast(state.autostart ? '已启用登录自启动。' : '已关闭登录自启动。');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
      el.sysAutostartToggle.checked = state.autostart; // 失败回滚开关视觉态
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  async function registerMcp() {
    const scope = el.sysMcpScope.value || 'user';
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/register-mcp', {
        method: 'POST',
        body: JSON.stringify({ scope })
      });
      window.adminUi.showToast(`已注册到 Claude Code（${scope}）：${result.url}`);
    } catch (error) {
      window.adminUi.showToast(`MCP 注册失败：${error.message}`, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  // ============ 应用更新（Velopack）============

  async function loadUpdate() {
    try {
      const s = await window.adminApi.requestJson('/admin/api/update/status');
      bindUpdate(s);
    } catch (error) {
      el.sysUpdateHint.textContent = `更新状态加载失败：${error.message}`;
    }
  }

  function bindUpdate(s) {
    // /update/check、/download 返回的字段不含 currentVersion/进度字段，合并保留
    state.update = { ...state.update, ...s };
    const u = state.update;
    el.sysVersion.textContent = u.currentVersion || '?';

    // 下载中 = 本地点击未返回 或 服务端轮询到 downloadInProgress
    const downloading = state.downloading || Boolean(u.downloadInProgress);
    let hint;
    if (!u.configured) {
      hint = '未配置更新源（UpdateSource）。需运维设置后才能检查更新。';
    } else if (!u.installed) {
      hint = '当前为开发模式运行（非 Velopack 安装包），无法检查更新。安装正式版后可用。';
    } else if (!u.checked) {
      hint = '尚未自动检查更新，点"检查更新"。';
    } else if (u.error && !downloading) {
      hint = `检查失败：${u.error}`;
    } else if (downloading) {
      hint = `正在下载新版本 ${u.targetVersion || ''}…`;
    } else if (u.downloaded) {
      hint = `已下载新版本 ${u.targetVersion}，点"安装并重启"应用。`;
    } else if (u.hasUpdate) {
      hint = `发现新版本 ${u.targetVersion}，点"下载"。`;
    } else {
      hint = '已是最新版本。';
    }
    el.sysUpdateHint.textContent = hint;

    renderProgress(downloading, u.downloadPercent || 0);

    // 下载中禁用全部更新按钮（setBusy 只禁顶栏按钮，覆盖不到本卡片）
    const canCheck = u.configured && u.installed && !downloading;
    el.sysCheckUpdateBtn.disabled = !canCheck;
    el.sysDownloadUpdateBtn.disabled = !(canCheck && u.hasUpdate && !u.downloaded);
    el.sysApplyUpdateBtn.disabled = !u.downloaded || downloading;
  }

  /** 渲染下载进度条：show=false 隐藏；percent 取 0-100。 */
  function renderProgress(show, percent) {
    el.sysUpdateProgress.hidden = !show;
    if (!show) {
      return;
    }
    const pct = Math.max(0, Math.min(100, Math.round(percent)));
    el.sysUpdateProgressFill.style.width = `${pct}%`;
    el.sysUpdateProgressText.textContent = `${pct}%`;
  }

  async function checkUpdate() {
    window.adminUi.setBusy(true);
    try {
      const s = await window.adminApi.requestJson('/admin/api/update/check', { method: 'POST' });
      bindUpdate(s);
      window.adminUi.showToast(state.update.hasUpdate
        ? `发现新版本 ${state.update.targetVersion}`
        : '已是最新版本');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  /** 启动下载进度轮询：500ms 拉 /update/status，读 downloadInProgress/downloadPercent 刷新进度条。 */
  function startProgressPolling() {
    stopProgressPolling();
    progressTimer = setInterval(async () => {
      try {
        const s = await window.adminApi.requestJson('/admin/api/update/status');
        bindUpdate(s);
      } catch (error) {
        // 单次轮询失败忽略（网络抖动），下一轮再试
      }
    }, 500);
  }

  function stopProgressPolling() {
    if (progressTimer !== null) {
      clearInterval(progressTimer);
      progressTimer = null;
    }
  }

  async function downloadUpdate() {
    state.downloading = true;
    bindUpdate(state.update); // 先渲染下载中态（0% 进度条 + 禁用按钮），进度轮询补真实百分比
    startProgressPolling();
    try {
      const s = await window.adminApi.requestJson('/admin/api/update/download', { method: 'POST' });
      bindUpdate(s);
      window.adminUi.showToast(s.downloaded ? '下载完成，可安装。' : (s.error || '下载未完成'));
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      state.downloading = false;
      stopProgressPolling();
      renderProgress(false, 0);
      bindUpdate(state.update); // 恢复按钮态（失败可重试；成功则"安装并重启"可用）
    }
  }

  async function applyUpdate() {
    const ok = await window.adminUi.confirmAction('安装并重启', '将应用已下载的更新并重启应用。');
    if (!ok) {
      return;
    }
    const oldVersion = state.update.currentVersion || el.sysVersion.textContent || '';
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.requestJson('/admin/api/update/apply', { method: 'POST' });
    } catch (error) {
      // 应用重启过程连接断开属正常（Velopack 接管退出）
    }
    window.adminUi.setBusy(false);
    el.sysUpdateHint.textContent = '正在应用更新并重启，完成后将自动刷新页面…';
    waitForUpgrade(oldVersion, 0);
  }

  /** 轮询 /admin/api/version 直到版本号变化（旧进程退出、新版本起来）自动刷新页面。
      以版本号变化而非"任意响应"判定完成：apply 响应返回后旧进程可能尚未退出。
      5 分钟超时（安装包替换 + 重启偶发较慢），超时提示手动刷新。 */
  function waitForUpgrade(oldVersion, elapsedMs) {
    if (elapsedMs >= 300000) {
      window.adminUi.showToast('等待服务恢复超时，请手动刷新页面查看状态。', true);
      return;
    }
    setTimeout(async () => {
      try {
        const v = await window.adminApi.requestJson('/admin/api/version');
        if (v.version && v.version !== oldVersion) {
          window.location.reload(); // 升级完成，自动刷新加载新版本页面
          return;
        }
        // 版本未变：旧进程尚未退出，或重启后仍是旧版本，继续等待
      } catch (error) {
        // 服务尚未恢复（连接拒绝/中断），继续等待
      }
      waitForUpgrade(oldVersion, elapsedMs + 2000);
    }, 2000);
  }

  async function restart() {
    const portChanged = state.configPort && state.configPort !== state.runningPort;
    const hint = portChanged
      ? `将以新端口 ${state.configPort} 启动。`
      : '将按当前配置重新启动。';
    const mcpHint = state.configPort
      ? ` MCP 客户端需改连：http://127.0.0.1:${state.configPort}/mcp`
      : '';
    const ok = await window.adminUi.confirmAction('重启服务', hint + mcpHint, '重启');
    if (!ok) {
      return;
    }
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.requestJson('/admin/api/restart', { method: 'POST' });
    } catch (error) {
      // 重启过程连接断开属正常（旧实例退出）
    }
    window.adminUi.setBusy(false);
    window.adminUi.showToast(portChanged
      ? `正在重启，新地址 http://127.0.0.1:${state.configPort}/admin，请稍候手动访问。`
      : '正在重启，请稍候刷新页面。');
  }

  function bindEvents() {
    el.sysSavePortBtn.addEventListener('click', savePort);
    el.sysAutostartToggle.addEventListener('change', toggleAutostart);
    el.sysRegisterMcpBtn.addEventListener('click', registerMcp);
    el.sysCheckUpdateBtn.addEventListener('click', checkUpdate);
    el.sysDownloadUpdateBtn.addEventListener('click', downloadUpdate);
    el.sysApplyUpdateBtn.addEventListener('click', applyUpdate);
    el.sysRestartBtn.addEventListener('click', restart);
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.system = {
    title: '系统设置',
    eyebrow: 'System',
    saveLabel: '', // 只读视图：各 section 独立按钮操作，隐藏顶栏保存按钮

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindValues();
      bindEvents();
    },

    onEnter() {
      loadSystem();
    },

    onLeave() {
      // 停止下载进度轮询（视图卸载后进度条 DOM 已失效）；
      // 升级等待轮询（waitForUpgrade）不清理——升级完成后刷新整页是全局期望行为
      stopProgressPolling();
    }
  };
})();
