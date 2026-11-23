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
  // Phone: a top bar with the search, and the bottom menu of admin design §5.2 (Home, Knowledge bases, Assistants,
  // Feedback, Menu); each target is at least 48 px high.
  const tab = (id, icon, label) => `<div class="${id === active ? 'on' : ''}"><span class="ms">${icon}</span>${label}</div>`
  const mtop = `<div class="mtop"><span class="ms">hub</span><b>Källa</b><span class="btn ghost icon" aria-label="Search"><span class="ms">search</span></span></div>`
  const bottom = `<nav class="bottom" aria-label="Main">${tab('home', 'space_dashboard', 'Home')}${tab('collections', 'library_books', 'Knowledge')}${tab('assistants', 'smart_toy', 'Assistants')}${tab('feedback', 'thumbs_up_down', 'Feedback')}${tab('menu', 'menu', 'Menu')}</nav>`
  // Tables become cards on a phone: each cell gets its column's name as a label.
  for (const t of document.querySelectorAll('table')) {
    t.classList.add('rt')
    const heads = [...t.querySelectorAll('tr:first-child th')].map((th) => th.textContent.trim())
    for (const tr of t.querySelectorAll('tr')) [...tr.children].forEach((td, i) => { if (td.tagName === 'TD') { td.dataset.label = heads[i] ?? ''; if (i === 0) td.classList.add('first') } })
  }
  const app = document.querySelector('.app')
  app.insertAdjacentHTML('afterbegin', side)
  document.querySelector('.main').insertAdjacentHTML('afterbegin', mtop + top)
  document.body.insertAdjacentHTML('beforeend', bottom)
})()
