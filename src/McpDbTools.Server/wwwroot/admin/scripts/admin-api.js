window.adminApi = (() => {
  async function requestJson(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (options.body) {
      headers.set('Content-Type', 'application/json');
    }

    const response = await fetch(path, { ...options, headers, credentials: 'same-origin' });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      const message = Array.isArray(body.errors) ? body.errors.join('\n') : (body.error || response.statusText);
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
