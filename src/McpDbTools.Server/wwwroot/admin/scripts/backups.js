/* 备份管理视图（SPA 视图模块）。
   负责：列出 config.json 的备份（每次保存自动生成）、下载、恢复、删除。
   数据流：GET /admin/api/backups → 列表；下载/恢复/删除各自对应 API。
   只读为主：不设 saveLabel → shell 隐藏保存按钮。
   视图接口：mount(container) / onEnter() / reload() */
(function () {
  const state = {
    list: null
  };
  let el = null;

  function template() {
    return `
      <div class="shell single">
        <section class="card">
          <div class="card-title">
            <div>
              <h2>备份管理</h2>
              <p id="backupMeta" class="muted">每次保存配置会自动生成一份备份。加载中…</p>
            </div>
            <button id="refreshBtn" type="button" class="button secondary">刷新</button>
          </div>
          <div class="audit-table-wrap">
            <table class="audit-table backup-table">
              <thead>
                <tr>
                  <th>备份文件</th>
                  <th>生成时间</th>
                  <th>大小</th>
                  <th class="col-actions">操作</th>
                </tr>
              </thead>
              <tbody id="backupBody">
                <tr class="audit-empty-row"><td colspan="4">暂无备份</td></tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = ['backupMeta', 'refreshBtn', 'backupBody'];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  /** 把字节数格式化为人类可读。 */
  function formatSize(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
  }

  /** UTC ISO 渲染成本地时间。 */
  function isoToLocal(iso) {
    if (!iso) return '';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
  }

  async function load() {
    window.adminUi.setBusy(true);
    try {
      state.list = await window.adminApi.requestJson('/admin/api/backups');
      render();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function render() {
    const list = state.list;
    if (!list) return;
    const items = list.items || [];
    el.backupMeta.textContent = items.length > 0
      ? `共 ${items.length} 个备份，目录：${list.directory}`
      : '暂无备份。每次保存配置会自动生成一份备份。';

    if (items.length === 0) {
      el.backupBody.innerHTML = '<tr class="audit-empty-row"><td colspan="4">暂无备份</td></tr>';
      return;
    }

    el.backupBody.innerHTML = '';
    for (const item of items) {
      el.backupBody.appendChild(renderRow(item));
    }
  }

  function renderRow(item) {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="cell-name">${window.adminUi.escapeHtml(item.name)}</td>
      <td class="cell-time">${window.adminUi.escapeHtml(isoToLocal(item.time))}</td>
      <td>${formatSize(item.sizeBytes)}</td>
      <td class="col-actions">
        <button type="button" class="button secondary row-action" data-action="download">下载</button>
        <button type="button" class="button primary row-action" data-action="restore">恢复</button>
        <button type="button" class="button danger subtle row-action" data-action="delete">删除</button>
      </td>
    `;

    tr.querySelector('[data-action="download"]').addEventListener('click', () => download(item));
    tr.querySelector('[data-action="restore"]').addEventListener('click', () => restore(item));
    tr.querySelector('[data-action="delete"]').addEventListener('click', () => remove(item));
    return tr;
  }

  function download(item) {
    // 走原生 a 标签触发下载，浏览器自动处理；带 cookie 凭证
    const a = document.createElement('a');
    a.href = `/admin/api/backups/download?name=${encodeURIComponent(item.name)}`;
    a.download = item.name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  }

  async function restore(item) {
    const ok = await window.adminUi.confirmAction(
      '恢复备份',
      `确定将配置恢复到备份「${item.name}」吗？恢复前会自动把当前配置另存为一份新备份，可再次撤销。`
    );
    if (!ok) return;

    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/backups/restore', {
        method: 'POST',
        body: JSON.stringify({ name: item.name })
      });
      window.adminUi.showToast(`已恢复到「${item.name}」，当前配置已存为「${result.snapshotName}」`);
      await load();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  async function remove(item) {
    const ok = await window.adminUi.confirmAction(
      '删除备份',
      `确定删除备份「${item.name}」吗？此操作不可恢复。`
    );
    if (!ok) return;

    window.adminUi.setBusy(true);
    try {
      await window.adminApi.requestJson('/admin/api/backups/delete', {
        method: 'POST',
        body: JSON.stringify({ name: item.name })
      });
      window.adminUi.showToast(`已删除备份「${item.name}」`);
      await load();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  window.adminViews = window.adminViews || {};
  window.adminViews['backups'] = {
    title: '备份管理',
    eyebrow: 'Backups',
    saveLabel: '',

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      el.refreshBtn.addEventListener('click', load);
    },

    onEnter() {
      if (el) {
        load();
      }
    },

    reload() {
      load();
    }
  };
})();
