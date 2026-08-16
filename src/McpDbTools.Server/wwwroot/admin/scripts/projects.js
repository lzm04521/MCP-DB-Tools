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

  // dirty 追踪：保存最近一次「已落库」的 projects 快照（JSON 字符串）。
  // syncFormToState 后比较当前 projects 与快照，不同即视为有未保存修改。
  // markClean 在 loadConfig 成功、saveConfig 成功、用户主动「丢弃」回滚后调用。
  let savedProjectsSnapshot = '';

  function markClean() {
    if (state.config) {
      savedProjectsSnapshot = JSON.stringify(state.config.projects);
    }
  }

  function isDirty() {
    if (!state.config) {
      return false;
    }
    syncFormToState();
    return JSON.stringify(state.config.projects) !== savedProjectsSnapshot;
  }

  /** 回滚到上次保存的 projects 快照（用户选「丢弃」时调用）。 */
  function restoreSnapshot() {
    if (!state.config || !savedProjectsSnapshot) {
      return;
    }
    try {
      state.config.projects = JSON.parse(savedProjectsSnapshot);
      state.selectedProject = Math.min(state.selectedProject, Math.max(0, state.config.projects.length - 1));
      state.selectedEnvironment = Math.min(state.selectedEnvironment, Math.max(0, (state.config.projects[state.selectedProject]?.environments.length || 1) - 1));
    } catch (error) {
      console.error('回滚未保存修改失败：', error);
    }
  }

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
                  <h2>项目配置<span class="eyebrow">Project</span></h2>
                </div>
                <div class="card-title-actions">
                  <button
                    id="copyProjectBtn"
                    type="button"
                    class="button secondary"
                  >
                    复制新增
                  </button>
                  <button
                    id="deleteProjectBtn"
                    type="button"
                    class="button danger subtle"
                  >
                    删除项目
                  </button>
                </div>
              </div>
              <div class="field-row">
                <label>
                  <span>项目 key *</span>
                  <input
                    id="projectName"
                    type="text"
                    required
                    autocomplete="off"
                  />
                  <small id="projectNameHelp">MCP 调用参数 project，修改后 MCP 调用需使用新 key。</small>
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
                  <h2>环境<span class="eyebrow">Environment</span></h2>
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
                  <h2>连接配置<span class="eyebrow">Connection</span></h2>
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

              <div id="allowWriteWarning" class="warning hidden" role="status">
                当前环境已开启 DB 写。db_query 将允许执行 INSERT/UPDATE/DELETE 等写操作；
                DROP TABLE、TRUNCATE 等危险关键字仍被默认阻止（详见「阻止关键字」页）。
                请在修改连接字符串或执行写操作前，仔细核对目标环境与数据库。
              </div>

              <!-- 连接配置头部：与下方 env-params 三列网格同列模板对齐。
                   第 1 列 = conn-header-primary（环境 key combobox + 两个开关横排）；
                   显示名 label 横跨第 2、3 列。switch-group 用 grid +「选项」标题占位，
                   与左右 field/label 的「span 标题 + 控件」两行结构对齐，使 checkbox 与 input 同一水平线。 -->
              <div class="conn-header">
                <!-- 第 1 列包裹层：combobox 弹性拉长，开关组按内容宽 -->
                <div class="conn-header-primary">
                  <!-- 环境 key：自定义 combobox（input + 下拉按钮 + 浮层）。
                       原生 datalist 在 Chrome/Edge 单击不展开且 showPicker() 无效，故自实现。
                       仅新建态可编辑时显示下拉；已保存环境 readonly 时只保留 input。
                       外层不用 <label>，避免点击下拉项时 label 把 click 转发给 input 触发重开。 -->
                  <div class="field envkey-combobox">
                    <span>环境 key *</span>
                    <div class="combobox-wrap">
                      <input
                        id="environmentName"
                        type="text"
                        required
                        autocomplete="off"
                        placeholder="Test / Prod / 自定义"
                        aria-autocomplete="list"
                        aria-expanded="false"
                        aria-controls="environmentKeyList"
                      />
                      <button
                        id="environmentKeyToggle"
                        type="button"
                        class="combobox-toggle"
                        tabindex="-1"
                        aria-label="展开环境 key 建议"
                      >▾</button>
                      <ul id="environmentKeyList" class="combobox-list hidden" role="listbox"></ul>
                    </div>
                  </div>
                  <div class="switch-group">
                    <span>选项</span>
                    <div class="switch-inline">
                      <label class="switch-row">
                        <input id="isProduction" type="checkbox" />
                        <span>生产环境</span>
                      </label>
                      <label class="switch-row">
                        <input id="allowWrite" type="checkbox" />
                        <span>支持 DB 写</span>
                      </label>
                    </div>
                  </div>
                </div>
                <label>
                  <span>显示名</span>
                  <input
                    id="environmentDisplayName"
                    type="text"
                    autocomplete="off"
                  />
                </label>
              </div>
              <small id="environmentNameHelp" class="env-key-help">可改名。可下拉选 Test/Prod，或直接输入自定义值。</small>

              <!-- 连接参数：3 列紧凑（6 字段正好 2 行） -->
              <div class="grid three env-params">
                <label>
                  <span>数据库类型</span>
                  <select id="databaseType">
                    <option value="sqlserver">SqlServer</option>
                    <option value="mysql">MySQL</option>
                    <option value="oracle">Oracle</option>
                    <option value="postgresql">PostgreSQL</option>
                  </select>
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
      'defaultEnvironment', 'copyProjectBtn', 'deleteProjectBtn', 'addEnvironmentBtn',
      'environmentTabs', 'environmentEditor', 'deleteEnvironmentBtn',
      'testConnectionBtn', 'testConnectionResult',
      'productionWarning', 'allowWriteWarning', 'environmentName', 'environmentNameHelp', 'environmentDisplayName',
      'environmentKeyToggle', 'environmentKeyList',
      'databaseType', 'isProduction', 'allowWrite', 'maxRows', 'commandTimeout',
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
   * - 无 displayName 且 key 命中环境预设（Test/Prod/Dev）：回退预设显示名，
   *   如 prod → prod(生产环境)；UAT 及自定义 key 维持裸 key。
   * 用于左侧项目列表、环境 tabs、默认环境下拉，保证三处一致。
   */
  function formatKeyLabel(key, displayName, fallback = '未命名') {
    const safeKey = window.adminUi.escapeHtml(key || fallback);
    const name = displayName ? String(displayName).trim() : (findEnvPreset(key)?.displayName ?? '');
    // 显示名与 key 相同（自定义 key 跟随预填，如 UAT）时只显示 key，不出现 UAT(UAT)
    return name && name !== key ? `${safeKey}(${window.adminUi.escapeHtml(name)})` : safeKey;
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
      markClean();
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
    // 项目 key 可改名：后端按 originalName 定位旧身份，改名保存后 MCP 调用需使用新 key
    el.projectNameHelp.textContent = 'MCP 调用参数 project，修改后 MCP 调用需使用新 key。';

    el.projectDisplayName.value = project.displayName || '';
    // 切换项目时重置跟随标记：显示名为空→跟随 key；已有值→视为用户已定，不跟随
    el.projectDisplayName.dataset.autoSynced = project.displayName ? '0' : '1';
    el.defaultEnvironment.innerHTML = '<option value="">不设置默认环境</option>';
    project.environments.forEach(env => {
      const option = document.createElement('option');
      option.value = env.name;
      // 显示「Key(显示名)」，与左侧/环境 tabs 保持一致（formatKeyLabel 含无显示名时的预设回退）
      option.textContent = formatKeyLabel(env.name, env.displayName, '未命名环境');
      el.defaultEnvironment.appendChild(option);
    });
    // 环境 key 改名后 state 里的 defaultEnvironment 仍指旧名（改名输入不触发 render）。
    // 与后端保存逻辑同语义（AdminConfigService 按 originalName 单跳重映射）：无匹配 option 时
    // 按旧名定位改名环境回填新名，避免 select 空白、再被 syncFormToState 静默洗成 null。
    const defaultEnvName = project.defaultEnvironment;
    if (defaultEnvName && !project.environments.some(env => env.name === defaultEnvName)) {
      const renamed = project.environments.find(
        env => env.originalName && env.originalName.trim() === defaultEnvName);
      if (renamed) {
        project.defaultEnvironment = renamed.name;
      }
    }
    el.defaultEnvironment.value = project.defaultEnvironment || '';
  }

  function renderEnvironmentTabs() {
    const project = activeProject();
    el.environmentTabs.innerHTML = '';
    project.environments.forEach((env, index) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `env-tab${index === state.selectedEnvironment ? ' active' : ''}`;
      // 生产 / 可写 badge：替代原 ⚠ 字符，用颜色区分（生产红、可写绿）；互斥不会同时出现
      const envBadges = [];
      if (env.isProduction) {
        envBadges.push('<span class="env-badge env-badge--prod">生产</span>');
      }
      if (env.allowWrite) {
        envBadges.push('<span class="env-badge env-badge--write">可写</span>');
      }
      button.innerHTML = `<strong>${formatKeyLabel(env.name, env.displayName, '未命名环境')}${envBadges.length ? ' ' + envBadges.join('') : ''}</strong><span>${window.adminUi.escapeHtml(env.type)} · maxRows ${env.maxRows}</span>`;
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
    // 环境 key 可改名：已保存环境也显示 ▾ 建议下拉（仅建议，预设联动见 setupEnvKeyPreset 的 gate）
    el.environmentKeyList.classList.add('hidden');
    el.environmentNameHelp.textContent = '可改名：下拉选 Test/Prod 或自定义；修改后 MCP 调用 environment 需使用新值。';

    el.environmentDisplayName.value = env.displayName || '';
    // 切换环境时按统一跟随判定重置标记：空/预设名/旧 key 预设名/裸 key 均视为跟随中（含脏数据自愈）
    el.environmentDisplayName.dataset.autoSynced = isEnvDisplayNameFollowing(env) ? '1' : '0';
    el.databaseType.value = env.type || 'sqlserver';
    el.isProduction.checked = Boolean(env.isProduction);
    el.allowWrite.checked = Boolean(env.allowWrite);
    el.maxRows.value = env.maxRows || 1000;
    el.commandTimeout.value = env.commandTimeout || 600;
    el.maxConcurrency.value = env.maxConcurrency || '';
    el.maxPoolSize.value = env.maxPoolSize || '';
    el.connectTimeoutSeconds.value = env.connectTimeoutSeconds || '';
    el.connectionString.value = env.connectionString || '';
    el.connectionString.placeholder = '请输入连接字符串';
    el.disabledKeywords.value = (env.disabledKeywords || []).join(', ');
    // 互斥联动：初始化 allowWrite/isProduction 的 disabled 状态与 productionWarning 显隐
    applyMutexState();
    // 切换到已存在环境时重置交互标记：用户尚未在新上下文里手动改过生产开关
    resetEnvInteractionMarks();
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
      env.allowWrite = el.allowWrite.checked;
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

  /** 复制新增：深拷贝当前项目为未保存的新项目（key 默认 原key-copy），保存后才写入 config.json。 */
  function copyProject() {
    const project = activeProject();
    if (!project) {
      return;
    }
    syncFormToState();
    const clone = JSON.parse(JSON.stringify(project));
    // key 默认 原key-copy（重复时 -copy-2 递增）；originalName 清空 = 全新身份，key 可编辑
    const base = project.name ? `${project.name}-copy` : 'new-project';
    clone.name = window.adminUi.uniqueName(base, state.config.projects.map(p => p.name));
    clone.originalName = null;
    clone.environments.forEach(env => {
      env.originalName = null;
    });
    state.config.projects.push(clone);
    state.selectedProject = state.config.projects.length - 1;
    state.selectedEnvironment = 0;
    render();
    // 复制后用户最常做的就是改 key：聚焦并全选
    el.projectName.focus();
    el.projectName.select();
    window.adminUi.showToast(`已复制为 ${clone.name}，保存后写入 config.json`);
  }

  function createEnvironment(name) {
    // key 命中预设（Test/Prod/Dev）时在数据层预填显示名与生产标识，等价于用户
    // 手动选中该 key 时的联动；程序化设置输入框 value 不触发 input 事件，不能依赖
    // setupEnvKeyPreset 自动生效。新建环境与新增项目的默认环境共用此逻辑。
    const preset = findEnvPreset(name);
    return {
      name,
      originalName: null,
      displayName: preset?.displayName ?? null,
      isProduction: preset?.isProduction ?? false,
      allowWrite: false,
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
    // 默认填 Test，即便当前项目已存在 Test 也允许临时重名，重复检测推迟到保存时由后端卡控。
    // Test 预设（显示名+生产标识）由 createEnvironment 在数据层统一应用。
    const env = createEnvironment('Test');
    project.environments.push(env);
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
      // key 变更提示：响应会重置 originalName = 新 key，须在替换前基于旧 state 判断
      const keyRenamed = state.config.projects.some(p =>
        p.originalName && p.originalName !== p.name) ||
        state.config.projects.some(p =>
          p.environments.some(e => e.originalName && e.originalName !== e.name));
      state.config = result.config;
      render();
      markClean();
      window.adminUi.showToast(
        `保存成功，备份：${result.backupName}${keyRenamed ? '；注意：key 已变更，MCP 调用需使用新 key' : ''}`);
      return true;
    } catch (error) {
      window.adminUi.showToast(error.message, true);
      return false;
    } finally {
      window.adminUi.setBusy(false);
    }
  }

  /**
   * 离开前提示（由 shell / onbeforeunload / 闲置定时器调用）。
   * - 无未保存修改：返回 'clean'，调用方继续后续动作。
   * - 有修改：弹三选一对话框。
   *   - save：调用 saveConfig；保存成功返回 'proceed'，失败返回 'cancel'（留在页面）。
   *   - discard：回滚到快照，返回 'proceed'。
   *   - cancel：返回 'cancel'，调用方应中止后续动作。
   * @returns {Promise<'clean'|'proceed'|'cancel'>}
   */
  async function confirmLeave() {
    if (!isDirty()) {
      return 'clean';
    }
    const choice = await window.adminUi.confirmSaveDiscard();
    if (choice === 'save') {
      const ok = await saveConfig();
      return ok ? 'proceed' : 'cancel';
    }
    if (choice === 'discard') {
      restoreSnapshot();
      render();
      return 'proceed';
    }
    return 'cancel';
  }

  function bindEvents(doc) {
    [
      el.projectName, el.projectDisplayName, el.defaultEnvironment,
      el.environmentName, el.environmentDisplayName, el.databaseType,
      el.isProduction, el.allowWrite, el.maxRows, el.commandTimeout,
      el.maxConcurrency, el.maxPoolSize, el.connectTimeoutSeconds,
      el.connectionString, el.disabledKeywords
    ].forEach(input => input.addEventListener('change', syncFormToState));

    // 项目 key → 显示名自动同步：显示名为空或「跟随中」时随 key 原文实时同步。
    setupNameSync(el.projectName, el.projectDisplayName);
    // 环境 key→显示名不走 setupNameSync（原文复制曾与预设联动竞争，改名时显示名被写成裸 key），
    // 统一走 setupEnvKeyPreset 的跟随模型（跟随中→预设名/清空；用户手填→不动）。
    setupEnvKeyPreset();

    // 生产环境 ↔ DB 写 软互斥：勾选一方时取消另一方勾选（不置灰，两框始终可编辑）。
    // productionWarning 显隐统一由 applyMutexState 处理。
    // dataset.touched 保留「用户手动操作过生产开关」语义，避免预设联动覆盖用户意图。
    el.isProduction.addEventListener('change', () => {
      el.isProduction.dataset.touched = '1';
      if (el.isProduction.checked) {
        el.allowWrite.checked = false;
      }
      applyMutexState();
    });
    el.allowWrite.addEventListener('change', () => {
      if (el.allowWrite.checked) {
        el.isProduction.checked = false;
      }
      applyMutexState();
    });

    el.addProjectBtn.addEventListener('click', addProject);
    el.addEnvironmentBtn.addEventListener('click', addEnvironment);
    el.copyProjectBtn.addEventListener('click', copyProject);
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

  /**
   * 环境 key 预设映射（combobox 选项 → 默认显示名）。
   * - Test  → 测试环境
   * - Prod  → 生产环境（同时联动勾选「生产环境」）
   * - Dev   → 开发环境
   * - UAT 及任意自定义输入 → 不预设，保留用户输入或留空。
   */
  const ENV_KEY_PRESETS = {
    Test: { displayName: '测试环境', isProduction: false },
    Prod: { displayName: '生产环境', isProduction: true },
    Dev: { displayName: '开发环境', isProduction: false }
  };

  /**
   * 按环境 key 查预设（大小写不敏感）：prod/Prod/PROD 均命中 Prod 预设。
   * 预设联动、新建预填、展示回退共用；UAT 及自定义 key 返回 undefined。
   */
  const ENV_PRESETS_BY_LOWER_KEY = new Map(
    Object.entries(ENV_KEY_PRESETS).map(([presetKey, preset]) => [presetKey.toLowerCase(), preset])
  );

  function findEnvPreset(name) {
    return name ? ENV_PRESETS_BY_LOWER_KEY.get(String(name).toLowerCase()) : undefined;
  }

  /**
   * 环境显示名「跟随 key」统一判定（bindEnvironment 初始化与显示名手动编辑共用）。
   * 满足任一即跟随中：
   * - 显示名为空；
   * - 等于当前 key（name）或旧 key（originalName）的预设中文显示名（程序预填形态）；
   * - 等于当前/旧 key 本身（旧版原文同步留下的裸 key 脏数据，借此自愈）。
   * 其余视为用户已定，改 key 不覆盖。
   */
  function isEnvDisplayNameFollowing(env) {
    const displayName = env?.displayName?.trim();
    if (!displayName) {
      return true;
    }
    return [env?.name, env?.originalName]
      .filter(Boolean)
      .some(key => displayName === key || displayName === findEnvPreset(key)?.displayName);
  }

  /**
   * 环境 key 联动 displayName 与 isProduction（统一跟随模型，新建与改名同一路径）。
   * - displayName：跟随中（autoSynced='1'）→ key 命中预设填预设中文显示名、
   *   自定义 key（UAT 等）填 key 原文；用户手填过（autoSynced='0'）→ 完全不动。
   * - 显示名手动编辑时按 isEnvDisplayNameFollowing 重新判定跟随（清空即恢复跟随）。
   * - isProduction：仅新建环境且用户未手动操作过时联动；已保存环境改 key 是重命名，
   *   不自动翻转生产开关（安全语义）。
   */
  function setupEnvKeyPreset() {
    el.environmentName.addEventListener('input', () => {
      const preset = findEnvPreset(el.environmentName.value);
      if (el.environmentDisplayName.dataset.autoSynced === '1') {
        // 跟随中：命中预设填中文显示名；自定义 key（UAT 等）填 key 原文
        el.environmentDisplayName.value = preset?.displayName ?? el.environmentName.value;
      }
      if (!preset) {
        return;
      }
      // 生产开关联动仅新建环境：已保存环境改 key 不自动翻转（originalName gate）
      if (activeEnvironment()?.originalName) {
        return;
      }
      if (el.isProduction.dataset.touched !== '1') {
        el.isProduction.checked = preset.isProduction;
        // 预设勾选生产时，按互斥清掉 DB 写（applyMutexState 现在不管勾选取消）
        if (preset.isProduction) {
          el.allowWrite.checked = false;
        }
        // 由 applyMutexState 统一处理 productionWarning 显隐
        applyMutexState();
      }
    });
    el.environmentDisplayName.addEventListener('input', () => {
      // 手动编辑显示名：值仍属跟随形态（空/预设名/等于 key）→ 继续跟随，否则视为用户已定
      const env = activeEnvironment();
      el.environmentDisplayName.dataset.autoSynced = isEnvDisplayNameFollowing({
        ...env,
        name: el.environmentName.value.trim() || env?.name,
        displayName: el.environmentDisplayName.value
      }) ? '1' : '0';
    });
    // isProduction 的 change 监听（dataset.touched + applyMutexState）已整合到 bindEvents，
    // 不再在此重复注册，避免 productionWarning 被切换两次（toggle 幂等抵消 bug）。
    // 自定义 combobox：替代原生 datalist（Chrome/Edge 单击不展开且 showPicker 无效）
    setupEnvKeyCombobox();
  }

  /**
   * 环境 key 自定义下拉。
   * - 建议：Test / Prod / Dev / UAT（前三个有联动预设，UAT 仅建议项不联动）。
   * - 点击 input 或 ▾ 按钮：展开/收起。
   * - 输入时按前缀过滤；点击列表项：填入 input 并触发 input 事件（联动 displayName/isProduction）。
   * - 键盘：↑↓ 选项、Enter 选中、Esc 关闭。
   * - 已保存环境 readonly：隐藏 ▾ 按钮，不响应展开。
   * - 点击外部关闭。
   */
  const ENV_KEY_SUGGESTIONS = ['Test', 'Prod', 'Dev', 'UAT'];
  let comboboxOpen = false;
  let comboboxHighlight = -1;
  // selectItem 后聚焦 input 时抑制一次自动 open，避免选完项浮层立刻又被打开
  let suppressingFocus = false;

  function setupEnvKeyCombobox() {
    const input = el.environmentName;
    const toggle = el.environmentKeyToggle;
    const list = el.environmentKeyList;

    function isOpenable() {
      return !input.readOnly;
    }

    function renderList(filter) {
      const q = (filter || '').trim().toLowerCase();
      const items = q ? ENV_KEY_SUGGESTIONS.filter(s => s.toLowerCase().includes(q)) : ENV_KEY_SUGGESTIONS.slice();
      list.innerHTML = '';
      items.forEach((value, index) => {
        const li = document.createElement('li');
        li.role = 'option';
        li.dataset.value = value;
        li.tabIndex = -1;
        // 联动信息提示，帮助用户预判
        const preset = findEnvPreset(value);
        const hint = preset ? `（${preset.displayName}${preset.isProduction ? ' · 生产' : ''}）` : '';
        li.textContent = `${value}${hint}`;
        li.addEventListener('mousedown', event => {
          // mousedown 先于 input blur 触发，避免失焦关掉浮层导致点击无效
          event.preventDefault();
        });
        li.addEventListener('click', () => selectItem(value));
        list.appendChild(li);
      });
      comboboxHighlight = items.length > 0 ? 0 : -1;
      updateHighlight();
      return items.length;
    }

    function open() {
      if (!isOpenable() || comboboxOpen) {
        return;
      }
      // focus/click 展开时显示完整建议列表，不按当前值过滤；
      // 过滤仅由 input 事件（用户主动输入字符）触发，避免初始默认值导致空列表。
      renderList('');
      list.classList.remove('hidden');
      comboboxOpen = true;
      input.setAttribute('aria-expanded', 'true');
    }

    function close() {
      if (!comboboxOpen) {
        return;
      }
      list.classList.add('hidden');
      comboboxOpen = false;
      comboboxHighlight = -1;
      input.setAttribute('aria-expanded', 'false');
    }

    function toggleOpen() {
      if (!isOpenable()) {
        return;
      }
      if (comboboxOpen) {
        close();
      } else {
        open();
      }
    }

    function selectItem(value) {
      input.value = value;
      // 触发 input 让 setupNameSync + setupEnvKeyPreset 联动 displayName 与 isProduction
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
      close();
      // focus 会再次触发 open（focus 监听器），用 suppressingFocus 标志临时抑制一次
      suppressingFocus = true;
      input.focus();
    }

    function updateHighlight() {
      const items = list.querySelectorAll('li');
      items.forEach((li, i) => {
        li.classList.toggle('highlighted', i === comboboxHighlight);
      });
      const cur = items[comboboxHighlight];
      if (cur) {
        cur.scrollIntoView({ block: 'nearest' });
      }
    }

    function moveHighlight(delta) {
      const items = list.querySelectorAll('li');
      if (items.length === 0) {
        return;
      }
      let next = comboboxHighlight + delta;
      if (next < 0) {
        next = items.length - 1;
      } else if (next >= items.length) {
        next = 0;
      }
      comboboxHighlight = next;
      updateHighlight();
    }

    toggle.addEventListener('click', event => {
      event.preventDefault();
      toggleOpen();
    });

    input.addEventListener('focus', () => {
      // selectItem 后聚焦会触发本监听器；用 suppressingFocus 抑制一次，避免选完项浮层立即重开
      if (suppressingFocus) {
        suppressingFocus = false;
        return;
      }
      // 仅在可编辑时聚焦自动展开（已保存环境 readonly 不响应）
      if (isOpenable()) {
        open();
      }
    });

    input.addEventListener('click', () => {
      if (isOpenable()) {
        open();
      }
    });

    input.addEventListener('input', () => {
      if (comboboxOpen) {
        renderList(input.value);
      }
    });

    input.addEventListener('keydown', event => {
      if (!comboboxOpen) {
        if (event.key === 'ArrowDown' || event.key === 'Enter') {
          if (isOpenable()) {
            event.preventDefault();
            open();
          }
        }
        return;
      }
      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault();
          moveHighlight(1);
          break;
        case 'ArrowUp':
          event.preventDefault();
          moveHighlight(-1);
          break;
        case 'Enter': {
          event.preventDefault();
          const items = list.querySelectorAll('li');
          const cur = items[comboboxHighlight];
          if (cur) {
            selectItem(cur.dataset.value);
          } else {
            close();
          }
          break;
        }
        case 'Escape':
          event.preventDefault();
          close();
          break;
        case 'Tab':
          close();
          break;
      }
    });

    // 点击外部关闭浮层
    document.addEventListener('click', event => {
      if (!comboboxOpen) {
        return;
      }
      const wrap = input.closest('.combobox-wrap');
      if (wrap && !wrap.contains(event.target)) {
        close();
      }
    });
  }

  /**
   * 重置环境字段交互标记（切换环境/项目/保存后调用）。
   * - autoSynced：bindEnvironment 中已根据 displayName 是否为空重置
   * - touched：isProduction 复选框的「用户已操作」标记需重置，允许下次新建环境时再次联动
   */
  function resetEnvInteractionMarks() {
    el.isProduction.dataset.touched = '';
  }

  /**
   * 生产环境 ↔ DB 写 互斥联动。
   * 互斥的「勾选取消」由各 change 监听负责（勾一方 → 清另一方）；
   * 此处统一管理 productionWarning / allowWriteWarning 显隐（互斥，两者不会同时显示）。
   * 不再 disabled 对方——编辑模式下两框始终可点击，避免「被错误禁止编辑」的困惑。
   * 由 bindEnvironment 初始化、两框 change 监听、setupEnvKeyPreset 预设联动统一调用。
   */
  function applyMutexState() {
    el.productionWarning.classList.toggle('hidden', !el.isProduction.checked);
    el.allowWriteWarning.classList.toggle('hidden', !el.allowWrite.checked);
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
    reload: loadConfig,
    /** 供 shell/外部查询：当前是否有未保存的修改。 */
    isDirty,
    /**
     * 离开前提示。返回 'clean'（无需提示）/'proceed'（用户同意离开，已保存或丢弃）/'cancel'（留在页面）。
     * shell.switchTo 在切换前调用，'cancel' 时中断切换。
     */
    confirmLeave
  };
})();
