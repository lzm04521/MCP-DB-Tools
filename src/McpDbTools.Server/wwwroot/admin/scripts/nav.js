/* 侧边导航：数据驱动渲染纵向菜单（内联 SVG 图标 + 文字），点击切换 hash，由 shell 路由器统一处理视图切换。
   - 已实现页面用 button[data-view]，点击改 location.hash，不触发整页刷新。
   - 未实现/规划中的页面置 disabled:true，渲染为禁用按钮并显示 hint。
   - setActive(id) 供 shell 在视图切换后调用，高亮当前项。
   - 图标为内联 SVG（24 viewBox / stroke=currentColor / stroke-width=2 / 线性圆角风格），
     文字包在 .nav-item__label 内，860px 以下由 CSS 隐藏文字、收窄为图标条。 */
(function () {
  const ICONS = {
    projects: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5"/><path d="M3 12c0 1.66 4 3 9 3s9-1.34 9-3"/></svg>',
    transfer: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3 4 7l4 4"/><path d="M4 7h16"/><path d="m16 21 4-4-4-4"/><path d="M20 17H4"/></svg>',
    keywords: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1 1 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/></svg>',
    'audit-log': '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M10 9H8"/><path d="M16 13H8"/><path d="M16 17H8"/></svg>',
    backups: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="20" height="5" x="2" y="3" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8"/><path d="M10 12h4"/></svg>',
    settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="21" x2="14" y1="4" y2="4"/><line x1="10" x2="3" y1="4" y2="4"/><line x1="21" x2="12" y1="12" y2="12"/><line x1="8" x2="3" y1="12" y2="12"/><line x1="21" x2="16" y1="20" y2="20"/><line x1="12" x2="3" y1="20" y2="20"/><line x1="14" x2="14" y1="2" y2="6"/><line x1="8" x2="8" y1="10" y2="14"/><line x1="16" x2="16" y1="18" y2="22"/></svg>',
    system: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M15 2v2"/><path d="M15 20v2"/><path d="M2 15h2"/><path d="M2 9h2"/><path d="M20 15h2"/><path d="M20 9h2"/><path d="M9 2v2"/><path d="M9 20v2"/></svg>'
  };

  const NAV_ITEMS = [
    { id: 'projects', label: '项目配置' },
    { id: 'transfer', label: '配置迁移' },
    { id: 'keywords', label: '全局关键字' },
    { id: 'audit-log', label: '审计日志' },
    { id: 'backups', label: '备份管理' },
    { id: 'settings', label: '全局设置' },
    { id: 'system', label: '系统设置' }
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
      btn.innerHTML = `${ICONS[item.id] || ''}<span class="nav-item__label"></span>`;
      btn.querySelector('.nav-item__label').textContent = item.label;
      if (item.disabled) {
        btn.disabled = true;
        btn.title = item.hint || '规划中';
      } else {
        btn.dataset.view = item.id;
        btn.title = item.label; /* 860px 图标条模式下 hover 提示 */
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
