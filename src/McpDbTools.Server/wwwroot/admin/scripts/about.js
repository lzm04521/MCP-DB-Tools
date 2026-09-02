/* 关于视图（SPA 视图模块，只读）。
   负责：当前版本展示 + 应用更新（自系统设置页迁来并重做为一键流）。
   数据流：
     - GET /admin/api/update/status（currentVersion + 缓存检查结果含 checkedAtUtc）→ 版本行 + 检查按钮初始态
       （hint 显示上次检查时间，可辨"已是最新"是实时结果还是最多 1 小时前的缓存）
     - POST /admin/api/update/check → 展开结果区（最新版本 + 状态徽章 + 一键更新 + 更新说明原文）；
       检查失败时结果区显示红色"检查失败"徽章，不保留上次成功的徽章误导
     - 一键更新：POST /download（期间 500ms 轮询 /status 刷新进度条）→ 下载完成自动 POST /apply
       （Velopack 接管重启；轮询 /version 直到版本变化自动刷新页面），无"下载完等手动安装"中间态
   版本显示统一 v 前缀且只加一次（后端 /version 与 targetVersion 均不带 v）。
   更新说明（Notes）只经 textContent 写入空 <pre>，不拼 innerHTML（零注入面）。
   只读视图：无 save/saveLabel（顶栏保存按钮自动隐藏）；onLeave 停进度轮询。
   公共能力（toast/confirm/busy）来自 window.adminUi / window.adminApi。 */
(function () {
  const state = {
    update: { currentVersion: null, configured: false, installed: false, checked: false, checkedAtUtc: null, hasUpdate: false, targetVersion: null, downloaded: false, error: null, downloadInProgress: false, downloadPercent: 0, notes: null, releaseUrl: null },
    checkedOnce: false, // 检查成功后置位：控制结果区展开（/update/status 不含 notes，缓存态不展开）
    downloading: false, // 本地下载中标志（POST /download 未返回期间，先于服务端 downloadInProgress 置位）
    applying: false // 已进入应用更新阶段（apply 后禁用操作，等待重启）
  };
  let el = null; // mount 后填充
  let progressTimer = null; // 下载进度轮询句柄

  function template() {
    return `
      <div class="shell single settings-shell">
        <section class="card settings-card">
          <div class="card-title"><div><h2>应用更新<span class="eyebrow">Update</span></h2></div></div>
          <div class="about-row">
            <span class="about-row__label">当前版本</span>
            <span id="aboutVersion" class="about-row__value">?</span>
            <button id="aboutCheckBtn" type="button" class="button primary" disabled>检查更新</button>
          </div>
          <p class="muted hint-inline" id="aboutHint"></p>

          <div id="aboutResult" class="about-result" hidden>
            <div class="about-row">
              <span class="about-row__label">最新版本</span>
              <span id="aboutTargetVersion" class="about-row__value">?</span>
              <span id="aboutBadge" class="pill"></span>
              <button id="aboutOneClickBtn" type="button" class="button primary" hidden>一键更新</button>
            </div>
            <div id="aboutProgress" class="update-progress" hidden>
              <div class="update-progress-track"><div id="aboutProgressFill" class="update-progress-fill"></div></div>
              <span id="aboutProgressText" class="update-progress-text">0%</span>
            </div>
            <pre id="aboutNotes" class="about-notes"></pre>
          </div>

          <p class="muted about-flow">升级流程：点击「检查更新」获取最新版本 → 一键更新自动下载新版本 → 下载完成后自动安装并重启应用，页面将自动刷新。</p>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'aboutVersion', 'aboutCheckBtn', 'aboutHint',
      'aboutResult', 'aboutTargetVersion', 'aboutBadge', 'aboutOneClickBtn',
      'aboutProgress', 'aboutProgressFill', 'aboutProgressText', 'aboutNotes'
    ];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  /** 版本显示：后端值不带 v 前缀，此处统一加且只加一次（避免 vv 双前缀）。 */
  function displayVersion(v) {
    return v ? `v${v}` : '?';
  }

  /** UTC ISO 渲染成本地时间（与 backups.js 同实现，视图模块各自独立）。 */
  function isoToLocal(iso) {
    if (!iso) return '';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
  }

  async function loadAbout() {
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.loadConfig(); // 初始化本机会话 cookie
      const s = await window.adminApi.requestJson('/admin/api/update/status');
      bindUpdate(s);
    } catch (error) {
      el.aboutHint.textContent = `更新状态加载失败：${error.message}`;
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function bindUpdate(s) {
    // /update/check、/download 返回的字段不含 currentVersion/进度字段，合并保留
    state.update = { ...state.update, ...s };
    const u = state.update;
    el.aboutVersion.textContent = displayVersion(u.currentVersion);

    // 下载中 = 本地点击未返回 或 服务端轮询到 downloadInProgress
    const downloading = state.downloading || Boolean(u.downloadInProgress);

    // 检查按钮：未配置源/开发态禁用；下载与应用阶段禁用（setBusy 只禁顶栏按钮，覆盖不到本卡片）
    const canCheck = u.configured && u.installed && !downloading && !state.applying;
    el.aboutCheckBtn.disabled = !canCheck;

    let hint;
    if (!u.configured) {
      hint = '未配置更新源（UpdateGithubRepo）。需运维设置后才能检查更新。';
    } else if (!u.installed) {
      hint = '当前为开发模式运行（非 Velopack 安装包），无法检查更新。安装正式版后可用。';
    } else if (state.applying) {
      hint = '正在应用更新并重启，完成后将自动刷新页面…';
    } else if (downloading) {
      hint = `正在下载新版本 ${u.targetVersion || ''}…`;
    } else if (u.error) {
      hint = `检查失败：${u.error}`;
    } else if (u.downloaded && !state.checkedOnce) {
      // 边界恢复：上个会话已下载但未走到重启（如中途离开页面）——重新检查后可一键安装
      hint = `已下载新版本 ${u.targetVersion || ''}，点"检查更新"后可一键安装。`;
    } else if (u.checkedAtUtc) {
      // 已检查过（手动或自动缓存）：显示检查时间，让"已是最新"可辨新旧（缓存最多滞后 1 小时）
      hint = `上次检查：${isoToLocal(u.checkedAtUtc)}`;
    } else if (u.configured && u.installed) {
      hint = '尚未自动检查更新，可点击"检查更新"。';
    } else {
      hint = '';
    }
    el.aboutHint.textContent = hint;

    if (state.checkedOnce) {
      renderResult(u, downloading);
    }
  }

  /** 渲染检查结果区：最新版本 + 状态徽章 + 一键更新按钮 + 进度条 + 更新说明。 */
  function renderResult(u, downloading) {
    el.aboutResult.hidden = false;

    // 检查失败态：显示红色失败徽章，不渲染上次成功的"已是最新/有新版本"误导信息
    if (u.error) {
      el.aboutTargetVersion.textContent = '—';
      el.aboutBadge.textContent = '检查失败';
      el.aboutBadge.className = 'pill pill--danger';
      el.aboutOneClickBtn.hidden = true;
      renderProgress(false, 0);
      el.aboutNotes.textContent = '';
      return;
    }

    // 已是最新时 latest 即当前版本
    el.aboutTargetVersion.textContent = displayVersion(u.hasUpdate ? u.targetVersion : u.currentVersion);

    // 徽章：有新版本（蓝，.pill 默认）/ 已是最新（灰，.pill--muted）
    el.aboutBadge.textContent = u.hasUpdate ? '有新版本' : '已是最新';
    el.aboutBadge.className = u.hasUpdate ? 'pill' : 'pill pill--muted';

    // 一键更新：发现新版本且未下载、不在下载中/应用中时可见
    const oneClick = u.hasUpdate && !u.downloaded && !downloading && !state.applying;
    el.aboutOneClickBtn.hidden = !oneClick;

    renderProgress(downloading, u.downloadPercent || 0);

    // 更新说明原文：textContent 写入（不拼 innerHTML），空时给占位
    el.aboutNotes.textContent = u.notes || '（本次发布无更新说明）';
  }

  /** 渲染下载进度条：show=false 隐藏；percent 取 0-100。 */
  function renderProgress(show, percent) {
    el.aboutProgress.hidden = !show;
    if (!show) {
      return;
    }
    const pct = Math.max(0, Math.min(100, Math.round(percent)));
    el.aboutProgressFill.style.width = `${pct}%`;
    el.aboutProgressText.textContent = `${pct}%`;
  }

  async function checkUpdate() {
    window.adminUi.setBusy(true);
    try {
      const s = await window.adminApi.requestJson('/admin/api/update/check', { method: 'POST' });
      if (!s.error) {
        state.checkedOnce = true; // 检查成功才展开结果区（失败保留 hint 提示）
      }
      bindUpdate(s);
      if (!s.error) {
        window.adminUi.showToast(s.hasUpdate
          ? `发现新版本 ${displayVersion(s.targetVersion)}`
          : '已是最新版本');
      }
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

  /** 一键更新：下载（进度轮询）→ 下载完成自动 apply 重启，无"下载完等手动安装"中间态。 */
  async function oneClickUpdate() {
    if (state.downloading || state.applying) {
      return;
    }
    state.downloading = true;
    bindUpdate(state.update); // 先渲染下载中态（0% 进度条 + 隐藏一键按钮），进度轮询补真实百分比
    startProgressPolling();
    try {
      const s = await window.adminApi.requestJson('/admin/api/update/download', { method: 'POST' });
      bindUpdate(s);
      if (!s.downloaded && s.error) {
        window.adminUi.showToast(`下载失败：${s.error}`, true);
      }
    } catch (error) {
      window.adminUi.showToast(`下载失败：${error.message}`, true);
    } finally {
      state.downloading = false;
      stopProgressPolling();
      // POST /download 响应不含进度字段，state 里残留的是最后一次轮询的陈旧值
      // （downloadInProgress=true），不清除会导致 UI 永久卡在"下载中"
      // （进度条卡旧百分比、按钮全隐藏，轮询已停再无刷新机制）。
      state.update.downloadInProgress = false;
      if (state.update.downloaded) {
        state.update.downloadPercent = 100;
        await applyUpdate(); // 下载完成自动应用（一键流的第二段）
      } else {
        bindUpdate(state.update); // 恢复按钮态（失败可重试一键更新）
      }
    }
  }

  /** 应用已下载的更新并重启（一键流内自动触发，不再单独确认）。 */
  async function applyUpdate() {
    state.applying = true;
    bindUpdate(state.update);
    const oldVersion = state.update.currentVersion || '';
    try {
      await window.adminApi.requestJson('/admin/api/update/apply', { method: 'POST' });
    } catch (error) {
      // 应用重启过程连接断开属正常（Velopack 接管退出）
    }
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

  function bindEvents() {
    el.aboutCheckBtn.addEventListener('click', checkUpdate);
    el.aboutOneClickBtn.addEventListener('click', oneClickUpdate);
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.about = {
    title: '关于',
    eyebrow: 'About',
    // 只读视图：无 save/saveLabel，顶栏保存按钮自动隐藏

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindEvents();
    },

    onEnter() {
      loadAbout();
    },

    onLeave() {
      // 停止下载进度轮询（视图卸载后进度条 DOM 已失效）；
      // 升级等待轮询（waitForUpgrade）不清理——升级完成后刷新整页是全局期望行为
      stopProgressPolling();
    }
  };
})();
