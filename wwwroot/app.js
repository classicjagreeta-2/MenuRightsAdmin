'use strict';

const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const state = {
  me: null,
  modules: [],
  moduleId: null,
  menu: null,        // { module, nodes }
  tree: [],          // nodes in display order with depth
  users: [],
  editing: null,     // { mode: 'new' | 'edit', parent?, node? }
  rights: null,      // { rows: Map key -> {granted, aedv, days}, original: Map }
};

// ---- HTTP ------------------------------------------------------------------------------

// The app may be hosted under a sub-folder (IIS application, e.g. /ClassicMenuController/),
// so server calls are relative to the page. Without a trailing slash the browser would
// resolve them one folder too high - add it.
if (!location.pathname.endsWith('/') && !/\.[a-z]+$/i.test(location.pathname)) {
  location.replace(location.pathname + '/' + location.search + location.hash);
}

async function call(method, url, body) {
  const res = await fetch(url.replace(/^\//, ''), {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  });
  if (res.status === 401 && !url.startsWith('/auth/')) { showLogin(); throw new Error('Signed out.'); }
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    // An HTML page instead of JSON: the request reached something other than this app.
    throw new Error(`Unexpected response from ${new URL(url.replace(/^\//, ''), location.href).pathname} (HTTP ${res.status}). ` +
                    'Check the site / application path in IIS.');
  }
  if (!res.ok) throw new Error((data && data.message) || `HTTP ${res.status}`);
  return data;
}

function toast(msg, isError) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast' + (isError ? ' err' : '');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.add('hidden'), isError ? 7000 : 3500);
}

// ---- Login -----------------------------------------------------------------------------

function showLogin() {
  $('#appView').classList.add('hidden');
  $('#loginView').classList.remove('hidden');
  $('#lgUser').focus();
}

$('#loginForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('#lgError').textContent = '';
  try {
    await call('POST', '/auth/login', { username: $('#lgUser').value, password: $('#lgPass').value });
    $('#lgPass').value = '';
    await start();
  } catch (err) {
    $('#lgError').textContent = err.message;
  }
});

$('#logoutBtn').addEventListener('click', async () => {
  await call('POST', '/auth/logout');
  showLogin();
});

async function start() {
  try {
    state.me = await call('GET', '/auth/me');
  } catch {
    showLogin();
    return;
  }
  $('#loginView').classList.add('hidden');
  $('#appView').classList.remove('hidden');
  $('#whoami').textContent = state.me.user;

  try {
    state.modules = await call('GET', '/api/modules');
  } catch (err) {
    showProblem('Could not load the module list from the database.', err.message);
    return;
  }
  if (!state.modules.length) {
    showProblem('The MODULE table returned no modules with a MENU_TABLE and USRACS_TABLE.',
      'Check that ConnectionStrings:ClassicNew in appsettings.json logs in as the ClassicNew schema.');
    return;
  }
  const sel = $('#moduleSel');
  // Ready modules first; a module whose menu table does not exist is listed but cannot be picked.
  const ordered = [...state.modules.filter((m) => m.ready), ...state.modules.filter((m) => !m.ready)];
  sel.innerHTML = ordered.map((m) => m.ready
    ? `<option value="${m.id}">${esc(m.name)} (${esc(m.code)})</option>`
    : `<option value="${m.id}" disabled>${esc(m.name)} (${esc(m.code)}) — not set up: ${esc(m.menuTable)} missing</option>`).join('');
  let saved = null;
  try { saved = localStorage.getItem('mra.module'); } catch { /* storage blocked */ }
  if (saved && state.modules.some((m) => String(m.id) === saved && m.ready)) sel.value = saved;
  else if (ordered.length && ordered[0].ready) sel.value = String(ordered[0].id);
  await loadModule();
}

// A setup problem stays on screen (a toast disappears before anyone can read it).
function showProblem(title, detail) {
  $('#menuGrid tbody').innerHTML =
    `<tr><td colspan="6"><p class="error"><b>${esc(title)}</b></p><p class="code">${esc(detail)}</p></td></tr>`;
}

$('#moduleSel').addEventListener('change', () => {
  try { localStorage.setItem('mra.module', $('#moduleSel').value); } catch { /* storage blocked */ }
  loadModule();
});

async function loadModule() {
  state.moduleId = Number($('#moduleSel').value);
  closeEditor();
  try {
    const [menu, users] = await Promise.all([
      call('GET', `/api/modules/${state.moduleId}/menu`),
      call('GET', `/api/modules/${state.moduleId}/users`),
    ]);
    state.menu = menu;
    state.users = users;
    state.tree = buildTree(menu.nodes, menu.module.root);
    renderMenu();
    renderUserSelect();
    await loadRights();
  } catch (err) {
    showProblem('Could not load this module.', err.message);
  }
}

// ---- Tree ------------------------------------------------------------------------------

// Nodes linked by PID = "<parent ID>!<parent INDEX_ORA>"; returns them in display order with depth.
function buildTree(nodes, root) {
  const byKey = new Map(nodes.map((n) => [n.key, n]));
  const kids = new Map();
  for (const n of nodes) {
    const p = byKey.has(n.pid) ? n.pid : '__root';
    if (!kids.has(p)) kids.set(p, []);
    kids.get(p).push(n);
  }
  for (const list of kids.values()) list.sort((a, b) => a.orderCode.localeCompare(b.orderCode));
  const out = [];
  const seen = new Set();
  // offAbove: a heading above this item is switched off, so the item is hidden too.
  const walk = (key, depth, offAbove) => {
    for (const n of kids.get(key) || []) {
      if (seen.has(n.key)) continue;
      seen.add(n.key);
      out.push({ ...n, depth, hasKids: kids.has(n.key), offAbove });
      walk(n.key, depth + 1, offAbove || !n.active);
    }
  };
  walk('__root', 0, false);
  return out;
}

const isScreen = (n) => !!n.controller || n.type === 'E';

function renderMenu() {
  const f = $('#menuFilter').value.trim().toLowerCase();
  const rows = state.tree.filter((n) => !f ||
    [n.name, n.orderCode, n.controller, n.id].some((v) => (v || '').toLowerCase().includes(f)));
  $('#menuGrid tbody').innerHTML = rows.map((n) => `
    <tr class="${n.active && !n.offAbove ? '' : 'inactive'}" data-key="${esc(n.key)}">
      <td class="name" style="padding-left:${10 + n.depth * 20}px">
        ${n.hasKids ? '▸ ' : ''}<span class="${isScreen(n) ? '' : 'tree-heading'}${n.depth === 0 ? ' tree-top' : ''}">${esc(n.name)}</span>
        ${isScreen(n) ? '' : '<span class="badge">heading</span>'}
      </td>
      <td class="code">${esc(n.orderCode)}</td>
      <td class="code">${esc(n.id)} / ${esc(n.index)}</td>
      <td>${n.controller ? `${esc(n.controller)}/${esc(n.action)}` : '<span class="muted">—</span>'}</td>
      <td><label class="switch" title="${n.offAbove ? 'A heading above is switched off - switch it on first' : 'Show / hide in the menu (also everything under it)'}">
        <input type="checkbox" data-act="toggle" ${n.active ? 'checked' : ''} ${n.offAbove ? 'disabled' : ''}><span></span></label></td>
      <td>
        <button class="link" data-act="add">+ Child</button>
        <button class="link" data-act="edit">Edit</button>
      </td>
    </tr>`).join('') || '<tr><td colspan="6" class="muted">No menu items.</td></tr>';
}

$('#menuFilter').addEventListener('input', renderMenu);

$('#menuGrid').addEventListener('click', async (e) => {
  const act = e.target.dataset.act;
  if (!act) return;
  const tr = e.target.closest('tr');
  const node = state.tree.find((n) => n.key === tr.dataset.key);
  if (!node) return;
  if (act === 'add') openNew(node);
  if (act === 'edit') openEdit(node);
  if (act === 'toggle') {
    const active = e.target.checked;
    // Items under this one follow it.
    const at = state.tree.indexOf(node);
    let end = at + 1;
    while (end < state.tree.length && state.tree[end].depth > node.depth) end++;
    const affected = state.tree.slice(at + 1, end).filter((d) => d.active !== active).length;
    if (affected && !confirm(`${active ? 'Switch on' : 'Switch off'} “${node.name}” and the ${affected} item(s) under it?`)) {
      e.target.checked = !active;
      return;
    }
    try {
      await call('POST', `/api/modules/${state.moduleId}/menu/update`, { key: node.key, active });
      toast(`${node.name}${affected ? ` and ${affected} item(s) under it` : ''} ${active ? 'activated' : 'deactivated'}.`);
      await reloadMenu();
    } catch (err) {
      e.target.checked = !active;
      toast(err.message, true);
    }
  }
});

async function reloadMenu() {
  state.menu = await call('GET', `/api/modules/${state.moduleId}/menu`);
  state.tree = buildTree(state.menu.nodes, state.menu.module.root);
  renderMenu();
}

// ---- Editor ----------------------------------------------------------------------------

$('#addTopBtn').addEventListener('click', () => openNew(null));
$('#editClose').addEventListener('click', closeEditor);
$('#editCancel').addEventListener('click', closeEditor);
document.querySelectorAll('input[name=fKind]').forEach((r) => r.addEventListener('change', syncKind));

function syncKind() {
  const screen = document.querySelector('input[name=fKind]:checked').value === 'screen';
  $('#fScreenBox').classList.toggle('hidden', !screen);
}

function closeEditor() {
  state.editing = null;
  $('#editPanel').classList.add('hidden');
}

async function openNew(parent) {
  state.editing = { mode: 'new', parent };
  $('#editTitle').textContent = 'New menu item';
  $('#editParent').textContent = parent ? `Under: ${parent.name} (${parent.orderCode})` : 'Top level';
  $('#editForm').reset();
  $('#fAction').value = 'Index';
  $('#fKindRow').classList.remove('hidden');
  $('#fCodeBox').classList.remove('hidden');
  $('#fGrantBox').classList.remove('hidden');
  $('#fActiveRow').classList.add('hidden');
  $('#fOrderCode').disabled = false;
  $('#fId').disabled = false;
  $('#fId').readOnly = false;
  $('#editError').textContent = '';
  syncKind();

  const defaults = new Set((state.me.defaultGrantUsers || []).map((u) => u.toUpperCase()));
  $('#fGrantList').innerHTML = state.users.map((u, i) => `
    <label class="inline"><input type="checkbox" data-i="${i}" ${defaults.has(u.user) ? 'checked' : ''}>
      ${esc(u.user)} <span class="muted small">@ ${esc(u.schema)}</span></label>`).join('')
    || '<span class="muted small">No users are linked to this module (USER_SCHEMA_LINK).</span>';

  $('#editPanel').classList.remove('hidden');
  $('#fName').focus();
  try {
    const s = await call('GET', `/api/modules/${state.moduleId}/suggest?parent=${encodeURIComponent(parent ? parent.key : '')}`);
    state.editing.siblingId = s.id;
    state.editing.idTouched = false;
    $('#fOrderCode').value = s.orderCode || '';
    $('#fId').value = s.id || '';
    $('#fId').readOnly = !!s.id;
    $('#fHint').textContent = `PID ${s.pid}` +
      (s.id ? ` · same ID as the other items here: ${s.id} / ${s.index}`
            : ' · first item under this parent: a new Menu ID is suggested from the name (must not be used elsewhere)') +
      (s.orderCode ? '' : ' · no free ORDERCODE at this level, enter one');
    let warn = '';
    if (parent && isScreen(parent)) warn = `“${parent.name}” is a screen, not a heading — items under a screen usually do not show as a sub-menu.`;
    $('#editError').textContent = warn;
  } catch (err) {
    $('#editError').textContent = err.message;
  }
}

// First child of a parent: propose an unused ID from the menu name, e.g. "Cultivation Job Entry" -> CULT_JOB.
function proposeId(name) {
  const words = name.toUpperCase().replace(/[^A-Z0-9 ]/g, ' ').split(/\s+/).filter(Boolean);
  if (!words.length) return '';
  let base = words.length === 1 ? words[0].slice(0, 8) : `${words[0].slice(0, 4)}_${words[1].slice(0, 6)}`;
  if (!/^[A-Z]/.test(base)) base = 'M' + base;
  base = base.slice(0, 9);
  const used = new Set(state.tree.map((n) => n.id));
  if (!used.has(base)) return base;
  for (let i = 1; i < 100; i++) if (!used.has(base + i)) return base + i;
  return '';
}

$('#fName').addEventListener('input', () => {
  const ed = state.editing;
  if (ed && ed.mode === 'new' && !ed.siblingId && !ed.idTouched) $('#fId').value = proposeId($('#fName').value);
});
$('#fId').addEventListener('input', () => { if (state.editing) state.editing.idTouched = true; });

// MVC controller names go in without the "Controller" suffix.
$('#fController').addEventListener('blur', () => {
  const v = $('#fController').value.trim();
  if (/controller$/i.test(v) && v.length > 10) $('#fController').value = v.slice(0, -10);
});

function openEdit(node) {
  state.editing = { mode: 'edit', node };
  $('#editTitle').textContent = 'Edit menu item';
  $('#editParent').textContent = `${node.orderCode} · ${node.id} / ${node.index} · PID ${node.pid}`;
  $('#editForm').reset();
  $('#fName').value = node.name;
  $('#fKindRow').classList.add('hidden');
  $('#fCodeBox').classList.add('hidden');
  $('#fGrantBox').classList.add('hidden');
  $('#fActiveRow').classList.remove('hidden');
  $('#fActive').checked = node.active;
  $('#fActive').disabled = !!node.offAbove;   // a heading above is off
  $('#fScreenBox').classList.toggle('hidden', !isScreen(node));
  $('#fController').value = node.controller || '';
  $('#fAction').value = node.action || 'Index';
  $('#fHint').textContent = '';
  $('#editError').textContent = '';
  $('#editPanel').classList.remove('hidden');
  $('#fName').focus();
}

$('#editForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const ed = state.editing;
  if (!ed) return;
  $('#editError').textContent = '';
  $('#editSave').disabled = true;
  try {
    if (ed.mode === 'new') {
      const screen = document.querySelector('input[name=fKind]:checked').value === 'screen';
      const grantTo = [...document.querySelectorAll('#fGrantList input:checked')]
        .map((c) => state.users[Number(c.dataset.i)])
        .map((u) => ({ user: u.user, schema: u.schema }));
      const created = await call('POST', `/api/modules/${state.moduleId}/menu`, {
        name: $('#fName').value,
        parentKey: ed.parent ? ed.parent.key : null,
        orderCode: $('#fOrderCode').value,
        id: $('#fId').value,
        isScreen: screen,
        controller: screen ? $('#fController').value : null,
        action: screen ? $('#fAction').value : null,
        grantTo,
      });
      toast(`Created ${created.name} (${created.orderCode}).`);
    } else {
      await call('POST', `/api/modules/${state.moduleId}/menu/update`, {
        key: ed.node.key,
        name: $('#fName').value,
        active: $('#fActive').checked,
        controller: isScreen(ed.node) ? $('#fController').value : null,
        action: isScreen(ed.node) ? $('#fAction').value : null,
      });
      toast('Saved.');
    }
    closeEditor();
    await reloadMenu();
    await loadRights();
  } catch (err) {
    $('#editError').textContent = err.message;
  } finally {
    $('#editSave').disabled = false;
  }
});

// ---- Rights ----------------------------------------------------------------------------

function renderUserSelect() {
  const sel = $('#userSel');
  sel.innerHTML = state.users.map((u, i) => `<option value="${i}">${esc(u.user)} @ ${esc(u.schema)}</option>`).join('');
  const defaults = (state.me.defaultGrantUsers || []).map((u) => u.toUpperCase());
  const core = state.users.findIndex((u) => defaults.includes(u.user));
  sel.value = String(core >= 0 ? core : 0);
}

$('#userSel').addEventListener('change', () => loadRights());
$('#rightsReload').addEventListener('click', () => loadRights());
$('#rightsFilter').addEventListener('input', renderRights);

async function loadRights() {
  const u = state.users[Number($('#userSel').value)];
  if (!u) {
    state.rights = null;
    $('#rightsGrid tbody').innerHTML = '<tr><td colspan="10" class="muted">No users are linked to this module.</td></tr>';
    return;
  }
  try {
    const data = await call('GET', `/api/modules/${state.moduleId}/rights?user=${encodeURIComponent(u.user)}&schema=${encodeURIComponent(u.schema)}`);
    const have = new Map(data.rights.map((r) => [`${r.id}!${r.index}`, r]));
    const rows = new Map();
    for (const n of state.tree) {
      const r = have.get(n.key);
      const def = state.me.defaultDays ?? 365;
      const day = (v) => (r && r.aedv && v != null ? v : def);   // headings / new rows start at the default
      rows.set(n.key, { granted: !!r, aedv: (r && r.aedv) || '', aday: day(r && r.aday), eday: day(r && r.eday), dday: day(r && r.dday) });
    }
    state.rights = { user: u, rows, original: new Map([...rows].map(([k, v]) => [k, { ...v }])) };
    renderRights();
    $('#rightsMsg').textContent = `${data.rights.length} rights rows for ${u.user} in ${u.schema}.`;
  } catch (err) {
    toast(err.message, true);
  }
}

function isChanged(key) {
  const a = state.rights.rows.get(key), b = state.rights.original.get(key);
  return a.granted !== b.granted || (a.granted && (a.aedv !== b.aedv ||
    (a.aedv && (a.aday !== b.aday || a.eday !== b.eday || a.dday !== b.dday))));
}

function renderRights() {
  if (!state.rights) return;
  const f = $('#rightsFilter').value.trim().toLowerCase();
  const rows = state.tree.filter((n) => !f || [n.name, n.orderCode, n.controller].some((v) => (v || '').toLowerCase().includes(f)));
  $('#rightsGrid tbody').innerHTML = rows.map((n) => {
    const r = state.rights.rows.get(n.key);
    const screen = isScreen(n);
    const box = (c) => screen
      ? `<input type="checkbox" data-aedv="${c}" ${r.aedv.includes(c) ? 'checked' : ''} ${r.granted ? '' : 'disabled'}>`
      : '';
    return `
      <tr data-key="${esc(n.key)}" class="${isChanged(n.key) ? 'changed' : ''} ${n.active && !n.offAbove ? '' : 'inactive'}">
        <td class="name" style="padding-left:${10 + n.depth * 20}px">
          <span class="${screen ? '' : 'tree-heading'}${n.depth === 0 ? ' tree-top' : ''}">${esc(n.name)}</span>
        </td>
        <td class="code">${esc(n.orderCode)}</td>
        <td><input type="checkbox" data-grant ${r.granted ? 'checked' : ''}></td>
        <td>${box('A')}</td><td>${box('E')}</td><td>${box('D')}</td><td>${box('V')}</td>
        ${['aday', 'eday', 'dday'].map((f) => `<td>${screen
          ? `<input type="number" min="0" max="9999" data-day="${f}" value="${r[f]}" ${r.granted && r.aedv ? '' : 'disabled'}>`
          : ''}</td>`).join('')}
      </tr>`;
  }).join('');
  const n = [...state.rights.rows.keys()].filter(isChanged).length;
  $('#rightsSave').textContent = n ? `Save changes (${n})` : 'Save changes';
}

$('#rightsGrid').addEventListener('change', (e) => {
  const tr = e.target.closest('tr');
  if (!tr || !state.rights) return;
  const key = tr.dataset.key;
  const r = state.rights.rows.get(key);
  const node = state.tree.find((n) => n.key === key);
  if (e.target.hasAttribute('data-grant')) {
    r.granted = e.target.checked;
    // A newly granted screen starts with full rights.
    if (r.granted && !r.aedv && isScreen(node)) r.aedv = 'AEDV';
  } else if (e.target.dataset.aedv) {
    const set = new Set(r.aedv.split(''));
    e.target.checked ? set.add(e.target.dataset.aedv) : set.delete(e.target.dataset.aedv);
    r.aedv = 'AEDV'.split('').filter((c) => set.has(c)).join('');
  } else if (e.target.dataset.day) {
    r[e.target.dataset.day] = Math.max(0, Math.min(9999, Number(e.target.value) || 0));
  }
  renderRights();
});

function setAll(granted) {
  if (!state.rights) return;
  for (const n of state.tree) {
    const r = state.rights.rows.get(n.key);
    r.granted = granted;
    if (granted && !r.aedv && isScreen(n)) r.aedv = 'AEDV';
  }
  renderRights();
}
$('#grantAllBtn').addEventListener('click', () => setAll(true));
$('#allDaysBtn').addEventListener('click', () => {
  if (!state.rights || $('#allDays').value === '') return;
  const v = Math.max(0, Math.min(9999, Number($('#allDays').value) || 0));
  for (const n of state.tree) {
    const r = state.rights.rows.get(n.key);
    if (r.granted && isScreen(n)) { r.aday = v; r.eday = v; r.dday = v; }
  }
  renderRights();
});
$('#revokeAllBtn').addEventListener('click', () => {
  if (confirm('Untick access to every menu item for this user? Nothing is saved until you press Save.')) setAll(false);
});

$('#rightsSave').addEventListener('click', async () => {
  if (!state.rights) return;
  const { user, rows } = state.rights;
  const removing = [...rows.entries()].filter(([k, r]) => !r.granted && state.rights.original.get(k).granted).length;
  if (removing && !confirm(`This removes ${removing} rights row(s) for ${user.user}. Continue?`)) return;
  const items = state.tree.map((n) => {
    const r = rows.get(n.key);
    const o = state.rights.original.get(n.key);
    // Only changed day values are sent; null = keep what the row has (empty days are not filled in silently).
    const day = (f) => (r[f] !== o[f] ? r[f] : null);
    return { id: n.id, index: n.index, granted: r.granted, aedv: r.aedv, aday: day('aday'), eday: day('eday'), dday: day('dday') };
  });
  $('#rightsSave').disabled = true;
  try {
    const res = await call('POST', `/api/modules/${state.moduleId}/rights/save`, { user: user.user, schema: user.schema, items });
    toast(`Saved: ${res.added} added, ${res.changed} changed, ${res.removed} removed.`);
    await loadRights();
  } catch (err) {
    toast(err.message, true);
  } finally {
    $('#rightsSave').disabled = false;
  }
});

// ---- Tabs ------------------------------------------------------------------------------

document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => {
  document.querySelectorAll('.tab').forEach((x) => x.classList.toggle('active', x === b));
  document.querySelectorAll('.tabpane').forEach((p) => p.classList.toggle('hidden', p.id !== `tab-${b.dataset.tab}`));
}));

start();
