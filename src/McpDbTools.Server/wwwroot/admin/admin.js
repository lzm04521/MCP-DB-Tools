const state = {
  config: null,
  selectedProject: 0,
  selectedEnvironment: 0,
  toastTimer: 0
};

const el = {
  configPath: document.getElementById('configPath'),
  reloadBtn: document.getElementById('reloadBtn'),
  saveBtn: document.getElementById('saveBtn'),
  addProjectBtn: document.getElementById('addProjectBtn'),
  projectList: document.getElementById('projectList'),
  emptyState: document.getElementById('emptyState'),
  editor: document.getElementById('editor'),
  projectName: document.getElementById('projectName'),
  projectDisplayName: document.getElementById('projectDisplayName'),
  defaultEnvironment: document.getElementById('defaultEnvironment'),
  deleteProjectBtn: document.getElementById('deleteProjectBtn'),
  addEnvironmentBtn: document.getElementById('addEnvironmentBtn'),
  environmentTabs: document.getElementById('environmentTabs'),
  environmentEditor: document.getElementById('environmentEditor'),
  deleteEnvironmentBtn: document.getElementById('deleteEnvironmentBtn'),
  productionWarning: document.getElementById('productionWarning'),
  environmentName: document.getElementById('environmentName'),
  environmentDisplayName: document.getElementById('environmentDisplayName'),
  databaseType: document.getElementById('databaseType'),
  isProduction: document.getElementById('isProduction'),
  maxRows: document.getElementById('maxRows'),
  commandTimeout: document.getElementById('commandTimeout'),
  connectionString: document.getElementById('connectionString'),
  disabledKeywords: document.getElementById('disabledKeywords'),
  auditEnabled: document.getElementById('auditEnabled'),
  auditLogPath: document.getElementById('auditLogPath'),
  auditMaxFileSizeMB: document.getElementById('auditMaxFileSizeMB'),
  auditMaxRetentionDays: document.getElementById('auditMaxRetentionDays'),
  toast: document.getElementById('toast'),
  confirmDialog: document.getElementById('confirmDialog'),
  confirmTitle: document.getElementById('confirmTitle'),
  confirmMessage: document.getElementById('confirmMessage'),
  confirmOkBtn: document.getElementById('confirmOkBtn')
};

function activeProject() {
  return state.config?.projects[state.selectedProject] || null;
}

function activeEnvironment() {
  const project = activeProject();
  return project?.environments[state.selectedEnvironment] || null;
}

async function loadConfig() {
  setBusy(true);
  try {
    state.config = await window.adminApi.loadConfig();
    state.selectedProject = Math.min(state.selectedProject, Math.max(0, state.config.projects.length - 1));
    state.selectedEnvironment = 0;
    render();
    showToast('配置已加载');
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setBusy(false);
  }
}

function render() {
  if (!state.config) {
    return;
  }

  el.configPath.textContent = `config: ${state.config.configPath}`;
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
    button.innerHTML = `<strong>${escapeHtml(project.displayName || project.name || '未命名项目')}</strong><span>默认环境：${escapeHtml(project.defaultEnvironment || '未设置')} · ${project.environments.length} 个环境</span>`;
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
    button.innerHTML = `<strong>${escapeHtml(env.displayName || env.name || '未命名环境')}${env.isProduction ? ' ⚠' : ''}</strong><span>${escapeHtml(env.type)} · maxRows ${env.maxRows}</span>`;
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
    project.displayName = emptyToNull(el.projectDisplayName.value);
    project.defaultEnvironment = emptyToNull(el.defaultEnvironment.value);
  }

  const env = activeEnvironment();
  if (env) {
    env.name = el.environmentName.value.trim();
    env.displayName = emptyToNull(el.environmentDisplayName.value);
    env.type = el.databaseType.value;
    env.isProduction = el.isProduction.checked;
    env.maxRows = Number(el.maxRows.value);
    env.commandTimeout = Number(el.commandTimeout.value);
    env.connectionString = emptyToNull(el.connectionString.value);
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
    name: uniqueName('new-project', state.config.projects.map(p => p.name)),
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
  project.environments.push(createEnvironment(uniqueName('Test', project.environments.map(e => e.name))));
  state.selectedEnvironment = project.environments.length - 1;
  render();
}

async function deleteProject() {
  const project = activeProject();
  if (!project) {
    return;
  }

  const ok = await confirmAction('删除项目', `确定删除项目“${project.name}”吗？此操作保存后才会写入 config.json。`);
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

  const ok = await confirmAction('删除环境', `确定删除环境“${project.name} / ${env.name}”吗？此操作保存后才会写入 config.json。`);
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
  setBusy(true);
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
    showToast(`保存成功，备份：${result.backupName}`);
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setBusy(false);
  }
}

function confirmAction(title, message, okText = '确认') {
  return new Promise(resolve => {
    el.confirmTitle.textContent = title;
    el.confirmMessage.textContent = message;
    el.confirmOkBtn.textContent = okText;
    el.confirmDialog.returnValue = '';
    el.confirmDialog.addEventListener('close', () => resolve(el.confirmDialog.returnValue === 'ok'), { once: true });
    el.confirmDialog.showModal();
  });
}

function setBusy(busy) {
  el.reloadBtn.disabled = busy;
  el.saveBtn.disabled = busy;
}

function showToast(message, isError = false) {
  clearTimeout(state.toastTimer);
  el.toast.textContent = message;
  el.toast.classList.toggle('error', isError);
  el.toast.classList.add('show');
  state.toastTimer = setTimeout(() => el.toast.classList.remove('show'), 4500);
}

function uniqueName(base, names) {
  const used = new Set(names.map(name => (name || '').toLowerCase()));
  if (!used.has(base.toLowerCase())) {
    return base;
  }
  let index = 2;
  while (used.has(`${base}-${index}`.toLowerCase())) {
    index += 1;
  }
  return `${base}-${index}`;
}

function emptyToNull(value) {
  const text = value.trim();
  return text.length === 0 ? null : text;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"]/g, char => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;'
  }[char]));
}

[
  el.projectName,
  el.projectDisplayName,
  el.defaultEnvironment,
  el.environmentName,
  el.environmentDisplayName,
  el.databaseType,
  el.isProduction,
  el.maxRows,
  el.commandTimeout,
  el.connectionString,
  el.disabledKeywords,
  el.auditEnabled,
  el.auditLogPath,
  el.auditMaxFileSizeMB,
  el.auditMaxRetentionDays
].forEach(input => input.addEventListener('change', syncFormToState));

el.reloadBtn.addEventListener('click', loadConfig);
el.saveBtn.addEventListener('click', saveConfig);
el.addProjectBtn.addEventListener('click', addProject);
el.addEnvironmentBtn.addEventListener('click', addEnvironment);
el.deleteProjectBtn.addEventListener('click', deleteProject);
el.deleteEnvironmentBtn.addEventListener('click', deleteEnvironment);

loadConfig();
