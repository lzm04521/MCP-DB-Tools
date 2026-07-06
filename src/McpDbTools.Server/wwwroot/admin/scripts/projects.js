/* 项目配置视图（SPA 视图模块）。
   负责：项目列表、项目字段、环境 tabs、环境连接配置、审计配置的读写。
   数据流：GET /admin/api/config → 缓存完整 config → 表单编辑 → PUT /admin/api/config（全量）。
   视图接口：mount(container) / onEnter() / onLeave() / save() / reload()
   公共能力（toast/confirm/busy/转义/去重）来自 window.adminUi。 */
(function () {
  // 视图内部 state 与 DOM 句柄，模块闭包内持有，切走再切回仍保留编辑内容
  const state = {
    config: null,
    selectedProject: 0,
    selectedEnvironment: 0
  };
  let el = null; // mount 后填充

  function template() {
    return `
      <div class="shell">
        <aside class="sidebar" aria-label="项目列表">
          <div class="side-title">
            <h2>项目</h2>
            <button id="addProjectBtn" type="button" class="button ghost">
              新增项目
            </button>
          </div>
          <div id="projectList" class="project-list"></div>
        </aside>

        <section class="workspace" aria-label="配置编辑区">
          <div id="emptyState" class="empty-state hidden">
            <h2>还没有项目</h2>
            <p>点击“新增项目”开始维护 config.json。</p>
          </div>

          <form id="editor" class="editor hidden">
            <section class="card">
              <div class="card-title">
                <div>
                  <p class="eyebrow">Project</p>
                  <h2>项目配置</h2>
                </div>
                <button
                  id="deleteProjectBtn"
                  type="button"
                  class="button danger subtle"
                >
                  删除项目
                </button>
              </div>
              <div class="grid two">
                <label>
                  <span>项目 key *</span>
                  <input
                    id="projectName"
                    type="text"
                    required
                    autocomplete="off"
                  />
                  <small id="projectNameHelp">MCP 调用参数 project，创建后不可修改。</small>
                </label>
                <label>
                  <span>显示名</span>
                  <input id="projectDisplayName" type="text" autocomplete="off" />
                </label>
                <label>
                  <span>默认环境</span>
                  <select id="defaultEnvironment"></select>
                </label>
              </div>
            </section>

            <section class="card">
              <div class="card-title">
                <div>
                  <p class="eyebrow">Environment</p>
                  <h2>环境</h2>
                </div>
                <button
                  id="addEnvironmentBtn"
                  type="button"
                  class="button secondary"
                >
                  新增环境
                </button>
              </div>
              <div id="environmentTabs" class="env-tabs"></div>
            </section>

            <section id="environmentEditor" class="card hidden">
              <div class="card-title">
                <div>
                  <p class="eyebrow">Connection</p>
                  <h2>连接配置</h2>
                </div>
                <div class="card-title-actions">
                  <button
                    id="testConnectionBtn"
                    type="button"
                    class="button secondary"
                  >
                    测试连接
                  </button>
                  <button
                    id="deleteEnvironmentBtn"
                    type="button"
                    class="button danger subtle"
                  >
                    删除环境
                  </button>
                </div>
              </div>

              <div id="testConnectionResult" class="connection-test hidden" role="status"></div>

              <div id="productionWarning" class="warning hidden" role="status">
                当前环境已标记为生产环境。保存不会再要求输入项目名确认，
                请在修改连接字符串、数据库类型、项目 key、环境 key 或删除环境前仔细核对。
              </div>

              <div class="grid two">
                <label>
                  <span>环境 key *</span>
                  <input
                    id="environmentName"
                    type="text"
                    required
                    autocomplete="off"
                  />
                  <small id="environmentNameHelp">创建后不可修改。</small>
                </label>
                <label>
                  <span>显示名</span>
                  <input
                    id="environmentDisplayName"
                    type="text"
                    autocomplete="off"
                  />
                </label>
                <label>
                  <span>数据库类型</span>
                  <select id="databaseType">
                    <option value="sqlserver">SqlServer</option>
                    <option value="mysql">MySQL</option>
                    <option value="oracle">Oracle</option>
                  </select>
                </label>
                <label class="switch-row">
                  <input id="isProduction" type="checkbox" />
                  <span>生产环境</span>
                </label>
                <label>
                  <span>最大行数 *</span>
                  <input id="maxRows" type="number" min="1" step="1" required />
                </label>
                <label>
                  <span>命令超时（秒）*</span>
                  <input
                    id="commandTimeout"
                    type="number"
                    min="1"
                    step="1"
                    required
                  />
                </label>
                <label>
                  <span>最大并发查询数</span>
                  <input
                    id="maxConcurrency"
                    type="number"
                    min="0"
                    step="1"
                    placeholder="留空用全局默认（10）"
                  />
                </label>
                <label>
                  <span>连接池上限</span>
                  <input
                    id="maxPoolSize"
                    type="number"
                    min="0"
                    step="1"
                    placeholder="留空用全局默认（100）"
                  />
                </label>
                <label>
                  <span>建连超时（秒）</span>
                  <input
                    id="connectTimeoutSeconds"
                    type="number"
                    min="0"
                    step="1"
                    placeholder="留空用全局默认（60）"
                  />
                </label>
              </div>

              <label class="full">
                <span>连接字符串 *</span>
                <textarea
                  id="connectionString"
                  rows="4"
                  spellcheck="false"
                ></textarea>
                <small id="connectionHelp"
                  >本机 Admin
                  页面会直接加载完整连接字符串，编辑时请注意避免截图或共享。</small
                >
              </label>

              <label class="full">
                <span>阻止关键字</span>
                <input
                  id="disabledKeywords"
                  type="text"
                  autocomplete="off"
                  placeholder="例如 DROP, DELETE, xp_cmdshell"
                />
                <small
                  >特殊关键字设置，用逗号分隔；保存时会去除空白并按大小写不敏感去重。</small
                >
              </label>
            </section>
          </form>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'projectList', 'emptyState', 'editor', 'projectName', 'projectNameHelp', 'projectDisplayName',
      'defaultEnvironment', 'deleteProjectBtn', 'addEnvironmentBtn',
      'environmentTabs', 'environmentEditor', 'deleteEnvironmentBtn',
      'testConnectionBtn', 'testConnectionResult',
      'productionWarning', 'environmentName', 'environmentNameHelp', 'environmentDisplayName',
      'databaseType', 'isProduction', 'maxRows', 'commandTimeout',
      'maxConcurrency', 'maxPoolSize', 'connectTimeoutSeconds',
      'connectionString', 'disabledKeywords'
    ];
    const refs = { addProjectBtn: root.getElementById('addProjectBtn') };
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
  }

  /**
   * 统一的「Key(显示名)」展示文本。
   * - 有 displayName：返回 `Key(DisplayName)`，二者均转义。
   * - 无 displayName：只返回 Key，不加空括号。
   * 用于左侧项目列表、环境 tabs、默认环境下拉，保证三处一致。
   */
  function formatKeyLabel(key, displayName, fallback = '未命名') {
    const safeKey = window.adminUi.escapeHtml(key || fallback);
    const name = displayName ? String(displayName).trim() : '';
    return name ? `${safeKey}(${window.adminUi.escapeHtml(name)})` : safeKey;
  }

  function activeProject() {
    return state.config?.projects[state.selectedProject] || null;
  }

  function activeEnvironment() {
    const project = activeProject();
    return project?.environments[state.selectedEnvironment] || null;
  }

  async function loadConfig() {
    window.adminUi.setBusy(true);
    try {
      state.config = await window.adminApi.loadConfig();
      state.selectedProject = Math.min(state.selectedProject, Math.max(0, state.config.projects.length - 1));
      state.selectedEnvironment = 0;
      render();
      window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      window.adminUi.showToast('配置已加载');
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function render() {
    if (!state.config) {
      return;
    }

    renderProjectList();

    const hasProjects = state.config.projects.length > 0;
    el.emptyState.classList.toggle('hidden', hasProjects);
    el.editor.classList.toggle('hidden', !hasProjects);

    if (!hasProjects) {
      return;
    }

    bindProject();
    renderEnvironmentTabs();
    bindEnvironment();
  }

  function renderProjectList() {
    el.projectList.innerHTML = '';
    state.config.projects.forEach((project, index) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `project-item${index === state.selectedProject ? ' active' : ''}`;
      button.innerHTML = `<strong>${formatKeyLabel(project.name, project.displayName, '未命名项目')}</strong><span>默认环境：${window.adminUi.escapeHtml(project.defaultEnvironment || '未设置')} · ${project.environments.length} 个环境</span>`;
      button.addEventListener('click', () => {
        syncFormToState();
        state.selectedProject = index;
        state.selectedEnvironment = 0;
        render();
      });
      el.projectList.appendChild(button);
    });
  }

  function bindProject() {
    const project = activeProject();
    el.projectName.value = project.name || '';
    // 项目 key 创建后不可编辑：仅新建（originalName 为 null）时允许输入
    const projectLocked = Boolean(project.originalName);
    el.projectName.readOnly = projectLocked;
    el.projectName.classList.toggle('readonly-field', projectLocked);
    el.projectNameHelp.textContent = projectLocked
      ? '创建后不可修改（已持久化）。'
      : 'MCP 调用参数 project，创建后不可修改。';

    el.projectDisplayName.value = project.displayName || '';
    // 切换项目时重置跟随标记：显示名为空→跟随 key；已有值→视为用户已定，不跟随
    el.projectDisplayName.dataset.autoSynced = project.displayName ? '0' : '1';
    el.defaultEnvironment.innerHTML = '<option value="">不设置默认环境</option>';
    project.environments.forEach(env => {
      const option = document.createElement('option');
      option.value = env.name;
      // 显示「Key(显示名)」，与左侧/环境 tabs 保持一致
      const name = env.displayName ? String(env.displayName).trim() : '';
      option.textContent = name ? `${env.name}(${name})` : env.name;
      el.defaultEnvironment.appendChild(option);
    });
    el.defaultEnvironment.value = project.defaultEnvironment || '';
  }

  function renderEnvironmentTabs() {
    const project = activeProject();
    el.environmentTabs.innerHTML = '';
    project.environments.forEach((env, index) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `env-tab${index === state.selectedEnvironment ? ' active' : ''}`;
      button.innerHTML = `<strong>${formatKeyLabel(env.name, env.displayName, '未命名环境')}${env.isProduction ? ' ⚠' : ''}</strong><span>${window.adminUi.escapeHtml(env.type)} · maxRows ${env.maxRows}</span>`;
      button.addEventListener('click', () => {
        syncFormToState();
        state.selectedEnvironment = index;
        render();
      });
      el.environmentTabs.appendChild(button);
    });
  }

  function bindEnvironment() {
    const env = activeEnvironment();
    el.environmentEditor.classList.toggle('hidden', !env);
    if (!env) {
      return;
    }

    el.environmentName.value = env.name || '';
    // 环境 key 创建后不可编辑：仅新建时允许输入
    const envLocked = Boolean(env.originalName);
    el.environmentName.readOnly = envLocked;
    el.environmentName.classList.toggle('readonly-field', envLocked);
    el.environmentNameHelp.textContent = envLocked
      ? '创建后不可修改（已持久化）。'
      : '创建后不可修改。';

    el.environmentDisplayName.value = env.displayName || '';
    // 切换环境时重置跟随标记：显示名为空→跟随 key；已有值→视为用户已定，不跟随
    el.environmentDisplayName.dataset.autoSynced = env.displayName ? '0' : '1';
    el.databaseType.value = env.type || 'sqlserver';
    el.isProduction.checked = Boolean(env.isProduction);
    el.maxRows.value = env.maxRows || 1000;
    el.commandTimeout.value = env.commandTimeout || 600;
    el.maxConcurrency.value = env.maxConcurrency || '';
    el.maxPoolSize.value = env.maxPoolSize || '';
    el.connectTimeoutSeconds.value = env.connectTimeoutSeconds || '';
    el.connectionString.value = env.connectionString || '';
    el.connectionString.placeholder = '请输入连接字符串';
    el.disabledKeywords.value = (env.disabledKeywords || []).join(', ');
    el.productionWarning.classList.toggle('hidden', !env.isProduction);
  }

  function syncFormToState() {
    if (!state.config) {
      return;
    }

    const project = activeProject();
    if (project) {
      project.name = el.projectName.value.trim();
      project.displayName = window.adminUi.emptyToNull(el.projectDisplayName.value);
      project.defaultEnvironment = window.adminUi.emptyToNull(el.defaultEnvironment.value);
    }

    const env = activeEnvironment();
    if (env) {
      env.name = el.environmentName.value.trim();
      env.displayName = window.adminUi.emptyToNull(el.environmentDisplayName.value);
      env.type = el.databaseType.value;
      env.isProduction = el.isProduction.checked;
      env.maxRows = Number(el.maxRows.value);
      env.commandTimeout = Number(el.commandTimeout.value);
      // 0 表示未配置，后端 resolve 时回退全局默认
      env.maxConcurrency = Number(el.maxConcurrency.value) || 0;
      env.maxPoolSize = Number(el.maxPoolSize.value) || 0;
      env.connectTimeoutSeconds = Number(el.connectTimeoutSeconds.value) || 0;
      env.connectionString = window.adminUi.emptyToNull(el.connectionString.value);
      env.disabledKeywords = el.disabledKeywords.value
        .split(',')
        .map(item => item.trim())
        .filter(Boolean);
    }
  }

  function addProject() {
    syncFormToState();
    state.config.projects.push({
      name: window.adminUi.uniqueName('new-project', state.config.projects.map(p => p.name)),
      originalName: null,
      displayName: null,
      defaultEnvironment: 'Test',
      environments: [createEnvironment('Test')]
    });
    state.selectedProject = state.config.projects.length - 1;
    state.selectedEnvironment = 0;
    render();
  }

  function createEnvironment(name) {
    return {
      name,
      originalName: null,
      displayName: null,
      isProduction: false,
      type: 'sqlserver',
      connectionString: '',
      maxRows: 1000,
      commandTimeout: 600,
      // 并发/池默认 0 = 未配置，resolve 时回退全局默认
      maxConcurrency: 0,
      maxPoolSize: 0,
      connectTimeoutSeconds: 0,
      disabledKeywords: []
    };
  }

  function addEnvironment() {
    syncFormToState();
    const project = activeProject();
    project.environments.push(createEnvironment(window.adminUi.uniqueName('Test', project.environments.map(e => e.name))));
    state.selectedEnvironment = project.environments.length - 1;
    render();
  }

  async function deleteProject() {
    const project = activeProject();
    if (!project) {
      return;
    }

    const ok = await window.adminUi.confirmAction('删除项目', `确定删除项目“${project.name}”吗？此操作保存后才会写入 config.json。`);
    if (!ok) {
      return;
    }

    state.config.projects.splice(state.selectedProject, 1);
    state.selectedProject = Math.max(0, state.selectedProject - 1);
    state.selectedEnvironment = 0;
    render();
  }

  async function deleteEnvironment() {
    const project = activeProject();
    const env = activeEnvironment();
    if (!project || !env) {
      return;
    }

    const ok = await window.adminUi.confirmAction('删除环境', `确定删除环境“${project.name} / ${env.name}”吗？此操作保存后才会写入 config.json。`);
    if (!ok) {
      return;
    }

    project.environments.splice(state.selectedEnvironment, 1);
    if (project.defaultEnvironment === env.name) {
      project.defaultEnvironment = project.environments[0]?.name || null;
    }
    state.selectedEnvironment = Math.max(0, state.selectedEnvironment - 1);
    render();
  }

  async function saveConfig() {
    syncFormToState();
    window.adminUi.setBusy(true);
    try {
      const result = await window.adminApi.requestJson('/admin/api/config', {
        method: 'PUT',
        body: JSON.stringify({
          projects: state.config.projects
        })
      });
      state.config = result.config;
      render();
      window.adminUi.showToast(`保存成功，备份：${result.backupName}`);
    } catch (error) {
      window.adminUi.showToast(error.message, true);
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  function bindEvents(doc) {
    [
      el.projectName, el.projectDisplayName, el.defaultEnvironment,
      el.environmentName, el.environmentDisplayName, el.databaseType,
      el.isProduction, el.maxRows, el.commandTimeout,
      el.maxConcurrency, el.maxPoolSize, el.connectTimeoutSeconds,
      el.connectionString, el.disabledKeywords
    ].forEach(input => input.addEventListener('change', syncFormToState));

    // key → 显示名 自动同步：显示名为空或处于「跟随中」时，输入 key 实时同步；
    // 用户手动编辑显示名（与 key 不同）后停止跟随，尊重已填值。
    setupNameSync(el.projectName, el.projectDisplayName);
    setupNameSync(el.environmentName, el.environmentDisplayName);

    el.addProjectBtn.addEventListener('click', addProject);
    el.addEnvironmentBtn.addEventListener('click', addEnvironment);
    el.deleteProjectBtn.addEventListener('click', deleteProject);
    el.deleteEnvironmentBtn.addEventListener('click', deleteEnvironment);
    el.testConnectionBtn.addEventListener('click', testConnection);
  }

  /** 测试当前编辑框里的连接（用未保存的值即时验证）。 */
  async function testConnection() {
    syncFormToState();
    const env = activeEnvironment();
    if (!env) {
      return;
    }
    if (!env.connectionString) {
      window.adminUi.showToast('请先填写连接字符串', true);
      return;
    }

    el.testConnectionBtn.disabled = true;
    el.testConnectionResult.className = 'connection-test pending';
    el.testConnectionResult.textContent = '正在测试连接…';
    el.testConnectionResult.classList.remove('hidden');

    try {
      const result = await window.adminApi.requestJson('/admin/api/test-connection', {
        method: 'POST',
        body: JSON.stringify({
          databaseType: env.type,
          connectionString: env.connectionString,
          timeoutSeconds: 5
        })
      });
      if (result.success) {
        el.testConnectionResult.className = 'connection-test ok';
        el.testConnectionResult.textContent = `连接成功（耗时 ${result.elapsedMs} ms）`;
      } else {
        el.testConnectionResult.className = 'connection-test fail';
        const reason = result.error ? `：${result.error}` : '';
        el.testConnectionResult.textContent = `连接失败${reason}`;
      }
    } catch (error) {
      el.testConnectionResult.className = 'connection-test fail';
      el.testConnectionResult.textContent = `测试失败：${error.message}`;
    } finally {
      el.testConnectionBtn.disabled = false;
    }
  }

  /**
   * 绑定 key → 显示名 的自动同步。
   * - key 输入时：若显示名为空，或显示名当前等于 key（说明一直在跟随），则同步并标记跟随中。
   * - 显示名手动编辑时：若内容与 key 不同，则取消跟随标记（用户接管）；若改回等于 key，恢复跟随。
   * data-auto-synced 标记用于跨多次 key 输入保持「跟随」状态，避免只同步第一个字符。
   */
  function setupNameSync(keyInput, displayInput) {
    keyInput.addEventListener('input', () => {
      const display = displayInput.value;
      if (display === '' || displayInput.dataset.autoSynced === '1') {
        displayInput.value = keyInput.value;
        displayInput.dataset.autoSynced = '1';
      }
    });
    displayInput.addEventListener('input', () => {
      // 用户手动改了显示名：与 key 不同则脱离跟随，相同则继续跟随
      displayInput.dataset.autoSynced = displayInput.value === keyInput.value ? '1' : '0';
    });
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.projects = {
    title: '项目管理',
    eyebrow: 'Local Admin',
    saveLabel: '保存配置',

    /** shell 注入视图 HTML，绑定事件；首次挂载或无 config 时自动加载。 */
    mount(container) {
      container.innerHTML = template();
      el = collectElements(document);
      bindEvents(document);
    },

    onEnter() {
      if (!state.config) {
        loadConfig();
      } else {
        render();
        window.adminShell.setConfigPath(`config: ${state.config.configPath}`);
      }
    },

    /** 切走前同步表单到内存 state，保留未保存的编辑内容。 */
    onLeave() {
      syncFormToState();
    },

    save: saveConfig,
    reload: loadConfig
  };
})();
