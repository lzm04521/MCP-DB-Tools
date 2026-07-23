/* SPA 外壳：路由 + 视图调度 + 顶栏全局按钮管理。
   - 顶栏 / 导航 / toast / 确认弹窗在整个 SPA 生命周期内常驻，不随视图切换刷新。
   - 视图通过 window.adminViews 注册，统一接口：
       { id, title, eyebrow, saveLabel,
         mount(container), onEnter(), onLeave(), save(), reload() }
   - 路由用 hash（#/projects、#/keywords），刷新 / 前进后退 / 收藏均可恢复。 */
(function () {
  const DEFAULT_VIEW = 'projects';

  const dom = {
    eyebrow: document.getElementById('eyebrow'),
    appVersion: document.getElementById('appVersion'),
    viewTitle: document.getElementById('viewTitle'),
    configPath: document.getElementById('configPath'),
    reloadBtn: document.getElementById('reloadBtn'),
    saveBtn: document.getElementById('saveBtn'),
    view: document.getElementById('view')
  };

  // 全局 UI 工具所需 DOM 由 shell 统一注册一次，各视图共享
  window.adminUi.mount({
    toast: document.getElementById('toast'),
    busyButtons: [dom.reloadBtn, dom.saveBtn],
    confirmDialog: document.getElementById('confirmDialog'),
    confirmTitle: document.getElementById('confirmTitle'),
    confirmMessage: document.getElementById('confirmMessage'),
    confirmOkBtn: document.getElementById('confirmOkBtn'),
    unsavedDialog: document.getElementById('unsavedDialog'),
    unsavedTitle: document.getElementById('unsavedTitle'),
    unsavedMessage: document.getElementById('unsavedMessage'),
    unsavedSaveBtn: document.getElementById('unsavedSaveBtn')
  });

  let current = null; // 当前已 mount 的视图对象
  // dirty 离开拦截：用户取消切换时把 hash 改回原值，避免再触发 hashchange
  let currentHash = '';
  let isSwitching = false; // 标记正在处理切换，避免改回 hash 时再次进入流程

  /** 从 location.hash 解析当前视图 id，无效时回退默认视图。 */
  function parseHash() {
    const match = (location.hash || '').match(/^#\/([a-z0-9-]+)/i);
    const id = match ? match[1] : '';
    if (id && window.adminViews && window.adminViews[id]) {
      return id;
    }
    return DEFAULT_VIEW;
  }

  /** 渲染顶栏：标题 / eyebrow / 保存按钮文案 / configPath 跟随当前视图。 */
  function applyHeader(view) {
    dom.eyebrow.textContent = view.eyebrow || '';
    dom.viewTitle.textContent = view.title || '';
    // 只读视图（无 save 或 saveLabel 为空）隐藏保存按钮，避免误导
    const canSave = typeof view.save === 'function' && view.saveLabel;
    dom.saveBtn.classList.toggle('hidden', !canSave);
    if (canSave) {
      dom.saveBtn.textContent = view.saveLabel || '保存';
    }
  }

  /** 卸载旧视图（同步表单到内存 state），清空容器，挂载新视图。 */
  async function switchTo(id) {
    const next = window.adminViews[id];
    if (!next) {
      return;
    }

    // 离开拦截：当前视图若实现了 confirmLeave，先征询未保存修改。
    // - 'cancel' / false：用户要留在当前视图。需要把 hash 改回，让地址栏与新状态一致。
    // - 'clean' / 'proceed' / true：继续切换。
    if (current && typeof current.confirmLeave === 'function') {
      let proceed = true;
      try {
        const result = await current.confirmLeave();
        proceed = result !== 'cancel';
      } catch (error) {
        console.error('confirmLeave 出错：', error);
      }
      if (!proceed) {
        // hash 已被用户改掉，需改回；isSwitching 防止 onHashChange 二次进入
        if (location.hash !== currentHash) {
          isSwitching = true;
          location.hash = currentHash;
        }
        return;
      }
    }

    if (current && typeof current.onLeave === 'function') {
      try {
        current.onLeave();
      } catch (error) {
        console.error('视图 onLeave 出错：', error);
      }
    }

    dom.view.innerHTML = '';
    current = next;
    currentHash = location.hash;

    if (typeof next.mount === 'function') {
      next.mount(dom.view);
    }
    applyHeader(next);
    if (window.adminNav && typeof window.adminNav.setActive === 'function') {
      window.adminNav.setActive(id);
    }
    if (typeof next.onEnter === 'function') {
      next.onEnter();
    }
  }

  function onHashChange() {
    if (isSwitching) {
      isSwitching = false;
      return;
    }
    switchTo(parseHash());
  }

  /**
   * 闲置超时提示：用户在 SPA 内停止输入/点击超过阈值后，若有未保存修改则弹窗。
   * - 任意 input/click/keydown 重置计时器。
   * - 仅触发一次提示；用户处理后再开始新一轮计时。
   * - 阈值默认 60 秒，可由 IDLE_PROMPT_SECONDS 全局变量覆盖。
   */
  const IDLE_PROMPT_SECONDS = Math.max(15, Number(window.IDLE_PROMPT_SECONDS) || 60);
  let idleTimer = 0;
  function scheduleIdlePrompt() {
    if (idleTimer) {
      clearTimeout(idleTimer);
    }
    idleTimer = setTimeout(async () => {
      idleTimer = 0;
      const view = current;
      if (!view || typeof view.isDirty !== 'function' || !view.isDirty()) {
        scheduleIdlePrompt();
        return;
      }
      // 仅提示，不强制离开：用户选保存就保存，选丢弃就回滚，取消则继续编辑。
      try {
        await view.confirmLeave();
      } catch (error) {
        console.error('闲置提示 confirmLeave 出错：', error);
      }
      scheduleIdlePrompt();
    }, IDLE_PROMPT_SECONDS * 1000);
  }

  function resetIdleTimer() {
    if (idleTimer) {
      scheduleIdlePrompt();
    }
  }

  // 浏览器关闭/刷新：原生 beforeunload，由浏览器自己渲染文案；只在 dirty 时拦截。
  window.addEventListener('beforeunload', event => {
    if (current && typeof current.isDirty === 'function' && current.isDirty()) {
      event.preventDefault();
      event.returnValue = ''; // Chrome/Firefox 触发原生提示所必需
    }
  });

  ['input', 'click', 'keydown', 'change'].forEach(type => {
    document.addEventListener(type, resetIdleTimer, { passive: true });
  });

  // 顶栏全局按钮：委托给当前视图
  dom.reloadBtn.addEventListener('click', async () => {
    if (!current) {
      return;
    }
    // 重新加载会丢弃未保存修改，dirty 时同样走 confirmLeave：用户选「丢弃」继续 reload，
    // 选「保存」先保存再 reload，选「取消」中止。
    if (typeof current.confirmLeave === 'function') {
      const result = await current.confirmLeave();
      if (result === 'cancel') {
        return;
      }
    }
    if (typeof current.reload === 'function') {
      current.reload();
    }
  });
  dom.saveBtn.addEventListener('click', () => {
    if (current && typeof current.save === 'function') {
      current.save();
    }
  });

  window.addEventListener('hashchange', onHashChange);

  // 启动：若 hash 为空，补上默认视图 hash（会触发 hashchange）；否则直接切换
  if (!location.hash) {
    location.hash = `#/${DEFAULT_VIEW}`;
  } else {
    onHashChange();
  }
  // 启动 idle 提示计时器（用户操作或保存后会重置）
  scheduleIdlePrompt();

  // 版本号：启动一次性拉取，渲染到顶栏；不随视图切换变化。失败保留占位，不阻断 UI。
  (async () => {
    try {
      const { version } = await window.adminApi.loadVersion();
      dom.appVersion.textContent = version ? `v${version}` : '';
    } catch (error) {
      console.error('加载版本失败：', error);
    }
  })();

  window.adminShell = {
    /** 供视图在 config 加载完成后回填顶栏 configPath。 */
    setConfigPath(text) {
      dom.configPath.textContent = text;
    }
  };
})();
