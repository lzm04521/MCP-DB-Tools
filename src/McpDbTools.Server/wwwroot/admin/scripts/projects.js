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
                  <small>MCP 调用参数 project，修改会影响调用方。</small>
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
                <button
                  id="deleteEnvironmentBtn"
                  type="button"
                  class="button danger subtle"
                >
                  删除环境
                </button>
              </div>

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

            <section class="card">
              <div class="card-title">
                <div>
                  <p class="eyebrow">Audit</p>
                  <h2>审计配置</h2>
                </div>
              </div>
              <div class="grid two">
                <label class="switch-row">
                  <input id="auditEnabled" type="checkbox" />
                  <span>启用审计日志</span>
                </label>
                <label>
                  <span>日志路径 *</span>
                  <input id="auditLogPath" type="text" required />
                </label>
                <label>
                  <span>单文件最大 MB *</span>
                  <input
                    id="auditMaxFileSizeMB"
                    type="number"
                    min="1"
                    step="1"
                    required
                  />
                </label>
                <label>
                  <span>保留天数 *</span>
                  <input
                    id="auditMaxRetentionDays"
                    type="number"
                    min="1"
                    step="1"
                    required
                  />
                </label>
              </div>
            </section>
          </form>
        </section>
      </div>
    `;
  }

  function collectElements(root) {
    const ids = [
      'projectList', 'emptyState', 'editor', 'projectName', 'projectDisplayName',
      'defaultEnvironment', 'deleteProjectBtn', 'addEnvironmentBtn',
      'environmentTabs', 'environmentEditor', 'deleteEnvironmentBtn',
      'productionWarning', 'environmentName', 'environmentDisplayName',
      'databaseType', 'isProduction', 'maxRows', 'commandTimeout',
      'connectionString', 'disabledKeywords', 'auditEnabled', 'auditLogPath',
      'auditMaxFileSizeMB', 'auditMaxRetentionDays'
    ];
    const refs = { addProjectBtn: root.getElementById('addProjectBtn') };
    for (const id of ids) {
      refs[id] = root.getElementById(id);
    }
    return refs;
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
    bindAudit();

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
      button.innerHTML = `<strong>${window.adminUi.escapeHtml(project.displayName || project.name || '未命名项目')}</strong><span>默认环境：${window.adminUi.escapeHtml(project.defaultEnvironment || '未设置')} · ${project.environments.length} 个环境</span>`;
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
    el.projectDisplayName.value = project.displayName || '';
    el.defaultEnvironment.innerHTML = '<option value="">不设置默认环境</option>';
    project.environments.forEach(env => {
      const option = document.createElement('option');
      option.value = env.name;
      option.textContent = env.name;
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
      button.innerHTML = `<strong>${window.adminUi.escapeHtml(env.displayName || env.name || '未命名环境')}${env.isProduction ? ' ⚠' : ''}</strong><span>${window.adminUi.escapeHtml(env.type)} · maxRows ${env.maxRows}</span>`;
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
    el.environmentDisplayName.value = env.displayName || '';
    el.databaseType.value = env.type || 'sqlserver';
    el.isProduction.checked = Boolean(env.isProduction);
    el.maxRows.value = env.maxRows || 1000;
    el.commandTimeout.value = env.commandTimeout || 30;
    el.connectionString.value = env.connectionString || '';
    el.connectionString.placeholder = '请输入连接字符串';
    el.disabledKeywords.value = (env.disabledKeywords || []).join(', ');
    el.productionWarning.classList.toggle('hidden', !env.isProduction);
  }

  function bindAudit() {
    const audit = state.config.audit || {};
    el.auditEnabled.checked = Boolean(audit.enabled);
    el.auditLogPath.value = audit.logPath || 'logs/audit.log';
    el.auditMaxFileSizeMB.value = audit.maxFileSizeMB || 10;
    el.auditMaxRetentionDays.value = audit.maxRetentionDays || 30;
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
      env.connectionString = window.adminUi.emptyToNull(el.connectionString.value);
      env.disabledKeywords = el.disabledKeywords.value
        .split(',')
        .map(item => item.trim())
        .filter(Boolean);
    }

    state.config.audit = {
      enabled: el.auditEnabled.checked,
      logPath: el.auditLogPath.value.trim(),
      maxFileSizeMB: Number(el.auditMaxFileSizeMB.value),
      maxRetentionDays: Number(el.auditMaxRetentionDays.value)
    };
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
      commandTimeout: 30,
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
          projects: state.config.projects,
          audit: state.config.audit
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
      el.isProduction, el.maxRows, el.commandTimeout, el.connectionString,
      el.disabledKeywords, el.auditEnabled, el.auditLogPath,
      el.auditMaxFileSizeMB, el.auditMaxRetentionDays
    ].forEach(input => input.addEventListener('change', syncFormToState));

    el.addProjectBtn.addEventListener('click', addProject);
    el.addEnvironmentBtn.addEventListener('click', addEnvironment);
    el.deleteProjectBtn.addEventListener('click', deleteProject);
    el.deleteEnvironmentBtn.addEventListener('click', deleteEnvironment);
  }

  window.adminViews = window.adminViews || {};
  window.adminViews.projects = {
    title: 'MCP DB Tools Config Admin',
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
