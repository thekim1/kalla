// The proposed side menu and top bar (UX1), shared by every mockup. <body data-active="assistants"> marks the page.
(function () {
  const active = document.body.dataset.active
  const item = (id, icon, label, extra = '') =>
    `<div class="nav${id === active ? ' on' : ''}"><span class="ms">${icon}</span>${label}${extra}</div>`
  const side = `
    <aside class="side">
      <div class="brand"><div class="logo"><span class="ms">hub</span></div><div><b>Källa</b><small>Umeå kommun</small></div></div>
      ${item('home', 'space_dashboard', 'Home', '<span class="count">3</span>')}
      <div class="grp">Build</div>
      ${item('collections', 'library_books', 'Knowledge bases')}
      ${item('assistants', 'smart_toy', 'Assistants')}
      <div class="grp">Follow-up</div>
      ${item('feedback', 'thumbs_up_down', 'Feedback')}
      ${item('costs', 'payments', 'Costs')}
      ${item('map', 'account_tree', 'Map')}
      <div class="grp">Administration</div>
      ${item('apps', 'apps', 'Apps and keys')}
      ${item('models', 'memory', 'Models')}
      ${item('settings', 'tune', 'Settings')}
      ${item('organisation', 'corporate_fare', 'Organisation')}
      ${item('audit', 'history', 'Audit log')}
      <div class="spacer"></div>
      <div class="me"><div class="avatar">AA</div><div><b>Anna Andersson</b><small>Kommunledningskontoret</small></div><span class="ms">unfold_more</span></div>
    </aside>`
  const top = `
    <div class="top">
      <div class="search"><span class="ms">search</span>Search<kbd>Ctrl K</kbd></div>
      <span class="env">Development</span>
    </div>`
  const app = document.querySelector('.app')
  app.insertAdjacentHTML('afterbegin', side)
  document.querySelector('.main').insertAdjacentHTML('afterbegin', top)
})()
