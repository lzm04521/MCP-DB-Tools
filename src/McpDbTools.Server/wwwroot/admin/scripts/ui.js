/* 通用 UI 工具：toast 提示、确认对话框、忙碌状态、字符串/关键字工具。
   SPA 架构下由 shell 统一调用 adminUi.mount({ toast, busyButtons, confirmDialog, ... })
   注册一次共享 DOM 节点，各视图直接使用 window.adminUi.showToast 等。
   设计为“零页面假设”，不绑定具体页面结构。 */
(function () {
  // 已注册的 DOM 句柄
  const refs = {
    toast: null,
    busyButtons: [],
    confirmDialog: null,
    confirmTitle: null,
    confirmMessage: null,
    confirmOkBtn: null,
    unsavedDialog: null,
    unsavedTitle: null,
    unsavedMessage: null,
    unsavedSaveBtn: null
  };

  // 每个 toast 独立的计时器，避免相互覆盖
  const timers = new Map();

  /**
   * 注册页面用到的 DOM 节点。
   * @param {Object} elements
   *   - toast: toast 容器元素（含 .show/.error 类）
   *   - busyButtons: 请求中需要 disable 的按钮数组
   *   - confirmDialog / confirmTitle / confirmMessage / confirmOkBtn: 确认弹窗组件
   */
  function mount(elements) {
    Object.assign(refs, elements);
  }

  function showToast(message, isError = false) {
    const toast = refs.toast;
    if (!toast) {
      return;
    }
    clearTimeout(timers.get(toast));
    toast.textContent = message;
    toast.classList.toggle('error', isError);
    toast.classList.add('show');
    timers.set(toast, setTimeout(() => toast.classList.remove('show'), 4500));
  }

  /**
   * 弹出确认对话框，返回 Promise<boolean>。
   * 需先 mount confirmDialog / confirmTitle / confirmMessage / confirmOkBtn。
   */
  function confirmAction(title, message, okText = '确认') {
    const dialog = refs.confirmDialog;
    if (!dialog) {
      return Promise.resolve(window.confirm(`${title}\n\n${message}`));
    }
    refs.confirmTitle.textContent = title;
    refs.confirmMessage.textContent = message;
    refs.confirmOkBtn.textContent = okText;
    dialog.returnValue = '';
    return new Promise(resolve => {
      dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
      dialog.showModal();
    });
  }

  /**
   * 未保存提示：三选一弹窗。
   * @returns {'save'|'discard'|'cancel'} save=保存并继续；discard=丢弃修改并继续；cancel=取消操作，留在当前页。
   * 需先 mount unsavedDialog / unsavedTitle / unsavedMessage / unsavedSaveBtn。
   */
  function confirmSaveDiscard(title = '有未保存的修改', message = '当前配置已修改但尚未保存，是否保存？', saveText = '保存') {
    const dialog = refs.unsavedDialog;
    if (!dialog) {
      // fallback：原生 confirm 仅能两选一，将「不保存」与「取消」合并为「不保存」
      const ok = window.confirm(`${title}\n\n${message}\n（确定=保存并继续，取消=留在页面）`);
      return Promise.resolve(ok ? 'save' : 'cancel');
    }
    refs.unsavedTitle.textContent = title;
    refs.unsavedMessage.textContent = message;
    refs.unsavedSaveBtn.textContent = saveText;
    dialog.returnValue = '';
    return new Promise(resolve => {
      dialog.addEventListener('close', () => {
        const value = dialog.returnValue || 'cancel';
        resolve(value === 'save' ? 'save' : (value === 'discard' ? 'discard' : 'cancel'));
      }, { once: true });
      dialog.showModal();
    });
  }

  /** 切换“忙碌”态：禁用/启用注册的按钮。 */
  function setBusy(busy) {
    for (const btn of refs.busyButtons || []) {
      if (btn) {
        btn.disabled = busy;
      }
    }
  }

  // ============ 字符串 / 关键字工具（纯函数，无 DOM 依赖） ============

  function escapeHtml(value) {
    return String(value).replace(/[&<>"]/g, char => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;'
    }[char]));
  }

  /** 在 names 中生成基于 base 的不重复名称（大小写不敏感）。 */
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

  /** 空字符串转 null，用于可选字段（与后端 init 字段语义一致）。 */
  function emptyToNull(value) {
    const text = value.trim();
    return text.length === 0 ? null : text;
  }

  /** 按逗号/换行切分关键字，去空白、大小写不敏感去重。 */
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

  window.adminUi = {
    mount,
    showToast,
    confirmAction,
    confirmSaveDiscard,
    setBusy,
    escapeHtml,
    uniqueName,
    emptyToNull,
    parseKeywords,
    formatKeywords
  };
})();
