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
    confirmOkBtn: document.getElementById('confirmOkBtn')
  });

  let current = null; // 当前已 mount 的视图对象

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

    if (current && typeof current.onLeave === 'function') {
      try {
        current.onLeave();
      } catch (error) {
        console.error('视图 onLeave 出错：', error);
      }
    }

    dom.view.innerHTML = '';
    current = next;

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
    switchTo(parseHash());
  }

  // 顶栏全局按钮：委托给当前视图
  dom.reloadBtn.addEventListener('click', () => {
    if (current && typeof current.reload === 'function') {
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

  window.adminShell = {
    /** 供视图在 config 加载完成后回填顶栏 configPath。 */
    setConfigPath(text) {
      dom.configPath.textContent = text;
    }
  };
})();
