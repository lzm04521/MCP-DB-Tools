/* 顶部导航条：数据驱动，点击切换 hash，由 shell 路由器统一处理视图切换。
   - 已实现页面用 button[data-view]，点击改 location.hash，不触发整页刷新。
   - 未实现/规划中的页面置 disabled:true，渲染为禁用按钮并显示 hint。
   - setActive(id) 供 shell 在视图切换后调用，高亮当前项。 */
(function () {
  const NAV_ITEMS = [
    { id: 'projects', label: '项目配置' },
    { id: 'keywords', label: '全局关键字' },
    { id: 'audit-log', label: '审计日志', disabled: true, hint: '规划中（将改为本地数据库日志）' },
    { id: 'backups', label: '备份管理', disabled: true, hint: '规划中（Phase 3）' },
    { id: 'test', label: '测试连接', disabled: true, hint: '规划中（Phase 3）' }
  ];

  let container = null;
  let activeId = '';

  function renderNav() {
    container = document.getElementById('nav');
    if (!container) {
      return;
    }
    container.innerHTML = '';
    for (const item of NAV_ITEMS) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'nav-item';
      btn.textContent = item.label;
      if (item.disabled) {
        btn.disabled = true;
        btn.title = item.hint || '规划中';
      } else {
        btn.dataset.view = item.id;
        if (item.id === activeId) {
          btn.classList.add('active');
          btn.setAttribute('aria-current', 'page');
        }
        btn.addEventListener('click', () => {
          if (item.id !== activeId) {
            location.hash = `#/${item.id}`;
          }
        });
      }
      container.appendChild(btn);
    }
  }

  function setActive(id) {
    activeId = id;
    if (!container) {
      return;
    }
    for (const btn of container.querySelectorAll('.nav-item[data-view]')) {
      const isActive = btn.dataset.view === id;
      btn.classList.toggle('active', isActive);
      btn.setAttribute('aria-current', isActive ? 'page' : 'false');
    }
  }

  renderNav();

  window.adminNav = { renderNav, setActive };
})();
