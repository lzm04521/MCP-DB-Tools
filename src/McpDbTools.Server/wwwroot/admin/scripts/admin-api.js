window.adminApi = (() => {
  async function ensureSession() {
    const response = await fetch('/admin/session', { credentials: 'same-origin' });
    if (!response.ok) {
      throw new Error('Admin 会话初始化失败，请刷新页面重试。');
    }
  }

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
    await ensureSession();
    try {
      return await requestJson('/admin/api/config');
    } catch (error) {
      if (error.status !== 401) {
        throw error;
      }

      await ensureSession();
      return await requestJson('/admin/api/config');
    }
  }

  return { loadConfig, requestJson };
})();
