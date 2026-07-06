/* 审计日志查看视图（SPA 视图模块）。
   负责：按字段筛选审计日志、分页浏览、点击查看完整 SQL（含复制）。
   数据流：GET /admin/api/audit-logs?... → 表格渲染 → 点击 SQL 弹窗查看。
   只读视图：不提供 save，shell 会隐藏顶栏保存按钮。
   视图接口：mount(container) / onEnter() / onLeave() / reload() */
(function () {
  // 可选的每页条数档位
  const PAGE_SIZE_OPTIONS = [50, 100, 500, 1000, 5000];

  const state = {
    // 筛选条件（与输入框双向绑定）
    filters: { project: '', environment: '', databaseType: '', success: '', fromTime: '', toTime: '', sqlContains: '' },
    page: 1,
    pageSize: 50,
    result: null,
    // 项目/环境联动下拉所需配置（从 /admin/api/config 拉取，只用于填充下拉项）
    config: null
  };
  let el = null; // mount 后填充
  let resultLoadToken = 0; // 查询结果懒加载的竞态 token（每次 openSqlDetail 自增，过期响应丢弃）

  function template() {
    return `
      <div class="shell single">
        <section class="card audit-filter-card">
          <div class="audit-filter-bar">
            <label class="filter-field">
              <span>项目</span>
              <select id="filterProject"><option value="">全部</option></select>
            </label>
            <label class="filter-field">
              <span>环境</span>
              <select id="filterEnvironment"><option value="">全部</option></select>
            </label>
            <label class="filter-field">
              <span>类型</span>
              <select id="filterDatabaseType">
                <option value="">全部</option>
                <option value="sqlserver">SqlServer</option>
                <option value="mysql">MySQL</option>
                <option value="oracle">Oracle</option>
              </select>
            </label>
            <label class="filter-field">
              <span>状态</span>
              <select id="filterSuccess">
                <option value="">全部</option>
                <option value="true">成功</option>
                <option value="false">失败</option>
              </select>
            </label>
            <label class="filter-field">
              <span>起始</span>
              <input id="filterFromTime" type="datetime-local" />
            </label>
            <label class="filter-field">
              <span>结束</span>
              <input id="filterToTime" type="datetime-local" />
            </label>
            <label class="filter-field filter-field-grow">
              <span>SQL 关键词</span>
              <input id="filterSqlContains" type="text" autocomplete="off" placeholder="模糊匹配" />
            </label>
            <div class="filter-actions">
              <button id="searchBtn" type="button" class="button primary">查询</button>
              <button id="resetBtn" type="button" class="button secondary">重置</button>
            </div>
          </div>
        </section>

        <section class="card">
          <div class="card-title">
            <div>
              <h2>审计日志</h2>
              <p id="resultMeta" class="muted">输入条件后点击「查询」。</p>
            </div>
            <button id="refreshBtn" type="button" class="button secondary">刷新</button>
          </div>
          <div class="audit-table-wrap">
            <table class="audit-table">
              <thead>
                <tr>
                  <th>时间</th>
                  <th>项目</th>
                  <th>环境</th>
                  <th>类型</th>
                  <th>状态</th>
                  <th>行数</th>
                  <th>耗时</th>
                  <th>SQL</th>
                  <th>错误</th>
                </tr>
              </thead>
              <tbody id="auditBody">
                <tr class="audit-empty-row">
                  <td colspan="9">暂无数据</td>
                </tr>
              </tbody>
            </table>
          </div>
          <div id="pager" class="pager hidden"></div>
        </section>
      </div>

      <!-- SQL 详情弹窗：展示完整 SQL，便于复制 -->
      <dialog id="sqlDialog" class="dialog sql-dialog">
        <form method="dialog">
          <div class="card-title">
            <div>
              <p class="eyebrow">SQL</p>
              <h2 id="sqlDialogTitle">查询语句</h2>
            </div>
            <button id="copySqlBtn" type="button" class="button secondary">复制</button>
          </div>
          <pre id="sqlDialogContent" class="sql-content"></pre>
          <div id="resultSection" class="result-section" hidden>
            <div class="result-header">
              <span class="result-title">查询结果</span>
              <span id="resultStatus" class="result-status"></span>
            </div>
            <div class="result-table-wrap">
              <table id="resultTable" class="result-table"></table>
            </div>
          </div>
          <div class="dialog-actions">
            <button value="close" type="submit" class="button secondary">关闭</button>
          </div>
        </form>
      </dialog>
    `;
  }

  function collectElements(root) {
    const ids = [
      'filterProject', 'filterEnvironment', 'filterDatabaseType', 'filterSuccess',
      'filterFromTime', 'filterToTime', 'filterSqlContains',
      'searchBtn', 'resetBtn', 'refreshBtn',
      'resultMeta', 'auditBody', 'pager',
      'sqlDialog', 'sqlDialogTitle', 'sqlDialogContent', 'copySqlBtn',
      'resultSection', 'resultStatus', 'resultTable'
    ];
    const refs = {};
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  /** datetime-local 用本地时间，提交给后端时转 UTC ISO。空值原样返回。 */
  function localInputToIso(value) {
    if (!value) {
      return '';
    }
    // value 形如 "2026-06-20T15:30"；当作本地时间解析
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '' : date.toISOString();
  }

  /** 把后端 UTC ISO 渲染成本地可读时间。 */
  function isoToLocal(iso) {
    if (!iso) {
      return '';
    }
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      return iso;
    }
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
  }

  function buildQuery() {
    const f = state.filters;
    return {
      project: f.project.trim() || undefined,
      environment: f.environment.trim() || undefined,
      databaseType: f.databaseType || undefined,
      success: f.success || undefined,
      fromTime: localInputToIso(f.fromTime) || undefined,
      toTime: localInputToIso(f.toTime) || undefined,
      sqlContains: f.sqlContains.trim() || undefined,
      page: state.page,
      pageSize: state.pageSize
    };
  }

  async function search(resetPage) {
    if (resetPage) {
      state.page = 1;
    }
    window.adminUi.setBusy(true);
    try {
      const params = new URLSearchParams();
      const q = buildQuery();
      for (const [key, value] of Object.entries(q)) {
        if (value !== undefined && value !== null && value !== '') {
          params.set(key, String(value));
        }
      }
      const data = await window.adminApi.requestJson(`/admin/api/audit-logs?${params.toString()}`);
      state.result = data;
      render();
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function syncFiltersToState() {
    const f = state.filters;
    f.project = el.filterProject.value;
    f.environment = el.filterEnvironment.value;
    f.databaseType = el.filterDatabaseType.value;
    f.success = el.filterSuccess.value;
    f.fromTime = el.filterFromTime.value;
    f.toTime = el.filterToTime.value;
    f.sqlContains = el.filterSqlContains.value;
  }

  /** 拉取配置以填充项目/环境联动下拉（失败时静默降级为手动可选的下拉，不影响查询）。 */
  async function loadConfigForFilters() {
    try {
      state.config = await window.adminApi.loadConfig();
      populateProjectOptions();
    } catch (error) {
      // 配置加载失败不影响日志查询核心功能
      state.config = null;
    }
  }

  /** 项目下拉：选项 = 全部 + 所有项目名。选完后联动环境下拉。 */
  function populateProjectOptions() {
    if (!state.config) {
      return;
    }
    const current = el.filterProject.value;
    el.filterProject.innerHTML = '<option value="">全部</option>';
    for (const project of state.config.projects || []) {
      const option = document.createElement('option');
      option.value = project.name;
      option.textContent = project.displayName || project.name;
      el.filterProject.appendChild(option);
    }
    if (current && [...el.filterProject.options].some(o => o.value === current)) {
      el.filterProject.value = current;
    }
    populateEnvironmentOptions();
  }

  /** 环境下拉：选项 = 全部 + 选中项目下的环境；未选项目则为全部。 */
  function populateEnvironmentOptions() {
    const current = el.filterEnvironment.value;
    const projectName = el.filterProject.value;
    el.filterEnvironment.innerHTML = '<option value="">全部</option>';
    if (projectName && state.config) {
      const project = (state.config.projects || []).find(p => p.name === projectName);
      for (const env of (project?.environments || [])) {
        const option = document.createElement('option');
        option.value = env.name;
        option.textContent = env.displayName || env.name;
        el.filterEnvironment.appendChild(option);
      }
    }
    if (current && [...el.filterEnvironment.options].some(o => o.value === current)) {
      el.filterEnvironment.value = current;
    }
  }

  function applyFiltersToInputs() {
    const f = state.filters;
    // 项目/环境是联动下拉：先按配置填充选项，再回填选中值
    populateProjectOptions();
    if (f.project && [...el.filterProject.options].some(o => o.value === f.project)) {
      el.filterProject.value = f.project;
      populateEnvironmentOptions();
      if (f.environment && [...el.filterEnvironment.options].some(o => o.value === f.environment)) {
        el.filterEnvironment.value = f.environment;
      }
    }
    el.filterDatabaseType.value = f.databaseType;
    el.filterSuccess.value = f.success;
    el.filterFromTime.value = f.fromTime;
    el.filterToTime.value = f.toTime;
    el.filterSqlContains.value = f.sqlContains;
  }

  function render() {
    const result = state.result;
    if (!result) {
      el.resultMeta.textContent = '输入条件后点击「查询」。';
      el.auditBody.innerHTML = '<tr class="audit-empty-row"><td colspan="9">暂无数据</td></tr>';
      el.pager.classList.add('hidden');
      return;
    }

    const items = result.items || [];
    const from = result.total === 0 ? 0 : (result.page - 1) * result.pageSize + 1;
    const to = (result.page - 1) * result.pageSize + items.length;
    el.resultMeta.textContent = `共 ${result.total} 条，当前第 ${from}-${to} 条（第 ${result.page} 页，每页 ${result.pageSize} 条）`;

    if (items.length === 0) {
      el.auditBody.innerHTML = '<tr class="audit-empty-row"><td colspan="9">暂无数据</td></tr>';
      el.pager.classList.add('hidden');
      return;
    }

    el.auditBody.innerHTML = '';
    for (const entry of items) {
      el.auditBody.appendChild(renderRow(entry));
    }
    renderPager(result);
  }

  function renderRow(entry) {
    const tr = document.createElement('tr');
    tr.className = entry.success ? 'row-ok' : 'row-fail';

    const sqlPreview = entry.sql && entry.sql.length > 60
      ? `${entry.sql.slice(0, 60)}…`
      : (entry.sql || '');
    // 错误信息也截断，过长部分点击弹窗查看
    const errorPreview = entry.error ? truncate(entry.error, 20) : '';

    tr.innerHTML = `
      <td class="cell-time">${window.adminUi.escapeHtml(isoToLocal(entry.time))}</td>
      <td>${window.adminUi.escapeHtml(entry.project)}</td>
      <td>${window.adminUi.escapeHtml(entry.environment)}</td>
      <td>${window.adminUi.escapeHtml(entry.databaseType)}</td>
      <td class="cell-status">${entry.success ? '<span class="badge-ok">成功</span>' : '<span class="badge-fail">失败</span>'}</td>
      <td>${entry.rowCount}</td>
      <td>${entry.elapsedMs} ms</td>
      <td class="cell-sql"><span class="sql-preview">${window.adminUi.escapeHtml(sqlPreview)}</span></td>
      <td class="cell-error"><span class="error-preview">${errorPreview ? window.adminUi.escapeHtml(errorPreview) : ''}</span></td>
    `;

    // 点击 SQL 单元格弹出完整 SQL + 查询结果（懒加载）
    if (entry.sql) {
      const sqlCell = tr.querySelector('.cell-sql');
      sqlCell.addEventListener('click', () => openSqlDetail(entry));
      sqlCell.classList.add('clickable');
    }
    // 点击错误单元格弹出完整错误信息（长文本友好查看）
    if (entry.error) {
      const errorCell = tr.querySelector('.cell-error');
      errorCell.addEventListener('click', () => openText('错误信息', entry.error));
      errorCell.classList.add('clickable');
    }
    return tr;
  }

  function truncate(text, max) {
    return text.length > max ? `${text.slice(0, max)}…` : text;
  }

  function renderPager(result) {
    const totalPages = Math.max(1, Math.ceil(result.total / result.pageSize));
    el.pager.classList.remove('hidden');
    el.pager.innerHTML = '';

    const prev = document.createElement('button');
    prev.type = 'button';
    prev.className = 'button secondary';
    prev.textContent = '上一页';
    prev.disabled = result.page <= 1;
    prev.addEventListener('click', () => { state.page -= 1; search(false); });

    const info = document.createElement('span');
    info.className = 'pager-info';
    info.textContent = `${result.page} / ${totalPages}`;

    const next = document.createElement('button');
    next.type = 'button';
    next.className = 'button secondary';
    next.textContent = '下一页';
    next.disabled = result.page >= totalPages;
    next.addEventListener('click', () => { state.page += 1; search(false); });

    // 每页条数下拉：切换后回到第一页重新查询
    const sizeWrap = document.createElement('label');
    sizeWrap.className = 'pager-size';
    const sizeLabel = document.createElement('span');
    sizeLabel.textContent = '每页';
    const sizeSelect = document.createElement('select');
    for (const size of PAGE_SIZE_OPTIONS) {
      const option = document.createElement('option');
      option.value = String(size);
      option.textContent = String(size);
      if (size === result.pageSize) {
        option.selected = true;
      }
      sizeSelect.appendChild(option);
    }
    sizeSelect.addEventListener('change', () => {
      state.pageSize = Number(sizeSelect.value);
      state.page = 1;
      search(false);
    });
    sizeWrap.appendChild(sizeLabel);
    sizeWrap.appendChild(sizeSelect);

    el.pager.appendChild(prev);
    el.pager.appendChild(info);
    el.pager.appendChild(next);
    el.pager.appendChild(sizeWrap);
  }

  /** 通用文本弹窗：展示标题 + 完整文本，供 SQL 与错误信息复用，便于查看与复制。 */
  function openText(title, text) {
    el.resultSection.hidden = true;          // 错误点击路径：不露出结果区
    el.sqlDialogTitle.textContent = title || '详情';
    el.sqlDialogContent.textContent = text || '';
    el.sqlDialog.showModal();
  }

  /** 打开 SQL 详情弹窗并懒加载查询结果。entry.success 为布尔。 */
  function openSqlDetail(entry) {
    // 清空上一次残留（表格/状态/显隐）
    el.resultTable.innerHTML = '';
    el.resultStatus.textContent = '';
    el.resultSection.hidden = true;

    el.sqlDialogTitle.textContent = '查询语句';
    el.sqlDialogContent.textContent = entry.sql || '';
    el.sqlDialog.showModal();

    if (entry.success !== true) {
      // 失败查询：不加载结果
      return;
    }

    el.resultSection.hidden = false;
    el.resultStatus.textContent = '加载中…';
    const myToken = ++resultLoadToken;
    loadResult(entry.id, myToken);
  }

  /** 拉取 result_json 并渲染表格。myToken 用于竞态保护（过期响应丢弃）。 */
  async function loadResult(auditId, myToken) {
    let data;
    try {
      data = await window.adminApi.requestJson(`/admin/api/audit-logs/${auditId}/result`);
    } catch (err) {
      if (myToken !== resultLoadToken) return;   // 已被新请求取代
      if (err && err.status === 404) {
        el.resultStatus.textContent = '该记录无查询结果（老记录或未开启结果记录）';
      } else {
        el.resultStatus.textContent = '加载失败';
      }
      return;
    }
    if (myToken !== resultLoadToken) return;     // 已被新请求取代

    let parsed;
    try {
      parsed = JSON.parse(data.resultJson);
    } catch {
      el.resultStatus.textContent = '结果解析失败';
      return;
    }
    renderResultTable(parsed);
    el.resultStatus.textContent = `共 ${parsed.rows.length} 行`;
  }

  /** 渲染结果表格：行号列 + columns 表头 + rows 表体。 */
  function renderResultTable(data) {
    const table = el.resultTable;
    table.innerHTML = '';

    const thead = document.createElement('thead');
    const headTr = document.createElement('tr');
    const numTh = document.createElement('th');
    numTh.textContent = '#';
    headTr.appendChild(numTh);
    for (const col of data.columns) {
      const th = document.createElement('th');
      th.textContent = col;
      headTr.appendChild(th);
    }
    thead.appendChild(headTr);
    table.appendChild(thead);

    const tbody = document.createElement('tbody');
    let idx = 1;
    for (const row of data.rows) {
      const tr = document.createElement('tr');
      const numTd = document.createElement('td');
      numTd.className = 'row-num';
      numTd.textContent = String(idx++);
      tr.appendChild(numTd);
      for (const val of row) {
        const td = document.createElement('td');
        if (val === null || val === undefined) {
          td.className = 'null-cell';
          td.textContent = 'NULL';
        } else {
          const s = String(val);
          td.textContent = s;
          td.title = s;
          if (s.length > 50) {
            td.classList.add('cell-long');
          }
        }
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
  }

  async function copySql() {
    const text = el.sqlDialogContent.textContent || '';
    try {
      await navigator.clipboard.writeText(text);
      window.adminUi.showToast('已复制到剪贴板');
    } catch (error) {
      // 剪贴板 API 受限时回退选中文本
      const range = document.createRange();
      range.selectNodeContents(el.sqlDialogContent);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      window.adminUi.showToast('复制失败，已为你选中全文，可手动 Ctrl+C', true);
    }
  }

  function resetFilters() {
    state.filters = { project: '', environment: '', databaseType: '', success: '', fromTime: '', toTime: '', sqlContains: '' };
    applyFiltersToInputs();
    state.page = 1;
    state.result = null;
    render();
  }

  /** 切换项目时联动环境，并清空已选环境（原环境可能不属于新项目）。 */
  function onProjectChange() {
    populateEnvironmentOptions();
    state.filters.environment = el.filterEnvironment.value;
  }

  function bindEvents() {
    el.searchBtn.addEventListener('click', () => { syncFiltersToState(); search(true); });
    el.resetBtn.addEventListener('click', resetFilters);
    el.refreshBtn.addEventListener('click', () => search(false));
    el.copySqlBtn.addEventListener('click', copySql);

    // 弹窗关闭后（点关闭 / ESC）清空结果区，避免下次打开残留
    el.sqlDialog.addEventListener('close', () => {
      el.resultTable.innerHTML = '';
      el.resultStatus.textContent = '';
      el.resultSection.hidden = true;
    });

    // 项目联动：选中项目后环境下拉只列该项目下的环境
    el.filterProject.addEventListener('change', onProjectChange);

    // 输入即更新筛选 state（不自动查询，避免抖动）
    [el.filterEnvironment, el.filterDatabaseType, el.filterSuccess,
      el.filterFromTime, el.filterToTime, el.filterSqlContains].forEach(input => {
      input.addEventListener('change', syncFiltersToState);
    });
  }

  window.adminViews = window.adminViews || {};
  window.adminViews['audit-log'] = {
    title: '审计日志',
    eyebrow: 'Audit Log',
    // 只读视图：不设 saveLabel → shell 自动隐藏保存按钮
    saveLabel: '',

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindEvents();
      // 拉配置填充项目/环境联动下拉；同步触发首次查询
      loadConfigForFilters().finally(() => search(true));
    },

    onEnter() {
      if (el && !state.config) {
        loadConfigForFilters();
      }
      if (el) {
        applyFiltersToInputs();
        render();
      }
    },

    onLeave() {
      syncFiltersToState();
    },

    reload() {
      search(false);
    }
  };
})();
