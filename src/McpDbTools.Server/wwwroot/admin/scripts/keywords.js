/* 全局关键字视图（SPA 视图模块）。
   负责：defaultDisabledKeywords（只读池全局默认）、defaultWriteDisabledKeywords（写池全局默认）、
        defaultDisabledKeywordsByType（按数据库类型追加）。
   数据流：GET /admin/api/config → 缓存完整 config → 表单编辑 → PUT /admin/api/config（全量）。
   保存时必须携带 defaultDisabledKeywords/defaultWriteDisabledKeywords/defaultDisabledKeywordsByType/projects，
   缺字段会导致后端全量替换丢数据。
   视图接口：mount(container) / onEnter() / onLeave() / save() / reload()
   布局：左侧三分类导航（只读/写/按类型），右侧每分类含「代码内置·不可修改」固定只读卡片
        + 对应可编辑 textarea。所有 textarea 始终在 DOM 中（仅显隐切换），切换分类不丢编辑内容。 */
(function () {
  // 按类型关键字支持的数据库类型（与后端 DatabaseType 枚举对齐，含 PostgreSQL）
  const KEYWORD_TYPES = ['sqlserver', 'mysql', 'oracle', 'postgresql'];
  // 类型显示名（与 projects.js 数据库类型下拉框保持一致）
  const TYPE_LABELS = {
    sqlserver: 'SQL Server',
    mysql: 'MySQL',
    oracle: 'Oracle',
    postgresql: 'PostgreSQL'
  };

  // 视图内部 state：config 缓存 + 当前选中的左侧分类
  const state = {
    config: null,
    selectedCategory: 'readonly'
  };
  let el = null; // mount 后填充

  function template() {
    return `
      <div class="shell">
        <aside class="sidebar" aria-label="关键字分类">
          <div class="side-title">
            <h2>分类</h2>
          </div>
          <div id="categoryNav" class="project-list">
            <button type="button" class="project-item active" data-category="readonly">
              <strong>📖 只读关键字</strong>
              <span>只读环境默认阻止</span>
            </button>
            <button type="button" class="project-item" data-category="write">
              <strong>✏️ 写关键字</strong>
              <span>写环境额外阻止</span>
            </button>
            <button type="button" class="project-item" data-category="byType">
              <strong>⚙️ 按类型通用关键字</strong>
              <span>按 DBMS 类型追加</span>
            </button>
          </div>
        </aside>

        <section class="workspace" aria-label="关键字编辑区">
          <!-- 只读分类面板 -->
          <div id="readonlyPane" class="category-pane">
            <section class="card">
              <div class="card-title">
                <div>
                  <h2>代码内置只读关键字<span class="eyebrow">Built-in</span></h2>
                </div>
                <span class="pill">代码内置 · 不可修改</span>
              </div>
              <p class="muted">以下关键字由代码内置，所有只读环境默认阻止，无法在此页面修改。</p>
              <div id="builtInReadOnlyList" class="keyword-readonly-list"></div>
            </section>

            <section class="card">
              <div class="card-title">
                <div>
                  <h2>全局只读阻止关键字<span class="eyebrow">Editable</span></h2>
                  <p class="muted">所有只读环境都会叠加这些关键字；每行一个关键字或短语。</p>
                </div>
                <span id="readonlyCount" class="pill">0 个</span>
              </div>
              <label class="full">
                <span>defaultDisabledKeywords</span>
                <textarea id="defaultDisabledKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
              </label>
            </section>
          </div>

          <!-- 写分类面板 -->
          <div id="writePane" class="category-pane hidden">
            <section class="card">
              <div class="card-title">
                <div>
                  <h2>代码内置写关键字<span class="eyebrow">Built-in</span></h2>
                </div>
                <span class="pill">代码内置 · 不可修改</span>
              </div>
              <p class="muted">以下关键字由代码内置，所有写环境（AllowWrite=true）默认阻止，无法在此页面修改。</p>
              <div id="builtInWriteList" class="keyword-readonly-list"></div>
            </section>

            <section class="card">
              <div class="card-title">
                <div>
                  <h2>全局写阻止关键字<span class="eyebrow">Editable</span></h2>
                  <p class="muted">所有写环境都会叠加这些关键字；每行一个关键字或短语。</p>
                </div>
                <span id="writeCount" class="pill">0 个</span>
              </div>
              <label class="full">
                <span>defaultWriteDisabledKeywords</span>
                <textarea id="defaultWriteDisabledKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
              </label>
            </section>
          </div>

          <!-- 按类型分类面板 -->
          <div id="byTypePane" class="category-pane hidden">
            <section class="card">
              <div class="card-title">
                <div>
                  <h2>代码内置按类型关键字<span class="eyebrow">Built-in</span></h2>
                </div>
                <span class="pill">代码内置 · 不可修改</span>
              </div>
              <p class="muted">以下关键字由代码内置，按数据库类型追加，无法在此页面修改。</p>
              <div id="builtInByTypeList" class="keyword-readonly-list"></div>
            </section>

            <section class="card">
              <div class="card-title">
                <div>
                  <h2>按类型追加关键字<span class="eyebrow">Editable</span></h2>
                  <p class="muted">这些关键字会在对应全局默认之上，按数据库类型继续叠加。</p>
                </div>
              </div>
              <div class="grid two">
                <label>
                  <span>SQL Server</span>
                  <textarea id="sqlserverKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
                </label>
                <label>
                  <span>MySQL</span>
                  <textarea id="mysqlKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
                </label>
                <label>
                  <span>Oracle</span>
                  <textarea id="oracleKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
                </label>
                <label>
                  <span>PostgreSQL</span>
                  <textarea id="postgresqlKeywords" rows="10" spellcheck="false" placeholder="留空使用系统默认"></textarea>
                </label>
              </div>
            </section>
          </div>

          <!-- 说明卡片（三分类共用，始终显示） -->
          <section class="card">
            <div class="card-title">
              <div>
                <h2>说明</h2>
                <p class="muted">保存后会写入 config.json，并生成备份。项目/环境额外阻止关键字仍在项目配置页维护。</p>
              </div>
            </div>
            <ul class="keyword-notes">
              <li>空行会被忽略。</li>
              <li>大小写不同但文本相同的关键字会去重。</li>
              <li>下层只能追加，不能缩减上层；最终生效列表由运行时合并。</li>
            </ul>
          </section>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    return {
      categoryNav: root.getElementById('categoryNav'),
      readonlyPane: root.getElementById('readonlyPane'),
      writePane: root.getElementById('writePane'),
      byTypePane: root.getElementById('byTypePane'),
      builtInReadOnlyList: root.getElementById('builtInReadOnlyList'),
      builtInWriteList: root.getElementById('builtInWriteList'),
      builtInByTypeList: root.getElementById('builtInByTypeList'),
      defaultDisabledKeywords: root.getElementById('defaultDisabledKeywords'),
      defaultWriteDisabledKeywords: root.getElementById('defaultWriteDisabledKeywords'),
      sqlserverKeywords: root.getElementById('sqlserverKeywords'),
      mysqlKeywords: root.getElementById('mysqlKeywords'),
      oracleKeywords: root.getElementById('oracleKeywords'),
      postgresqlKeywords: root.getElementById('postgresqlKeywords'),
      readonlyCount: root.getElementById('readonlyCount'),
      writeCount: root.getElementById('writeCount')
    };
  }

  async function loadConfig() {
    window.adminUi.setBusy(true);
    try {
      state.config = await window.adminApi.loadConfig();
      bindKeywords();
      window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      window.adminUi.showToast('关键字配置已加载');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  /** 把单个关键字列表（如 builtInReadOnlyKeywords）渲染为灰底只读 div 的 innerHTML。 */
  function renderReadOnlyList(keywords) {
    const items = (keywords || []).filter(Boolean);
    if (items.length === 0) {
      return '<em>（无）</em>';
    }
    // 与按类型内置列表一致：单行逗号分隔输出（长串由 CSS word-break 自然折行）
    return items.map(k => window.adminUi.escapeHtml(k)).join(', ');
  }

  /** 把 builtInDisabledKeywordsByType 渲染为按 DBMS 分组的 innerHTML（每种类型一行）。 */
  function renderReadOnlyByType(map) {
    const data = map || {};
    return KEYWORD_TYPES.map(type => {
      const items = (data[type] || []).filter(Boolean);
      const label = TYPE_LABELS[type] || type;
      const content = items.length > 0
        ? items.map(k => window.adminUi.escapeHtml(k)).join(', ')
        : '<em>（无）</em>';
      return `<div><strong>${label}：</strong>${content}</div>`;
    }).join('');
  }

  function bindKeywords() {
    if (!state.config) {
      return;
    }

    // 固定只读区（代码内置·不可修改）
    el.builtInReadOnlyList.innerHTML = renderReadOnlyList(state.config.builtInReadOnlyKeywords);
    el.builtInWriteList.innerHTML = renderReadOnlyList(state.config.builtInWriteKeywords);
    el.builtInByTypeList.innerHTML = renderReadOnlyByType(state.config.builtInDisabledKeywordsByType);

    // 可编辑区
    el.defaultDisabledKeywords.value = window.adminUi.formatKeywords(state.config.defaultDisabledKeywords);
    el.defaultWriteDisabledKeywords.value = window.adminUi.formatKeywords(state.config.defaultWriteDisabledKeywords);
    // 兜底：defaultDisabledKeywordsByType 可能缺 postgresql 等键，确保 4 个 textarea 都能取到值
    if (!state.config.defaultDisabledKeywordsByType) {
      state.config.defaultDisabledKeywordsByType = {};
    }
    for (const type of KEYWORD_TYPES) {
      el[`${type}Keywords`].value = window.adminUi.formatKeywords(state.config.defaultDisabledKeywordsByType[type]);
    }

    applyCategoryState();
    updateCounts();
  }

  /** 同步左侧导航 active 与右侧 pane 显隐到 state.selectedCategory。 */
  function applyCategoryState() {
    const category = state.selectedCategory;
    el.categoryNav.querySelectorAll('.project-item').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.category === category);
    });
    el.readonlyPane.classList.toggle('hidden', category !== 'readonly');
    el.writePane.classList.toggle('hidden', category !== 'write');
    el.byTypePane.classList.toggle('hidden', category !== 'byType');
  }

  /** 切换左侧分类：先同步当前编辑到 state，再切换 active/pane，避免切走时丢失未保存输入。 */
  function switchCategory(category) {
    if (category === state.selectedCategory) {
      return;
    }
    syncFormToState();
    state.selectedCategory = category;
    applyCategoryState();
  }

  function syncFormToState() {
    if (!state.config) {
      return;
    }

    state.config.defaultDisabledKeywords = window.adminUi.parseKeywords(el.defaultDisabledKeywords.value);
    state.config.defaultWriteDisabledKeywords = window.adminUi.parseKeywords(el.defaultWriteDisabledKeywords.value);
    // 全量重建 byType，覆盖 4 种 DBMS；缺失类型在循环中补空数组
    state.config.defaultDisabledKeywordsByType = {};
    for (const type of KEYWORD_TYPES) {
      state.config.defaultDisabledKeywordsByType[type] = window.adminUi.parseKeywords(el[`${type}Keywords`].value);
    }
    updateCounts();
  }

  async function saveConfig() {
    syncFormToState();
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/config', {
        method: 'PUT',
        body: JSON.stringify({
          defaultDisabledKeywords: state.config.defaultDisabledKeywords,
          defaultWriteDisabledKeywords: state.config.defaultWriteDisabledKeywords,
          defaultDisabledKeywordsByType: state.config.defaultDisabledKeywordsByType,
          projects: state.config.projects
        })
      });
      state.config = result.config;
      bindKeywords();
      window.adminUi.showToast(`保存成功，备份：${result.backupName}`);
      return true;
    } catch (error) {
      window.adminUi.showToast(error.message, true);
      return false;
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function updateCounts() {
    el.readonlyCount.textContent = `${window.adminUi.parseKeywords(el.defaultDisabledKeywords.value).length} 个`;
    el.writeCount.textContent = `${window.adminUi.parseKeywords(el.defaultWriteDisabledKeywords.value).length} 个`;
  }

  function bindEvents() {
    // 可编辑 textarea：input 即时同步到 state，保留未保存编辑（切走/保存前无需额外动作）
    [
      el.defaultDisabledKeywords,
      el.defaultWriteDisabledKeywords,
      el.sqlserverKeywords,
      el.mysqlKeywords,
      el.oracleKeywords,
      el.postgresqlKeywords
    ].forEach(input => input.addEventListener('input', syncFormToState));

    // 左侧分类导航：点击切换右侧 pane
    el.categoryNav.querySelectorAll('.project-item').forEach(btn => {
      btn.addEventListener('click', () => switchCategory(btn.dataset.category));
    });
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.keywords = {
    title: '阻止关键字',
    eyebrow: 'MCP DB Tools',
    saveLabel: '保存关键字',

    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindEvents();
    },

    onEnter() {
      if (!state.config) {
        loadConfig();
      } else {
        bindKeywords();
        window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      }
    },

    onLeave() {
      syncFormToState();
    },

    save: saveConfig,
    reload: loadConfig
  };
})();
