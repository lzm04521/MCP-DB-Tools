window.adminApi = (() => {
  async function requestJson(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (options.body) {
      headers.set('Content-Type', 'application/json');
    }

    const response = await fetch(path, { ...options, headers, credentials: 'same-origin' });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      // 优先取后端结构化 errors/error；无 JSON body 时用带状态码的中文提示，不透出英文 statusText
      const message = Array.isArray(body.errors) ? body.errors.join('\n')
        : (body.error || `请求失败（HTTP ${response.status}）`);
      const error = new Error(message);
      error.status = response.status;
      throw error;
    }
    return body;
  }

  async function loadConfig() {
    return requestJson('/admin/api/config');
  }

  async function loadVersion() {
    return requestJson('/admin/api/version');
  }

  return { loadConfig, loadVersion, requestJson };
})();
