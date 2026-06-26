/* 全局关键字视图（SPA 视图模块）。
   负责：defaultDisabledKeywords（全局默认）与 defaultDisabledKeywordsByType（按类型追加）。
   数据流：GET /admin/api/config → 缓存完整 config → 表单编辑 → PUT /admin/api/config（全量）。
   保存时必须携带 projects/audit，否则后端全量替换会丢数据。
   视图接口：mount(container) / onEnter() / onLeave() / save() / reload() */
(function () {
  const KEYWORD_TYPES = ['sqlserver', 'mysql', 'oracle'];

  const state = {
    config: null
  };
  let el = null; // mount 后填充

  function template() {
    return `
      <div class="shell single">
        <section class="card">
          <div class="card-title">
            <div>
              <h2>全局默认阻止关键字</h2>
              <p class="muted">所有数据库类型都会叠加这些关键字；每行一个关键字或短语。</p>
            </div>
            <span id="globalCount" class="pill">0 个</span>
          </div>
          <label class="full">
            <span>defaultDisabledKeywords</span>
            <textarea id="defaultDisabledKeywords" rows="10" spellcheck="false"></textarea>
          </label>
        </section>

        <section class="card">
          <div class="card-title">
            <div>
              <h2>按数据库类型追加</h2>
              <p class="muted">这些关键字会在全局默认之上，按数据库类型继续叠加。</p>
            </div>
          </div>
          <div class="grid three">
            <label>
              <span>SQL Server</span>
              <textarea id="sqlserverKeywords" rows="10" spellcheck="false"></textarea>
            </label>
            <label>
              <span>MySQL</span>
              <textarea id="mysqlKeywords" rows="10" spellcheck="false"></textarea>
            </label>
            <label>
              <span>Oracle</span>
              <textarea id="oracleKeywords" rows="10" spellcheck="false"></textarea>
            </label>
          </div>
        </section>

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
      </div>
    `;
  }

  function collectElements(root) {
    return {
      defaultDisabledKeywords: root.getElementById('defaultDisabledKeywords'),
      sqlserverKeywords: root.getElementById('sqlserverKeywords'),
      mysqlKeywords: root.getElementById('mysqlKeywords'),
      oracleKeywords: root.getElementById('oracleKeywords'),
      globalCount: root.getElementById('globalCount')
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

  function bindKeywords() {
    if (!state.config) {
      return;
    }

    el.defaultDisabledKeywords.value = window.adminUi.formatKeywords(state.config.defaultDisabledKeywords);
    for (const type of KEYWORD_TYPES) {
      el[`${type}Keywords`].value = window.adminUi.formatKeywords(state.config.defaultDisabledKeywordsByType?.[type]);
    }
    updateCounts();
  }

  function syncFormToState() {
    if (!state.config) {
      return;
    }

    state.config.defaultDisabledKeywords = window.adminUi.parseKeywords(el.defaultDisabledKeywords.value);
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
          defaultDisabledKeywordsByType: state.config.defaultDisabledKeywordsByType,
          projects: state.config.projects
        })
      });
      state.config = result.config;
      bindKeywords();
      window.adminUi.showToast(`保存成功，备份：${result.backupName}`);
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function updateCounts() {
    el.globalCount.textContent = `${window.adminUi.parseKeywords(el.defaultDisabledKeywords.value).length} 个`;
  }

  function bindEvents() {
    [
      el.defaultDisabledKeywords,
      el.sqlserverKeywords,
      el.mysqlKeywords,
      el.oracleKeywords
    ].forEach(input => input.addEventListener('input', syncFormToState));
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
