const KEYWORD_TYPES = ['sqlserver', 'mysql', 'oracle'];

const el = {
  configPath: document.getElementById('configPath'),
  reloadBtn: document.getElementById('reloadBtn'),
  saveBtn: document.getElementById('saveBtn'),
  defaultDisabledKeywords: document.getElementById('defaultDisabledKeywords'),
  sqlserverKeywords: document.getElementById('sqlserverKeywords'),
  mysqlKeywords: document.getElementById('mysqlKeywords'),
  oracleKeywords: document.getElementById('oracleKeywords'),
  globalCount: document.getElementById('globalCount'),
  toast: document.getElementById('toast')
};

const state = {
  config: null,
  toastTimer: null
};

async function loadConfig() {
  setBusy(true);
  try {
    state.config = await window.adminApi.loadConfig();
    bindKeywords();
    showToast('关键字配置已加载');
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setBusy(false);
  }
}

function bindKeywords() {
  if (!state.config) {
    return;
  }

  el.configPath.textContent = `config: ${state.config.configPath}`;
  el.defaultDisabledKeywords.value = formatKeywords(state.config.defaultDisabledKeywords);
  for (const type of KEYWORD_TYPES) {
    el[`${type}Keywords`].value = formatKeywords(state.config.defaultDisabledKeywordsByType?.[type]);
  }
  updateCounts();
}

function syncFormToState() {
  if (!state.config) {
    return;
  }

  state.config.defaultDisabledKeywords = parseKeywords(el.defaultDisabledKeywords.value);
  state.config.defaultDisabledKeywordsByType = {};
  for (const type of KEYWORD_TYPES) {
    state.config.defaultDisabledKeywordsByType[type] = parseKeywords(el[`${type}Keywords`].value);
  }
  updateCounts();
}

async function saveConfig() {
  syncFormToState();
  setBusy(true);
  try {
    const result = await window.adminApi.requestJson('/admin/api/config', {
      method: 'PUT',
      body: JSON.stringify({
        defaultDisabledKeywords: state.config.defaultDisabledKeywords,
        defaultDisabledKeywordsByType: state.config.defaultDisabledKeywordsByType,
        projects: state.config.projects,
        audit: state.config.audit
      })
    });
    state.config = result.config;
    bindKeywords();
    showToast(`保存成功，备份：${result.backupName}`);
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setBusy(false);
  }
}

function parseKeywords(value) {
  const seen = new Set();
  const result = [];
  for (const item of value.split(/[,\n]/)) {
    const keyword = item.trim();
    const key = keyword.toLowerCase();
    if (keyword && !seen.has(key)) {
      seen.add(key);
      result.push(keyword);
    }
  }
  return result;
}

function formatKeywords(keywords) {
  return (keywords || []).join('\n');
}

function updateCounts() {
  el.globalCount.textContent = `${parseKeywords(el.defaultDisabledKeywords.value).length} 个`;
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

[
  el.defaultDisabledKeywords,
  el.sqlserverKeywords,
  el.mysqlKeywords,
  el.oracleKeywords
].forEach(input => input.addEventListener('input', syncFormToState));

el.reloadBtn.addEventListener('click', loadConfig);
el.saveBtn.addEventListener('click', saveConfig);

loadConfig();
