/* 全局设置视图（SPA 视图模块）。
   负责：审计日志/备份文件的自动清理开关与保留天数，以及手动清理两者。
   数据流：
     - 自动清理设置：GET /admin/api/maintenance → 表单 → PUT /admin/api/maintenance（仅改 maintenance 节点）。
     - 手动清理审计：POST /admin/api/audit-logs/cleanup，body {name: "<days>"}。
     - 手动清理备份：POST /admin/api/backups/cleanup，body {name: "<days>"}。
   视图接口：mount(container) / onEnter() / onLeave() / save() / reload()
   公共能力（toast/confirm/busy/转义）来自 window.adminUi / window.adminApi。 */
(function () {
  // 手动清理档位（按需求为 10/20/30/50）
  const CLEANUP_DAY_OPTIONS = [10, 20, 30, 50];

  const state = {
    settings: null,
    auditCleanupDays: 30,
    backupCleanupDays: 30
  };
  let el = null; // mount 后填充

  function template() {
    return `
      <div class="shell single settings-shell">
        <section class="card settings-card">
          <div class="card-title">
            <div>
              <p class="eyebrow">Audit Log</p>
              <h2>审计日志</h2>
            </div>
          </div>
          <p class="muted">开启后后台服务按保留天数自动删除过期审计记录；也可立即手动清理。</p>

          <div class="settings-row">
            <label class="switch-row">
              <input id="auditLogAutoCleanup" type="checkbox" />
              <span>自动删除</span>
            </label>
            <label class="retention-field">
              <span>保留天数</span>
              <input id="auditLogRetentionDays" type="number" min="1" step="1" placeholder="30" />
            </label>
          </div>

          <div class="settings-row">
            <label class="switch-row">
              <input id="auditRecordResults" type="checkbox" />
              <span>记录查询结果</span>
            </label>
            <p class="muted hint-inline">关闭时仅记录行数；开启后同时保存完整查询结果到子表，结果集可能很大，请关注 audit.db 体积。</p>
          </div>

          <div class="manual-cleanup">
            <span class="manual-cleanup__label">手动清理</span>
            <select id="auditCleanupDays">
              ${CLEANUP_DAY_OPTIONS.map(d => `<option value="${d}">${d} 天前</option>`).join('')}
            </select>
            <button id="auditCleanupBtn" type="button" class="button danger subtle">立即清理</button>
          </div>
        </section>

        <section class="card settings-card">
          <div class="card-title">
            <div>
              <p class="eyebrow">Backup</p>
              <h2>备份文件</h2>
            </div>
          </div>
          <p class="muted">开启后后台服务按保留天数自动删除过期备份（按文件时间判断）；也可立即手动清理。</p>

          <div class="settings-row">
            <label class="switch-row">
              <input id="backupAutoCleanup" type="checkbox" />
              <span>自动删除</span>
            </label>
            <label class="retention-field">
              <span>保留天数</span>
              <input id="backupRetentionDays" type="number" min="1" step="1" placeholder="30" />
            </label>
          </div>

          <div class="manual-cleanup">
            <span class="manual-cleanup__label">手动清理</span>
            <select id="backupCleanupDays">
              ${CLEANUP_DAY_OPTIONS.map(d => `<option value="${d}">${d} 天前</option>`).join('')}
            </select>
            <button id="backupCleanupBtn" type="button" class="button danger subtle">立即清理</button>
          </div>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'auditLogAutoCleanup', 'auditLogRetentionDays', 'auditRecordResults', 'auditCleanupDays', 'auditCleanupBtn',
      'backupAutoCleanup', 'backupRetentionDays', 'backupCleanupDays', 'backupCleanupBtn'
    ];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  async function loadSettings() {
    window.adminUi.setBusy(true);
    try {
      await window.adminApi.loadConfig(); // 初始化本机会话 cookie
      state.settings = await window.adminApi.requestJson('/admin/api/maintenance');
      bindSettings();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  /** 把后端返回的设置回填到表单。 */
  function bindSettings() {
    if (!state.settings) {
      return;
    }
    el.auditLogAutoCleanup.checked = Boolean(state.settings.auditLogAutoCleanup);
    el.auditLogRetentionDays.value = state.settings.auditLogRetentionDays || 30;
    el.auditRecordResults.checked = Boolean(state.settings.auditRecordResults);
    el.backupAutoCleanup.checked = Boolean(state.settings.backupAutoCleanup);
    el.backupRetentionDays.value = state.settings.backupRetentionDays || 30;
    updateRetentionDisabled();
  }

  /** 自动删除开关关闭时，对应天数输入禁用（不生效，避免误以为保存了）。 */
  function updateRetentionDisabled() {
    el.auditLogRetentionDays.disabled = !el.auditLogAutoCleanup.checked;
    el.backupRetentionDays.disabled = !el.backupAutoCleanup.checked;
  }

  /** 同步表单到内存 state，保留未保存的编辑内容。 */
  function syncFormToState() {
    if (!state.settings) {
      return;
    }
    state.settings.auditLogAutoCleanup = el.auditLogAutoCleanup.checked;
    state.settings.auditLogRetentionDays = Number(el.auditLogRetentionDays.value) || 30;
    state.settings.auditRecordResults = el.auditRecordResults.checked;
    state.settings.backupAutoCleanup = el.backupAutoCleanup.checked;
    state.settings.backupRetentionDays = Number(el.backupRetentionDays.value) || 30;
  }

  async function saveSettings() {
    syncFormToState();
    if (state.settings.auditLogAutoCleanup && state.settings.auditLogRetentionDays <= 0) {
      window.adminUi.showToast('审计日志自动删除已启用，保留天数必须大于 0', true);
      return;
    }
    if (state.settings.backupAutoCleanup && state.settings.backupRetentionDays <= 0) {
      window.adminUi.showToast('备份自动删除已启用，保留天数必须大于 0', true);
      return;
    }

    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/maintenance', {
        method: 'PUT',
        body: JSON.stringify({
          auditLogAutoCleanup: state.settings.auditLogAutoCleanup,
          auditLogRetentionDays: state.settings.auditLogRetentionDays,
          auditRecordResults: state.settings.auditRecordResults,
          backupAutoCleanup: state.settings.backupAutoCleanup,
          backupRetentionDays: state.settings.backupRetentionDays
        })
      });
      state.settings = result;
      bindSettings();
      window.adminUi.showToast('设置已保存');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  /** 通用手动清理：二次确认 + 调用指定接口 + toast 结果。 */
  async function runManualCleanup(endpoint, label, days) {
    const ok = await window.adminUi.confirmAction(
      `${label}清理`,
      `确定删除 ${days} 天前的所有${label}吗？此操作不可恢复。`
    );
    if (!ok) {
      return;
    }

    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson(endpoint, {
        method: 'POST',
        body: JSON.stringify({ name: String(days) })
      });
      window.adminUi.showToast(`已删除 ${result.deleted} 个 ${days} 天前的${label}`);
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function bindEvents() {
    el.auditLogAutoCleanup.addEventListener('change', updateRetentionDisabled);
    el.backupAutoCleanup.addEventListener('change', updateRetentionDisabled);
    el.auditCleanupDays.addEventListener('change', () => { state.auditCleanupDays = Number(el.auditCleanupDays.value); });
    el.backupCleanupDays.addEventListener('change', () => { state.backupCleanupDays = Number(el.backupCleanupDays.value); });
    el.auditCleanupBtn.addEventListener('click', () => {
      runManualCleanup('/admin/api/audit-logs/cleanup', '审计记录', Number(el.auditCleanupDays.value));
    });
    el.backupCleanupBtn.addEventListener('click', () => {
      runManualCleanup('/admin/api/backups/cleanup', '备份文件', Number(el.backupCleanupDays.value));
    });
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.settings = {
    title: '全局设置',
    eyebrow: 'Settings',
    saveLabel: '保存设置',

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      // 回填用户上次选的手动清理档位（跨视图切换保留）
      if (state.auditCleanupDays && [...el.auditCleanupDays.options].some(o => Number(o.value) === state.auditCleanupDays)) {
        el.auditCleanupDays.value = String(state.auditCleanupDays);
      }
      if (state.backupCleanupDays && [...el.backupCleanupDays.options].some(o => Number(o.value) === state.backupCleanupDays)) {
        el.backupCleanupDays.value = String(state.backupCleanupDays);
      }
      bindEvents();
    },

    onEnter() {
      if (!state.settings) {
        loadSettings();
      } else {
        bindSettings();
      }
    },

    onLeave() {
      syncFormToState();
    },

    save: saveSettings,
    reload: loadSettings
  };
})();
