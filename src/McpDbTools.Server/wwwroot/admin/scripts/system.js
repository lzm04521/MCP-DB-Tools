/* 系统设置视图（SPA 视图模块）。
   负责：服务端口配置（改后重启生效）、登录自启动开关、Claude Code MCP 注册、应用重启。
   应用更新已迁至「关于」页（scripts/about.js，一键更新流）。
   数据流：
     - 端口：GET /admin/api/port（runningPort + configPort）→ 输入框 → PUT /admin/api/port → toast 提示重启
     - 自启动：GET /admin/api/autostart → 开关 → PUT /admin/api/autostart
     - MCP 注册：POST /admin/api/register-mcp {scope}（用当前 runningPort）
     - 重启：POST /admin/api/restart（confirmAction 确认；旧实例退出，新实例起来）
   只读视图：各 section 独立按钮操作，不使用顶栏"保存配置"（saveLabel 为空，shell 自动隐藏保存按钮）。
   公共能力（toast/confirm/busy）来自 window.adminUi / window.adminApi。 */
(function () {
  const state = {
    runningPort: null,
    configPort: null,
    autostart: false
  };
  let el = null; // mount 后填充

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

  // ============ 应用更新（Velopack）：已迁至「关于」页（scripts/about.js） ============

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
      // 应用更新的进度轮询已随功能迁至「关于」页，本视图无后台任务需要清理
    }
  };
})();
