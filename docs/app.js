// Interactive explainer for the movie-recommender repo.
// Everything reads from data/movies.json, produced by `dotnet run -- export-viz`.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const $ = (sel) => document.querySelector(sel);
const el = (tag, attrs = {}, parent) => {
  const n = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
  if (parent) parent.appendChild(n);
  return n;
};
const fmt = (n) => n.toLocaleString('en-US');
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

// Genre palette — first genre of each movie decides its colour.
const GENRE_COLORS = {
  Action: '#fc8181', Adventure: '#f6ad55', Animation: '#f6e05e', Children: '#faf089',
  Comedy: '#68d391', Crime: '#b794f4', Documentary: '#a0aec0', Drama: '#63b3ed',
  Fantasy: '#d6bcfa', 'Film-Noir': '#718096', Horror: '#e53e3e', IMAX: '#cbd5e0',
  Musical: '#fbb6ce', Mystery: '#9f7aea', Romance: '#f687b3', 'Sci-Fi': '#4fd1c5',
  Thriller: '#ed8936', War: '#c05621', Western: '#d69e2e', '': '#718096',
};

const data = await fetch('data/movies.json', { cache: 'no-cache' }).then((r) => r.json());
const movies = data.movies;

// Each section is independent: a failure in one (e.g. no WebGL) must not take the rest down.
for (const init of [heroBackground, embeddingDemo, vectorSpace, distancePlayground, cosineMatrix, scanAnimation, annAnimation, pipelineDiagram, ragDiagram]) {
  try { init(); } catch (err) { console.error(`${init.name} failed:`, err); }
}

// ---------------------------------------------------------------- hero background
function heroBackground() {
  const svg = $('#hero-bg');
  const W = 1200, H = 600;
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  svg.setAttribute('preserveAspectRatio', 'xMidYMid slice');
  const pts = Array.from({ length: 70 }, () => ({
    x: Math.random() * W, y: Math.random() * H,
    vx: (Math.random() - 0.5) * 0.25, vy: (Math.random() - 0.5) * 0.25,
    r: 1 + Math.random() * 2,
  }));
  const lines = el('g', { stroke: '#7c9cff', 'stroke-opacity': 0.18, 'stroke-width': 1 }, svg);
  const dots = el('g', { fill: '#7c9cff' }, svg);
  const dotEls = pts.map((p) => el('circle', { r: p.r, opacity: 0.7 }, dots));
  const lineEls = [];
  const step = () => {
    for (const p of pts) {
      p.x = (p.x + p.vx + W) % W; p.y = (p.y + p.vy + H) % H;
    }
    dotEls.forEach((c, i) => { c.setAttribute('cx', pts[i].x); c.setAttribute('cy', pts[i].y); });
    let li = 0;
    for (let i = 0; i < pts.length; i++) for (let j = i + 1; j < pts.length; j++) {
      const dx = pts[i].x - pts[j].x, dy = pts[i].y - pts[j].y, d2 = dx * dx + dy * dy;
      if (d2 < 130 * 130) {
        const l = lineEls[li] ?? (lineEls[li] = el('line', {}, lines));
        l.setAttribute('x1', pts[i].x); l.setAttribute('y1', pts[i].y);
        l.setAttribute('x2', pts[j].x); l.setAttribute('y2', pts[j].y);
        l.setAttribute('stroke-opacity', 0.22 * (1 - Math.sqrt(d2) / 130));
        li++;
      }
    }
    for (let k = li; k < lineEls.length; k++) lineEls[k].setAttribute('stroke-opacity', 0);
    if (!reduceMotion) requestAnimationFrame(step);
  };
  step();
}

// ---------------------------------------------------------------- 01 embedding demo
function embeddingDemo() {
  const pick = $('#embed-pick');
  const text = $('#embed-text');
  const bars = $('#embed-bars');
  const tail = $('#embed-tail');
  const flow = $('#embed-flow');

  for (const f of data.featured) {
    const o = document.createElement('option');
    o.value = f.i; o.textContent = `${movies[f.i].t} (${movies[f.i].y})`;
    pick.appendChild(o);
  }

  const maxAbs = Math.max(...data.featured.flatMap((f) => f.v.map(Math.abs)));
  const rects = Array.from({ length: data.dimensions }, (_, i) =>
    el('rect', { x: i, width: 1, y: 60, height: 0 }, bars));
  el('line', { x1: 0, x2: 768, y1: 60, y2: 60, stroke: '#273043', 'stroke-width': 0.5 }, bars);

  // Little animated "model" between text and vector.
  flow.innerHTML = `
    <defs><marker id="arr" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
      <path d="M0 0 L10 5 L0 10 z" fill="#7c9cff"/></marker></defs>
    <line x1="0" y1="100" x2="30" y2="100" stroke="#7c9cff" stroke-width="2" marker-end="url(#arr)"/>
    <rect x="32" y="60" width="56" height="80" rx="10" fill="#1b2232" stroke="#7c9cff"/>
    <text x="60" y="92" fill="#e6e9f0" font-size="9" text-anchor="middle" font-family="monospace">embedding</text>
    <text x="60" y="104" fill="#e6e9f0" font-size="9" text-anchor="middle" font-family="monospace">model</text>
    <text x="60" y="122" fill="#8b94a7" font-size="7" text-anchor="middle">${data.model}</text>
    <line x1="90" y1="100" x2="118" y2="100" stroke="#7c9cff" stroke-width="2" marker-end="url(#arr)"/>
    <g id="flow-dots"></g>`;
  const dotsG = flow.querySelector('#flow-dots');
  const flowDots = Array.from({ length: 4 }, (_, i) => el('circle', { r: 2.5, cy: 100, fill: '#4fd1c5' }, dotsG));
  if (!reduceMotion) {
    let t0 = performance.now();
    const anim = (t) => {
      const p = ((t - t0) / 1800) % 1;
      flowDots.forEach((d, i) => {
        const q = (p + i / 4) % 1;
        d.setAttribute('cx', q * 120);
        d.setAttribute('opacity', q > 0.27 && q < 0.73 ? 0 : 1);
      });
      requestAnimationFrame(anim);
    };
    requestAnimationFrame(anim);
  }

  const show = (idx) => {
    const f = data.featured.find((f) => f.i === idx);
    const txt = f.txt ?? '';
    const pfx = 'search_document: ';
    text.innerHTML = txt.startsWith(pfx)
      ? `<span class="pfx">${pfx}</span>${escapeHtml(txt.slice(pfx.length))}`
      : escapeHtml(txt);
    f.v.forEach((v, i) => {
      const h = Math.abs(v) / maxAbs * 58;
      rects[i].setAttribute('height', h);
      rects[i].setAttribute('y', v >= 0 ? 60 - h : 60);
      rects[i].setAttribute('fill', v >= 0 ? '#7c9cff' : '#4fd1c5');
    });
    tail.textContent = `[${f.v.slice(0, 8).map((v) => v.toFixed(4)).join(', ')}, … ${f.v.length - 8} more]`;
  };
  pick.addEventListener('change', () => show(+pick.value));
  show(data.featured[0].i);

  // Provider tabs
  document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach((x) => x.classList.toggle('active', x === b));
    document.querySelectorAll('.tab-panel').forEach((p) => p.classList.toggle('active', p.dataset.panel === b.dataset.tab));
  }));
}

// ---------------------------------------------------------------- 02 three.js space
function vectorSpace() {
  const host = $('#space-canvas');
  const tooltip = $('#space-tooltip');
  $('#space-count').textContent = fmt(data.count);
  document.querySelectorAll('[data-count]').forEach((e) => { e.textContent = fmt(data.count); });
  $('#space-var').textContent = (data.explainedVariance.reduce((a, b) => a + b, 0) * 100).toFixed(1) + '%';

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(50, 1, 0.01, 100);
  camera.position.set(0, 0.6, 3.2);
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  } catch {
    $('#space-loading').textContent = 'WebGL is not available in this browser, so the 3-D view cannot be shown.';
    return;
  }
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  host.appendChild(renderer.domElement);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true; controls.autoRotate = !reduceMotion; controls.autoRotateSpeed = 0.6;
  controls.minDistance = 1.2; controls.maxDistance = 6;

  // Points
  const n = movies.length;
  const pos = new Float32Array(n * 3);
  const col = new Float32Array(n * 3);
  const baseCol = new Float32Array(n * 3);
  const size = new Float32Array(n);
  const tmp = new THREE.Color();
  const genreCount = {};
  movies.forEach((m, i) => {
    pos.set(m.p, i * 3);
    const g = m.g.split('|')[0] ?? '';
    genreCount[g] = (genreCount[g] ?? 0) + 1;
    tmp.set(GENRE_COLORS[g] ?? '#718096');
    baseCol.set([tmp.r, tmp.g, tmp.b], i * 3);
    size[i] = 0.012 + Math.min(m.n, 300) / 300 * 0.03;
  });
  col.set(baseCol);
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
  geo.setAttribute('aSize', new THREE.BufferAttribute(size, 1));
  const mat = new THREE.ShaderMaterial({
    transparent: true, depthWrite: false,
    uniforms: { uScale: { value: 1 } },
    vertexShader: `
      attribute float aSize; attribute vec3 color; varying vec3 vColor; uniform float uScale;
      void main() { vColor = color; vec4 mv = modelViewMatrix * vec4(position, 1.0);
        gl_PointSize = aSize * uScale * (300.0 / -mv.z); gl_Position = projectionMatrix * mv; }`,
    fragmentShader: `
      varying vec3 vColor;
      void main() { float d = length(gl_PointCoord - 0.5); if (d > 0.5) discard;
        float a = smoothstep(0.5, 0.25, d); gl_FragColor = vec4(vColor, a * 0.9); }`,
  });
  const points = new THREE.Points(geo, mat);
  scene.add(points);

  // Faint axes box so the projection has a frame of reference.
  const box = new THREE.LineSegments(
    new THREE.EdgesGeometry(new THREE.BoxGeometry(2.1, 2.1, 2.1)),
    new THREE.LineBasicMaterial({ color: 0x273043, transparent: true, opacity: 0.6 }));
  scene.add(box);

  // Highlight lines (neighbours) and query marker
  const lineGeo = new THREE.BufferGeometry();
  const lineMat = new THREE.LineBasicMaterial({ color: 0x4fd1c5, transparent: true, opacity: 0.85 });
  const lines = new THREE.LineSegments(lineGeo, lineMat);
  scene.add(lines);
  const marker = new THREE.Mesh(new THREE.OctahedronGeometry(0.035), new THREE.MeshBasicMaterial({ color: 0xf6e05e }));
  marker.visible = false; scene.add(marker);
  const ring = new THREE.Mesh(new THREE.RingGeometry(0.035, 0.045, 32), new THREE.MeshBasicMaterial({ color: 0xffffff, side: THREE.DoubleSide }));
  ring.visible = false; scene.add(ring);

  // Legend
  const legend = $('#genre-legend');
  Object.entries(genreCount).sort((a, b) => b[1] - a[1]).slice(0, 12).forEach(([g, c]) => {
    const d = document.createElement('div');
    d.innerHTML = `<i style="background:${GENRE_COLORS[g] ?? '#718096'}"></i>${g || 'none'} <span class="muted">${c}</span>`;
    legend.appendChild(d);
  });

  // Interaction state
  let hovered = -1, pinned = -1, activeQuery = -1;
  const raycaster = new THREE.Raycaster();
  raycaster.params.Points.threshold = 0.03;
  const mouse = new THREE.Vector2(2, 2);

  const setLines = (from, toIdx) => {
    const arr = new Float32Array(toIdx.length * 6);
    toIdx.forEach((j, k) => { arr.set(from, k * 6); arr.set(movies[j].p, k * 6 + 3); });
    lineGeo.setAttribute('position', new THREE.BufferAttribute(arr, 3));
    lineGeo.computeBoundingSphere();
  };
  const recolor = () => {
    const dim = $('#opt-dim').checked && (pinned >= 0 || activeQuery >= 0);
    const hi = new Set();
    if (pinned >= 0) { hi.add(pinned); movies[pinned].nn.forEach((j) => hi.add(j)); }
    if (activeQuery >= 0) data.queries[activeQuery].top.forEach((t) => hi.add(t.i));
    for (let i = 0; i < n; i++) {
      const f = dim && !hi.has(i) ? 0.18 : 1;
      col[i * 3] = baseCol[i * 3] * f; col[i * 3 + 1] = baseCol[i * 3 + 1] * f; col[i * 3 + 2] = baseCol[i * 3 + 2] * f;
    }
    geo.attributes.color.needsUpdate = true;
  };
  const pin = (i) => {
    pinned = i; activeQuery = -1;
    document.querySelectorAll('#query-list button').forEach((b) => b.classList.remove('active'));
    marker.visible = false;
    if (i < 0) { lineGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(0), 3)); $('#pinned-block').hidden = true; recolor(); return; }
    const m = movies[i];
    setLines(m.p, m.nn);
    ring.position.set(...m.p); ring.visible = true; ring.lookAt(camera.position);
    $('#pinned-block').hidden = false;
    $('#pinned').innerHTML = `<b>${escapeHtml(m.t)} (${m.y})</b><span class="muted small">${m.g.replaceAll('|', ' · ')}</span>
      <p class="muted small" style="margin:.3rem 0">Nearest in 768-D:</p>
      <ol>${m.nn.map((j) => `<li>${escapeHtml(movies[j].t)} <span class="muted">(${movies[j].y})</span></li>`).join('')}</ol>
      <button class="btn" style="margin-top:.6rem;padding:.35rem .7rem;font-size:.8rem" id="unpin">clear</button>`;
    $('#unpin').onclick = () => pin(-1);
    recolor();
  };
  const runQuery = (qi) => {
    pinned = -1; ring.visible = false; $('#pinned-block').hidden = true;
    activeQuery = qi;
    document.querySelectorAll('#query-list button').forEach((b, k) => b.classList.toggle('active', k === qi));
    const q = data.queries[qi];
    marker.position.set(...q.pos); marker.visible = true;
    setLines(q.pos, q.top.map((t) => t.i));
    $('#pinned-block').hidden = false;
    $('#pinned').innerHTML = `<b style="color:#f6e05e">“${escapeHtml(q.text)}”</b>
      <p class="muted small" style="margin:.3rem 0">Top 8 by cosine distance:</p>
      <ol>${q.top.map((t) => `<li>${escapeHtml(movies[t.i].t)} <span class="d">${t.dist.toFixed(3)}</span></li>`).join('')}</ol>
      <button class="btn" style="margin-top:.6rem;padding:.35rem .7rem;font-size:.8rem" id="unpin">clear</button>`;
    $('#unpin').onclick = () => pin(-1);
    recolor();
  };

  const ql = $('#query-list');
  data.queries.forEach((q, i) => {
    const b = document.createElement('button');
    b.textContent = q.text; b.onclick = () => runQuery(i); ql.appendChild(b);
  });
  $('#opt-spin').addEventListener('change', (e) => (controls.autoRotate = e.target.checked));
  $('#opt-dim').addEventListener('change', recolor);

  renderer.domElement.addEventListener('pointermove', (e) => {
    const r = renderer.domElement.getBoundingClientRect();
    mouse.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
    tooltip.style.left = e.clientX - r.left + 14 + 'px';
    tooltip.style.top = e.clientY - r.top + 14 + 'px';
  });
  renderer.domElement.addEventListener('pointerleave', () => mouse.set(2, 2));
  let downAt = null;
  renderer.domElement.addEventListener('pointerdown', (e) => (downAt = [e.clientX, e.clientY]));
  renderer.domElement.addEventListener('pointerup', (e) => {
    if (downAt && Math.hypot(e.clientX - downAt[0], e.clientY - downAt[1]) < 4 && hovered >= 0) pin(hovered);
    downAt = null;
  });

  // Double-click toggles full screen; Esc leaves it. The ResizeObserver below re-fits the renderer.
  const wrap = host.closest('.space-wrap');
  const setFullscreen = (on) => {
    wrap.classList.toggle('fullscreen', on);
    document.body.classList.toggle('space-fullscreen', on);
  };
  renderer.domElement.addEventListener('dblclick', () => setFullscreen(!wrap.classList.contains('fullscreen')));
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') setFullscreen(false); });

  const resize = () => {
    const w = host.clientWidth, h = host.clientHeight;
    if (!w || !h) return;
    // updateStyle=true pins the CSS size to w×h; the backing store is w×h×devicePixelRatio.
    renderer.setSize(w, h, true);
    camera.aspect = w / h; camera.updateProjectionMatrix();
    mat.uniforms.uScale.value = h / 620;
  };
  new ResizeObserver(resize).observe(host);
  resize();
  $('#space-loading').remove();

  const render = () => {
    controls.update();
    raycaster.setFromCamera(mouse, camera);
    const hit = raycaster.intersectObject(points)[0];
    const idx = hit ? hit.index : -1;
    if (idx !== hovered) {
      hovered = idx;
      if (idx >= 0) {
        const m = movies[idx];
        tooltip.hidden = false;
        tooltip.innerHTML = `<b>${escapeHtml(m.t)} (${m.y})</b><span class="g">${m.ty === 'tv' ? 'TV series · ' : ''}${m.g.replaceAll('|', ' · ')}</span>
          <div class="muted">★ ${m.r ?? '–'} · ${fmt(m.n)} ${m.s === 'tmdb' ? 'TMDB votes' : 'ratings'}</div>${m.o ? `<div class="muted small">${escapeHtml(m.o)}…</div>` : ''}`;
        host.style.cursor = 'pointer';
      } else { tooltip.hidden = true; host.style.cursor = ''; }
    }
    if (ring.visible) ring.lookAt(camera.position);
    if (marker.visible) marker.rotation.y += 0.02;
    renderer.render(scene, camera);
    requestAnimationFrame(render);
  };
  render();
}

// ---------------------------------------------------------------- 03 distance playground
function distancePlayground() {
  const svg = $('#dist-svg');
  const C = 210, S = 150;
  const a = { x: 0.8, y: 0.3 }, b = { x: 0.35, y: 0.75 };
  const toPx = (v) => [C + v.x * S, C - v.y * S];
  const fromPx = (px, py) => ({ x: (px - C) / S, y: (C - py) / S });

  svg.innerHTML = `
    <defs>
      <marker id="arrA" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0L10 5L0 10z" fill="#7c9cff"/></marker>
      <marker id="arrB" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0L10 5L0 10z" fill="#4fd1c5"/></marker>
    </defs>
    <g id="grid" stroke="#1b2232"></g>
    <circle cx="${C}" cy="${C}" r="${S}" fill="none" stroke="#273043" stroke-dasharray="3 4"/>
    <line x1="${C}" y1="20" x2="${C}" y2="400" stroke="#273043"/><line x1="20" y1="${C}" x2="400" y2="${C}" stroke="#273043"/>
    <path id="arc" fill="rgba(124,156,255,0.12)" stroke="#7c9cff" stroke-width="1"/>
    <line id="euc" stroke="#f6ad55" stroke-dasharray="4 3" stroke-width="1.5"/>
    <line id="va" x1="${C}" y1="${C}" stroke="#7c9cff" stroke-width="3" marker-end="url(#arrA)"/>
    <line id="vb" x1="${C}" y1="${C}" stroke="#4fd1c5" stroke-width="3" marker-end="url(#arrB)"/>
    <text id="theta" fill="#c9d4ff" font-size="13" font-family="monospace"></text>
    <circle id="ha" r="11" fill="#7c9cff" fill-opacity="0.25" stroke="#7c9cff" cursor="grab"/>
    <circle id="hb" r="11" fill="#4fd1c5" fill-opacity="0.25" stroke="#4fd1c5" cursor="grab"/>
    <text x="24" y="40" fill="#8b94a7" font-size="11">drag the circles · dashed ring = unit length</text>
    <text id="la" fill="#7c9cff" font-size="12" font-weight="600">a</text>
    <text id="lb" fill="#4fd1c5" font-size="12" font-weight="600">b</text>`;
  const grid = svg.querySelector('#grid');
  for (let i = -2; i <= 2; i++) if (i) {
    el('line', { x1: C + i * S / 2, x2: C + i * S / 2, y1: 20, y2: 400 }, grid);
    el('line', { y1: C + i * S / 2, y2: C + i * S / 2, x1: 20, x2: 400 }, grid);
  }

  const update = () => {
    const [ax, ay] = toPx(a), [bx, by] = toPx(b);
    const set = (id, attrs) => Object.entries(attrs).forEach(([k, v]) => svg.querySelector(id).setAttribute(k, v));
    set('#va', { x2: ax, y2: ay }); set('#vb', { x2: bx, y2: by });
    set('#ha', { cx: ax, cy: ay }); set('#hb', { cx: bx, cy: by });
    set('#euc', { x1: ax, y1: ay, x2: bx, y2: by });
    set('#la', { x: ax + 12, y: ay - 8 }); set('#lb', { x: bx + 12, y: by - 8 });

    const dot = a.x * b.x + a.y * b.y;
    const la = Math.hypot(a.x, a.y), lb = Math.hypot(b.x, b.y);
    const cos = la && lb ? dot / (la * lb) : 0;
    const theta = Math.acos(Math.max(-1, Math.min(1, cos)));
    const euc = Math.hypot(a.x - b.x, a.y - b.y);

    // angle arc
    const r = 38, angA = Math.atan2(a.y, a.x), angB = Math.atan2(b.y, b.x);
    let d0 = angA, d1 = angB;
    if (((d1 - d0 + Math.PI * 3) % (Math.PI * 2)) - Math.PI < 0) [d0, d1] = [d1, d0];
    const sweep = ((d1 - d0) + Math.PI * 2) % (Math.PI * 2);
    const p0 = [C + r * Math.cos(d0), C - r * Math.sin(d0)], p1 = [C + r * Math.cos(d1), C - r * Math.sin(d1)];
    set('#arc', { d: `M${C} ${C} L${p0[0]} ${p0[1]} A${r} ${r} 0 ${sweep > Math.PI ? 1 : 0} 0 ${p1[0]} ${p1[1]} Z` });
    const mid = d0 + sweep / 2;
    set('#theta', { x: C + 52 * Math.cos(mid) - 10, y: C - 52 * Math.sin(mid) + 4 });
    svg.querySelector('#theta').textContent = `θ=${(theta * 180 / Math.PI).toFixed(0)}°`;

    $('#m-cos').textContent = (1 - cos).toFixed(3);
    $('#m-euc').textContent = euc.toFixed(3);
    $('#m-dot').textContent = (-dot).toFixed(3);
  };

  let drag = null;
  const pt = svg.createSVGPoint();
  const toLocal = (e) => { pt.x = e.clientX; pt.y = e.clientY; return pt.matrixTransform(svg.getScreenCTM().inverse()); };
  svg.querySelector('#ha').addEventListener('pointerdown', (e) => { drag = a; svg.setPointerCapture(e.pointerId); });
  svg.querySelector('#hb').addEventListener('pointerdown', (e) => { drag = b; svg.setPointerCapture(e.pointerId); });
  svg.addEventListener('pointermove', (e) => { if (!drag) return; const p = toLocal(e); const v = fromPx(p.x, p.y); drag.x = Math.max(-1.25, Math.min(1.25, v.x)); drag.y = Math.max(-1.25, Math.min(1.25, v.y)); update(); });
  svg.addEventListener('pointerup', () => (drag = null));
  update();
}

// ---------------------------------------------------------------- 03b real cosine matrix
function cosineMatrix() {
  const f = data.featured;
  const host = $('#cos-matrix');
  host.style.gridTemplateColumns = `140px repeat(${f.length}, 1fr)`;
  const cos = (u, v) => { let d = 0, nu = 0, nv = 0; for (let i = 0; i < u.length; i++) { d += u[i] * v[i]; nu += u[i] * u[i]; nv += v[i] * v[i]; } return 1 - d / Math.sqrt(nu * nv); };
  host.appendChild(Object.assign(document.createElement('div'), { className: 'h' }));
  for (const x of f) host.appendChild(Object.assign(document.createElement('div'), { className: 'h', textContent: movies[x.i].t, style: 'text-align:center' }));
  for (const r of f) {
    host.appendChild(Object.assign(document.createElement('div'), { className: 'h', textContent: movies[r.i].t }));
    for (const c of f) {
      const d = cos(r.v, c.v);
      const cell = document.createElement('div');
      cell.className = 'v'; cell.textContent = d.toFixed(3);
      const t = Math.max(0, Math.min(1, 1 - d / 0.6)); // 0 → neutral, 1 → strong
      cell.style.background = `rgba(124,156,255,${0.08 + t * 0.55})`;
      host.appendChild(cell);
    }
  }
}

// ---------------------------------------------------------------- 04 exact-scan animation
function scanAnimation() {
  const svg = $('#scan-svg');
  const COLS = 28, ROWS = 12, CW = 11, CH = 11, GAP = 3, X0 = 14, Y0 = 40;
  const TOTAL_ROWS = data.count, cells = COLS * ROWS;
  el('text', { x: X0, y: 22, fill: '#8b94a7', 'font-size': 11 }, svg).textContent = `Movies table — each cell ≈ ${Math.round(TOTAL_ROWS / cells)} rows`;
  el('text', { x: 420, y: 22, fill: '#8b94a7', 'font-size': 11 }, svg).textContent = 'TOP 10';
  const grid = el('g', {}, svg);
  const cellEls = [];
  for (let r = 0; r < ROWS; r++) for (let c = 0; c < COLS; c++)
    cellEls.push(el('rect', { x: X0 + c * (CW + GAP), y: Y0 + r * (CH + GAP), width: CW, height: CH, rx: 2, fill: '#1b2232' }, grid));
  const dists = cellEls.map(() => 0.25 + Math.random() * 0.5);
  // Plant some clear winners so the top-10 is visually distinct.
  for (let k = 0; k < 14; k++) dists[Math.floor(Math.random() * cells)] = 0.1 + Math.random() * 0.12;

  const resG = el('g', {}, svg);
  const slots = Array.from({ length: 10 }, (_, i) => el('rect', { x: 420, y: Y0 + i * 17, width: 80, height: 13, rx: 3, fill: '#1b2232', stroke: '#273043' }, resG));
  const slotTxt = Array.from({ length: 10 }, (_, i) => el('text', { x: 426, y: Y0 + i * 17 + 10, fill: '#e6e9f0', 'font-size': 9, 'font-family': 'monospace' }, resG));
  const cursor = el('rect', { width: CW + 4, height: CH + 4, rx: 3, fill: 'none', stroke: '#f6e05e', 'stroke-width': 1.5, opacity: 0 }, svg);
  const status = $('#scan-status');
  const heat = (d) => { const t = Math.max(0, Math.min(1, (0.75 - d) / 0.65)); return `rgb(${Math.round(27 + t * 97)}, ${Math.round(34 + t * 122)}, ${Math.round(50 + t * 205)})`; };

  let running = false;
  $('#scan-play').addEventListener('click', async () => {
    if (running) return; running = true; $('#scan-play').disabled = true;
    cellEls.forEach((c) => c.setAttribute('fill', '#1b2232'));
    slots.forEach((s) => s.setAttribute('stroke', '#273043')); slotTxt.forEach((t) => (t.textContent = ''));
    cursor.setAttribute('opacity', 1);
    const perTick = reduceMotion ? cells : 3;
    for (let i = 0; i < cells; i += perTick) {
      for (let k = i; k < Math.min(cells, i + perTick); k++) {
        const c = cellEls[k];
        c.setAttribute('fill', heat(dists[k]));
        cursor.setAttribute('x', +c.getAttribute('x') - 2); cursor.setAttribute('y', +c.getAttribute('y') - 2);
      }
      status.textContent = `${fmt(TOTAL_ROWS)} rows · ${fmt(Math.min(TOTAL_ROWS, Math.round((i + perTick) / cells * TOTAL_ROWS)))} compared`;
      await sleep(12);
    }
    cursor.setAttribute('opacity', 0);
    status.textContent = `${fmt(TOTAL_ROWS)} rows · ${fmt(TOTAL_ROWS)} compared · sorting…`;
    await sleep(300);
    const top = dists.map((d, i) => [d, i]).sort((a, b) => a[0] - b[0]).slice(0, 10);
    for (let k = 0; k < 10; k++) {
      const [d, i] = top[k];
      cellEls[i].setAttribute('fill', '#f6e05e');
      slots[k].setAttribute('stroke', '#f6e05e');
      slotTxt[k].textContent = `#${k + 1}  d=${d.toFixed(3)}`;
      await sleep(reduceMotion ? 0 : 80);
    }
    status.textContent = `${fmt(TOTAL_ROWS)} rows · ${fmt(TOTAL_ROWS)} compared · 10 returned · exact`;
    running = false; $('#scan-play').disabled = false;
  });
}

// ---------------------------------------------------------------- 05 DiskANN-style greedy search
function annAnimation() {
  const svg = $('#ann-svg');
  const W = 520, H = 420, N = 70, K = 4;
  const rnd = mulberry32(7);
  const nodes = Array.from({ length: N }, () => ({ x: 30 + rnd() * (W - 60), y: 30 + rnd() * (H - 60) }));
  // kNN graph plus a few long-range edges (what makes DiskANN/HNSW navigable).
  const adj = nodes.map(() => new Set());
  nodes.forEach((p, i) => {
    nodes.map((q, j) => [Math.hypot(p.x - q.x, p.y - q.y), j]).sort((a, b) => a[0] - b[0]).slice(1, K + 1)
      .forEach(([, j]) => { adj[i].add(j); adj[j].add(i); });
  });
  for (let k = 0; k < 10; k++) { const i = Math.floor(rnd() * N), j = Math.floor(rnd() * N); if (i !== j) { adj[i].add(j); adj[j].add(i); } }
  // Entry point: the medoid.
  const entry = nodes.map((p, i) => [nodes.reduce((s, q) => s + Math.hypot(p.x - q.x, p.y - q.y), 0), i]).sort((a, b) => a[0] - b[0])[0][1];

  const edgesG = el('g', { stroke: '#273043', 'stroke-width': 1 }, svg);
  const edgeEls = {};
  nodes.forEach((p, i) => adj[i].forEach((j) => { if (i < j) edgeEls[`${i}-${j}`] = el('line', { x1: p.x, y1: p.y, x2: nodes[j].x, y2: nodes[j].y }, edgesG); }));
  const pathG = el('g', { stroke: '#f6e05e', 'stroke-width': 2.5, 'stroke-linecap': 'round' }, svg);
  const nodesG = el('g', {}, svg);
  const nodeEls = nodes.map((p) => el('circle', { cx: p.x, cy: p.y, r: 4.5, fill: '#4a5568' }, nodesG));
  nodeEls[entry].setAttribute('fill', '#7c9cff'); nodeEls[entry].setAttribute('r', 7);
  el('text', { x: nodes[entry].x + 10, y: nodes[entry].y + 4, fill: '#7c9cff', 'font-size': 11 }, svg).textContent = 'entry point';
  const query = el('g', { opacity: 0 }, svg);
  el('circle', { r: 8, fill: 'none', stroke: '#f6e05e', 'stroke-width': 2 }, query);
  el('circle', { r: 2.5, fill: '#f6e05e' }, query);
  const truth = el('circle', { r: 9, fill: 'none', stroke: '#68d391', 'stroke-width': 1.5, 'stroke-dasharray': '3 3', opacity: 0 }, svg);
  const hint = el('text', { x: 14, y: 22, fill: '#8b94a7', 'font-size': 11 }, svg);
  hint.textContent = `${N} vectors · each linked to ~${K} neighbours · click to search`;
  $('#ann-total').textContent = N;

  let busy = false;
  svg.addEventListener('click', async (e) => {
    if (busy) return; busy = true;
    const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
    const q = pt.matrixTransform(svg.getScreenCTM().inverse());
    query.setAttribute('transform', `translate(${q.x} ${q.y})`); query.setAttribute('opacity', 1);
    truth.setAttribute('opacity', 0); pathG.innerHTML = '';
    nodeEls.forEach((c, i) => { c.setAttribute('fill', i === entry ? '#7c9cff' : '#4a5568'); c.setAttribute('r', i === entry ? 7 : 4.5); });
    Object.values(edgeEls).forEach((l) => l.setAttribute('stroke', '#273043'));

    const dist = (i) => Math.hypot(nodes[i].x - q.x, nodes[i].y - q.y);
    const trueNearest = nodes.map((_, i) => i).sort((a, b) => dist(a) - dist(b))[0];

    // Greedy best-first with a small beam (DiskANN's search list), visited counter included.
    let cur = entry, visited = new Set([entry]);
    nodeEls[cur].setAttribute('fill', '#f6e05e');
    $('#ann-visited').textContent = visited.size; $('#ann-recall').textContent = '…';
    while (true) {
      await sleep(reduceMotion ? 0 : 260);
      let best = cur, bestD = dist(cur);
      for (const j of adj[cur]) {
        visited.add(j);
        nodeEls[j].setAttribute('fill', '#a0aec0');
        edgeEls[`${Math.min(cur, j)}-${Math.max(cur, j)}`]?.setAttribute('stroke', '#718096');
        if (dist(j) < bestD) { best = j; bestD = dist(j); }
      }
      $('#ann-visited').textContent = visited.size;
      if (best === cur) break;
      el('line', { x1: nodes[cur].x, y1: nodes[cur].y, x2: nodes[best].x, y2: nodes[best].y }, pathG);
      nodeEls[best].setAttribute('fill', '#f6e05e'); nodeEls[best].setAttribute('r', 6.5);
      cur = best;
    }
    truth.setAttribute('cx', nodes[trueNearest].x); truth.setAttribute('cy', nodes[trueNearest].y); truth.setAttribute('opacity', 1);
    const ok = cur === trueNearest;
    $('#ann-recall').textContent = ok ? 'yes ✓' : 'no — local optimum';
    $('#ann-recall').style.color = ok ? '#68d391' : '#fc8181';
    hint.textContent = ok
      ? `found the true nearest after visiting ${visited.size} of ${N} (${Math.round(visited.size / N * 100)}%)`
      : `stuck in a local optimum — real indexes widen the beam (search list) to recover recall`;
    busy = false;
  });
}

// ---------------------------------------------------------------- 06 pipeline diagram
// Shared SVG helpers for the flow diagrams (06 pipeline, 07 RAG).
function flowKit(svg, prefix) {
  svg.innerHTML = `<defs>
    <marker id="${prefix}a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0L10 5L0 10z" fill="#637089"/></marker>
    <marker id="${prefix}q" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0L10 5L0 10z" fill="#f6e05e"/></marker>
  </defs>`;
  const box = (x, y, w, h, title, sub, color = '#273043') => {
    const g = el('g', {}, svg);
    el('rect', { x, y, width: w, height: h, rx: 10, fill: '#151b28', stroke: color, 'stroke-width': 1.5 }, g);
    el('text', { x: x + w / 2, y: y + (sub ? 26 : h / 2 + 5), fill: '#e6e9f0', 'font-size': 13, 'font-weight': 600, 'text-anchor': 'middle' }, g).textContent = title;
    if (sub) el('text', { x: x + w / 2, y: y + 46, fill: '#8b94a7', 'font-size': 11, 'text-anchor': 'middle', 'font-family': 'monospace' }, g).textContent = sub;
    return { x, y, w, h };
  };
  // Labels get a dark halo (paint-order) so they stay legible where they cross a path.
  const label = (x, y, text, anchor = 'middle') => el('text', { x, y, fill: '#8b94a7', 'font-size': 10, 'text-anchor': anchor, 'paint-order': 'stroke', stroke: '#0f131c', 'stroke-width': 5, 'stroke-linejoin': 'round' }, svg).textContent = text;
  const path = (d, cls) => {
    const p = el('path', { d, fill: 'none', stroke: cls === 'query' ? '#f6e05e' : '#637089', 'stroke-width': 1.5, 'marker-end': cls === 'query' ? `url(#${prefix}q)` : `url(#${prefix}a)`, 'stroke-dasharray': '6 5' }, svg);
    if (!reduceMotion) el('animate', { attributeName: 'stroke-dashoffset', from: 22, to: 0, dur: '1.2s', repeatCount: 'indefinite' }, p);
  };
  const arrow = (a, b, text, cls = 'build') => {
    const x1 = a.x + a.w, y1 = a.y + a.h / 2, x2 = b.x, y2 = b.y + b.h / 2;
    const mx = (x1 + x2) / 2;
    path(`M${x1} ${y1} C${mx} ${y1} ${mx} ${y2} ${x2} ${y2}`, cls);
    if (text) label(mx, (y1 + y2) / 2 - 8, text);
  };
  const heading = (x, y, text, color) => el('text', { x, y, fill: color, 'font-size': 11, 'font-family': 'monospace', 'letter-spacing': 1 }, svg).textContent = text;
  return { box, label, path, arrow, heading };
}

// ---------------------------------------------------------------- 06 pipeline diagram
function pipelineDiagram() {
  const svg = $('#pipeline-svg');
  const { box, label, path, arrow } = flowKit(svg, 'p');

  // Build-time row
  el('text', { x: 20, y: 24, fill: '#4fd1c5', 'font-size': 11, 'font-family': 'monospace', 'letter-spacing': 1 }, svg).textContent = 'BUILD TIME';
  const ml = box(20, 40, 150, 60, 'MovieLens CSVs', 'import');
  const tm = box(20, 120, 150, 60, 'TMDB API', 'enrich');
  const txt = box(230, 80, 170, 60, 'Composed text', 'BuildDocumentText');
  const ol = box(460, 80, 170, 60, 'Embedding model', data.model, '#7c9cff');
  const db = box(700, 40, 180, 140, 'SQL Server 2025', 'Movies.Embedding', '#4fd1c5');
  el('text', { x: 790, y: 118, fill: '#8b94a7', 'font-size': 10, 'text-anchor': 'middle', 'font-family': 'monospace' }, svg).textContent = 'vector(768)';
  el('text', { x: 790, y: 136, fill: '#8b94a7', 'font-size': 10, 'text-anchor': 'middle', 'font-family': 'monospace' }, svg).textContent = '+ Title, Year, Genres…';
  el('text', { x: 790, y: 160, fill: '#637089', 'font-size': 10, 'text-anchor': 'middle' }, svg).textContent = '(optional: DiskANN index)';
  arrow(ml, txt, 'title · genres · tags'); arrow(tm, txt, 'overview');
  arrow(txt, ol, 'text'); arrow(ol, { x: db.x, y: db.y + 20, w: 0, h: 60 }, '768 floats');

  // Query-time row
  el('text', { x: 20, y: 214, fill: '#f6e05e', 'font-size': 11, 'font-family': 'monospace', 'letter-spacing': 1 }, svg).textContent = 'QUERY TIME';
  const uq = box(230, 200, 170, 50, '“lonely robot…”', 'search_query:', '#f6e05e');
  const res = box(920, 200, 160, 50, 'Top-10 results', 'ORDER BY distance', '#f6e05e');
  // query text → model (enters from below), model → DB (lower edge), DB → results
  path(`M${uq.x + uq.w} ${uq.y + 25} C${ol.x + 40} ${uq.y + 25} ${ol.x + 60} ${ol.y + ol.h + 30} ${ol.x + 60} ${ol.y + ol.h}`, 'query');
  label(ol.x + 10, uq.y - 2, 'embed once');
  path(`M${ol.x + ol.w} ${ol.y + 50} C${ol.x + ol.w + 35} ${ol.y + 50} ${db.x - 35} ${db.y + 120} ${db.x} ${db.y + 120}`, 'query');
  label(ol.x + ol.w + 35, ol.y + 75, 'query vector');
  path(`M${db.x + db.w / 2} ${db.y + db.h} C${db.x + db.w / 2} ${res.y + 25} ${res.x - 40} ${res.y + 25} ${res.x} ${res.y + 25}`, 'query');
  label(db.x + db.w / 2 + 50, res.y + 12, 'VECTOR_DISTANCE');
}

// ---------------------------------------------------------------- 07 RAG flow
function ragDiagram() {
  const svg = $('#rag-svg');
  const { box, label, path, arrow, heading } = flowKit(svg, 'r');
  const Y = '#f6e05e', T = '#4fd1c5', B = '#7c9cff';

  heading(16, 22, 'OFFLINE · once per document', T);
  const docs = box(16, 36, 110, 54, 'Documents', 'PDF, wiki, tickets');
  const embedA = box(172, 36, 84, 54, 'Embed', 'per chunk', B);
  const db = box(316, 36, 228, 84, 'SQL Server', 'Chunks', T);
  el('text', { x: 430, y: 104, fill: '#8b94a7', 'font-size': 10, 'text-anchor': 'middle', 'font-family': 'monospace' }, svg).textContent = 'Text · Embedding · Dept · ACL';
  arrow(docs, embedA, 'chunks');
  arrow(embedA, db, 'vector(768)');

  heading(16, 186, 'ONLINE · every question', Y);
  const user = box(16, 200, 120, 54, 'User asks', '"how do we…?"', Y);
  const embedQ = box(176, 200, 100, 54, 'Embed', 'one model call', B);
  const llm = box(385, 168, 90, 40, 'LLM', null, B);
  const answer = box(330, 240, 214, 54, 'Grounded answer', 'with citations', Y);
  arrow(user, embedQ, null, 'query');
  // query vector up into the DB; permissions are a plain WHERE on the same row
  path(`M${embedQ.x + embedQ.w} 227 C300 227 300 100 ${db.x} 100`, 'query');
  label(276, 150, 'WHERE user may read', 'end');
  label(276, 163, 'ORDER BY distance TOP 5', 'end');
  // top-k chunks straight down into the prompt, then the answer
  path(`M430 ${db.y + db.h} L430 ${llm.y}`, 'query');
  label(440, 150, 'top-5 chunks + question', 'start');
  path(`M430 ${llm.y + llm.h} L430 ${answer.y}`, 'query');
}

// ---------------------------------------------------------------- utils
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }
function escapeHtml(s) { return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
function mulberry32(a) { return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
