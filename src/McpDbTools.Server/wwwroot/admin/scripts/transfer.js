/* 配置迁移视图（SPA 视图模块）。
   负责：项目配置的多选导出（JSON 文件）、文件/粘贴导入（预览 + 确认应用）。
   数据流：
     导出：复选框选项目 → 前端映射成 databases 片段 → Blob 下载（无后端往返）。
     导入：选文件/粘贴 → POST /admin/api/projects/import-preview → 渲染计划+校验问题
           → POST /admin/api/projects/import-apply（重新合并校验 + 原子落盘 + 自动备份）。
   只读视图：不持有需保存的编辑态，不实现 save/isDirty/confirmLeave；shell 隐藏保存按钮。
   公共能力（toast/busy/转义）来自 window.adminUi。 */
(function () {
  // 视图内部 state：config 为 GET /admin/api/config 的 DTO；selected 为勾选的项目 key 集合；
  // preview 为最近一次预览响应（含 plan + errors），供「确认导入」按钮判断是否可应用。
  const state = {
    config: null,
    selected: new Set(),
    preview: null
  };
  let el = null;

  /** 导出文件名时间戳：YYYYMMDD-HHmmss（本地时区，仅用于文件名）。 */
  function exportTimestamp() {
    const d = new Date();
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}` +
      `-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
  }

  /** AdminEnvironmentDto → 导出文件的环境对象（剥离 originalName/connectionStringMasked，省略空可选字段）。 */
  function toExportEnv(e) {
    const o = {
      type: e.type,
      connectionString: e.connectionString || ''
    };
    if (e.isProduction) o.isProduction = true;
    if (e.allowWrite) o.allowWrite = true;
    if (e.displayName) o.displayName = e.displayName;
    o.maxRows = e.maxRows;
    o.commandTimeout = e.commandTimeout;
    if (e.maxPoolSize) o.maxPoolSize = e.maxPoolSize;
    if (e.connectTimeoutSeconds) o.connectTimeoutSeconds = e.connectTimeoutSeconds;
    if (e.maxConcurrency) o.maxConcurrency = e.maxConcurrency;
    if (Array.isArray(e.disabledKeywords) && e.disabledKeywords.length) o.disabledKeywords = e.disabledKeywords;
    return o;
  }

  /** AdminProjectDto 列表 + 选中 key → { databases: { key: { defaultEnvironment, displayName, environments } } }。 */
  function toExportDatabases(projects, selected) {
    const dbs = {};
    for (const p of projects) {
      if (!selected.has(p.name)) continue;
      const envs = {};
      for (const e of (p.environments || [])) {
        envs[e.name] = toExportEnv(e);
      }
      const entry = { environments: envs };
      if (p.defaultEnvironment) entry.defaultEnvironment = p.defaultEnvironment;
      if (p.displayName) entry.displayName = p.displayName;
      dbs[p.name] = entry;
    }
    return { databases: dbs };
  }

  function downloadJsonText(text, filename) {
    const blob = new Blob([text], { type: 'application/json;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  function template() {
    return `
      <div class="shell transfer-view">
        <section class="card">
          <div class="card-title"><h2>导出<span class="eyebrow">Export</span></h2></div>
          <div class="transfer-toolbar">
            <label class="switch-row">
              <input id="selectAll" type="checkbox" />
              <span>全选</span>
            </label>
          </div>
          <div id="exportList" class="transfer-list"></div>
          <div class="transfer-actions">
            <button id="exportBtn" type="button" class="button primary" disabled>导出选中项目</button>
            <span class="warning inline">⚠ 导出文件含明文连接字符串，请注意保管。</span>
          </div>
        </section>

        <section class="card">
          <div class="card-title"><h2>导入<span class="eyebrow">Import</span></h2></div>
          <div class="transfer-import">
            <div class="transfer-file">
              <input id="importFile" type="file" accept=".json,application/json" />
            </div>
            <label class="full">
              <span>或粘贴 JSON</span>
              <textarea id="importText" rows="10" spellcheck="false" placeholder='{ "databases": { ... } }'></textarea>
            </label>
            <div class="transfer-actions">
              <button id="previewBtn" type="button" class="button secondary">预览导入</button>
            </div>
            <div id="importReport" class="transfer-report hidden"></div>
            <div class="transfer-actions">
              <button id="applyBtn" type="button" class="button primary" disabled>确认导入</button>
              <button id="cancelBtn" type="button" class="button ghost hidden">取消</button>
            </div>
          </div>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'selectAll', 'exportList', 'exportBtn',
      'importFile', 'importText', 'previewBtn',
      'importReport', 'applyBtn', 'cancelBtn'
    ];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  async function loadConfig() {
    window.adminUi.setBusy(true);
    try {
      state.config = await window.adminApi.loadConfig();
      state.selected = new Set();
      renderExportList();
      window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      window.adminUi.showToast('配置已加载');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function formatLabel(key, displayName) {
    const safeKey = window.adminUi.escapeHtml(key || '');
    const name = displayName ? String(displayName).trim() : '';
    return name ? `${safeKey}(${window.adminUi.escapeHtml(name)})` : safeKey;
  }

  function renderExportList() {
    const projects = (state.config && state.config.projects) || [];
    el.exportList.innerHTML = '';
    projects.forEach(p => {
      const id = `proj-${p.name}`;
      const wrap = document.createElement('label');
      wrap.className = 'transfer-item';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.id = id;
      cb.checked = state.selected.has(p.name);
      cb.addEventListener('change', () => {
        if (cb.checked) state.selected.add(p.name);
        else state.selected.delete(p.name);
        syncSelectAllState();
        el.exportBtn.disabled = state.selected.size === 0;
      });
      const span = document.createElement('span');
      span.innerHTML = `<strong>${formatLabel(p.name, p.displayName)}</strong>` +
        `<em>${(p.environments || []).length} 个环境</em>`;
      wrap.appendChild(cb);
      wrap.appendChild(span);
      el.exportList.appendChild(wrap);
    });
    syncSelectAllState();
    el.exportBtn.disabled = state.selected.size === 0;
  }

  function syncSelectAllState() {
    const projects = (state.config && state.config.projects) || [];
    const total = projects.length;
    const checked = projects.filter(p => state.selected.has(p.name)).length;
    el.selectAll.checked = total > 0 && checked === total;
    el.selectAll.indeterminate = checked > 0 && checked < total;
  }

  function toggleSelectAll() {
    const projects = (state.config && state.config.projects) || [];
    if (el.selectAll.checked) {
      projects.forEach(p => state.selected.add(p.name));
    } else {
      state.selected.clear();
    }
    renderExportList();
  }

  function exportSelected() {
    const projects = (state.config && state.config.projects) || [];
    if (state.selected.size === 0) {
      window.adminUi.showToast('请至少选择一个项目', true);
      return;
    }
    const obj = toExportDatabases(projects, state.selected);
    const text = JSON.stringify(obj, null, 2);
    downloadJsonText(text, `mcpdb-projects-${exportTimestamp()}.json`);
    window.adminUi.showToast(`已导出 ${state.selected.size} 个项目`);
  }

  /** 文件选择：读为文本填进 textarea，与粘贴等价（用户可继续编辑）。限制 1MB。 */
  function onFileChange() {
    const file = el.importFile.files && el.importFile.files[0];
    if (!file) return;
    if (file.size > 1024 * 1024) {
      window.adminUi.showToast('文件超过 1MB，请检查或拆分', true);
      el.importFile.value = '';
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      el.importText.value = typeof reader.result === 'string' ? reader.result : '';
    };
    reader.onerror = () => window.adminUi.showToast('读取文件失败', true);
    reader.readAsText(file, 'utf-8');
  }

  async function previewImport() {
    const json = el.importText.value.trim();
    if (!json) {
      window.adminUi.showToast('请粘贴或选择 JSON 文件', true);
      return;
    }
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/projects/import-preview', {
        method: 'POST',
        body: JSON.stringify({ json })
      });
      state.preview = result;
      renderReport(result);
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function renderReport(result) {
    const plan = result.plan || {};
    const errors = result.errors || [];
    const hasChange =
      (plan.addedProjects && plan.addedProjects.length) ||
      (plan.updatedProjects && plan.updatedProjects.length) ||
      (plan.addedEnvironments && plan.addedEnvironments.length) ||
      (plan.updatedEnvironments && plan.updatedEnvironments.length);
    const canApply = errors.length === 0 && hasChange;

    const esc = window.adminUi.escapeHtml;
    const li = arr => (arr && arr.length ? arr.map(x => `<li>${esc(x)}</li>`).join('') : '<li class="muted">无</li>');

    el.importReport.classList.remove('hidden');
    el.importReport.innerHTML = `
      <div class="report-section">
        <h3>合并计划（解析 ${result.parsedProjectCount ?? 0} 个项目）</h3>
        <div class="report-grid">
          <div><h4>新增项目</h4><ul>${li(plan.addedProjects)}</ul></div>
          <div><h4>更新项目</h4><ul>${li(plan.updatedProjects)}</ul></div>
          <div><h4>新增环境</h4><ul>${li(plan.addedEnvironments)}</ul></div>
          <div><h4>更新环境</h4><ul>${li(plan.updatedEnvironments)}</ul></div>
        </div>
      </div>
      ${errors.length ? `
        <div class="report-section report-errors">
          <h3>⚠ 校验问题（${errors.length}）</h3>
          <ul>${errors.map(e => `<li>${esc(e)}</li>`).join('')}</ul>
          <p class="muted">修正文件后重新预览；存在校验问题时不可应用。</p>
        </div>` : ''}
    `;

    el.applyBtn.disabled = !canApply;
    el.applyBtn.textContent = canApply ? '确认导入' : (errors.length ? '存在校验问题' : '无变更');
    el.cancelBtn.classList.toggle('hidden', !hasChange && !errors.length);
  }

  async function applyImport() {
    const json = el.importText.value.trim();
    if (!json) return;
    // 二次确认：直接落盘操作
    const ok = await window.adminUi.confirmAction('确认导入', '将按上述计划合并并写入 config.json（自动产生备份，可在备份管理回退）。确认继续？');
    if (!ok) return;
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/projects/import-apply', {
        method: 'POST',
        body: JSON.stringify({ json })
      });
      window.adminUi.showToast(`导入成功，备份：${result.backupName}（可在备份管理回退）`);
      // 清空并重载：导入已落盘，重新拉取以反映合并结果
      el.importText.value = '';
      el.importFile.value = '';
      state.preview = null;
      el.importReport.classList.add('hidden');
      el.importReport.innerHTML = '';
      el.applyBtn.disabled = true;
      el.cancelBtn.classList.add('hidden');
      await loadConfig();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function cancelImport() {
    state.preview = null;
    el.importReport.classList.add('hidden');
    el.importReport.innerHTML = '';
    el.applyBtn.disabled = true;
    el.applyBtn.textContent = '确认导入';
    el.cancelBtn.classList.add('hidden');
  }

  function bindEvents() {
    el.selectAll.addEventListener('change', toggleSelectAll);
    el.exportBtn.addEventListener('click', exportSelected);
    el.importFile.addEventListener('change', onFileChange);
    el.previewBtn.addEventListener('click', previewImport);
    el.applyBtn.addEventListener('click', applyImport);
    el.cancelBtn.addEventListener('click', cancelImport);
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.transfer = {
    title: '配置迁移',
    eyebrow: 'Local Admin',

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindEvents();
    },

    onEnter() {
      if (!state.config) {
        loadConfig();
      } else {
        renderExportList();
        window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      }
    },

    onLeave() {
      // 无编辑态需同步；保留 state 便于切回
    }
  };
})();
