using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Voxta.Modules.GraphMemory.Controllers;

[Authorize(Roles = "ADMIN")]
[Route("manage/graph-memory")]
public sealed class GraphMemoryManageController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return Content(PageHtml, "text/html");
    }

    private const string PageHtml =
        // language=html
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Voxta Graph Memory</title>
          <style>
            :root {
              --bg: #0b0f14;
              --panel: #0f1722;
              --panel2: #0c1320;
              --text: #e6edf3;
              --muted: #9aa7b2;
              --border: #223047;
              --accent: #4f8cff;
              --danger: #ff5f5f;
              --ok: #42d392;
              --mono: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace;
              --sans: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, Arial, "Noto Sans", "Liberation Sans", sans-serif;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              font-family: var(--sans);
              color: var(--text);
              background: radial-gradient(1000px 600px at 20% 0%, #101a2b, transparent),
                          radial-gradient(900px 500px at 90% 20%, #131b2e, transparent),
                          var(--bg);
            }
            header {
              padding: 18px 22px;
              border-bottom: 1px solid var(--border);
              background: rgba(10, 16, 24, 0.75);
              backdrop-filter: blur(10px);
              position: sticky;
              top: 0;
              z-index: 10;
            }
            header h1 {
              margin: 0;
              font-size: 16px;
              letter-spacing: 0.2px;
            }
            header .sub {
              margin-top: 6px;
              color: var(--muted);
              font-size: 12px;
              line-height: 1.35;
            }
            .wrap {
              max-width: 1300px;
              margin: 0 auto;
              padding: 18px 18px 26px;
            }
            .card {
              border: 1px solid var(--border);
              border-radius: 10px;
              background: rgba(15, 23, 34, 0.72);
              box-shadow: 0 6px 24px rgba(0,0,0,0.25);
              overflow: hidden;
            }
            .card .title {
              padding: 12px 14px;
              border-bottom: 1px solid var(--border);
              display: flex;
              justify-content: space-between;
              align-items: center;
              gap: 10px;
            }
            .title .left {
              display: flex;
              gap: 10px;
              align-items: center;
              min-width: 0;
            }
            .pill {
              font-size: 11px;
              padding: 2px 8px;
              border-radius: 999px;
              background: rgba(79, 140, 255, 0.14);
              border: 1px solid rgba(79, 140, 255, 0.25);
              color: #b9d0ff;
              white-space: nowrap;
            }
            .title h2 {
              margin: 0;
              font-size: 13px;
              font-weight: 600;
              letter-spacing: 0.2px;
              white-space: nowrap;
              overflow: hidden;
              text-overflow: ellipsis;
            }
            .card .body { padding: 14px; }
            .row {
              display: grid;
              grid-template-columns: 2fr 1fr 1fr 1fr;
              gap: 12px;
              margin-bottom: 12px;
              align-items: end;
            }
            label {
              display: block;
              font-size: 12px;
              color: var(--muted);
              margin-bottom: 6px;
            }
            select, button, input[type="text"] {
              width: 100%;
              padding: 10px 10px;
              border-radius: 8px;
              border: 1px solid var(--border);
              background: rgba(11, 15, 20, 0.65);
              color: var(--text);
              outline: none;
            }
            button {
              cursor: pointer;
              background: rgba(79, 140, 255, 0.12);
              border: 1px solid rgba(79, 140, 255, 0.25);
            }
            button:hover { border-color: rgba(79, 140, 255, 0.45); }
            .status {
              font-family: var(--mono);
              font-size: 12px;
              padding: 10px 10px;
              border-radius: 8px;
              border: 1px solid var(--border);
              background: rgba(11, 15, 20, 0.65);
              color: var(--muted);
              overflow: hidden;
              white-space: nowrap;
              text-overflow: ellipsis;
            }
            .status.ok { color: var(--ok); }
            .status.err { color: var(--danger); }
            .split {
              display: grid;
              grid-template-columns: 1fr 360px;
              gap: 12px;
            }
            .panel {
              border: 1px solid var(--border);
              border-radius: 10px;
              background: rgba(11, 15, 20, 0.55);
              overflow: hidden;
              min-height: 520px;
            }
            canvas {
              display: block;
              width: 100%;
              height: 100%;
              background: radial-gradient(900px 500px at 30% 0%, rgba(79,140,255,0.08), transparent),
                          rgba(10, 16, 24, 0.65);
            }
            .side {
              padding: 12px;
              display: flex;
              flex-direction: column;
              gap: 12px;
            }
            .section {
              border: 1px solid var(--border);
              border-radius: 10px;
              background: rgba(12, 19, 32, 0.65);
              overflow: hidden;
            }
            .section .h {
              padding: 10px 10px;
              border-bottom: 1px solid var(--border);
              font-size: 12px;
              color: var(--muted);
              display: flex;
              justify-content: space-between;
              align-items: center;
              gap: 10px;
            }
            .section pre {
              margin: 0;
              padding: 10px 10px;
              font-family: var(--mono);
              font-size: 11px;
              line-height: 1.35;
              white-space: pre-wrap;
              word-break: break-word;
              max-height: 320px;
              overflow: auto;
            }
            a { color: #b9d0ff; text-decoration: none; }
            a:hover { text-decoration: underline; }
            @media (max-width: 980px) {
              .split { grid-template-columns: 1fr; }
              .row { grid-template-columns: 1fr; }
              .panel { min-height: 420px; }
            }
          </style>
        </head>
        <body>
          <header>
            <h1>Graph Memory Viewer</h1>
            <div class="sub">Visualize GraphMemory entities/relations per chat (and optionally per character scope). Drag nodes to pin them.</div>
          </header>
        
          <div class="wrap">
            <div class="card">
              <div class="title">
                <div class="left">
                  <span class="pill">ADMIN</span>
                  <h2>Graph Viewer</h2>
                </div>
                <div class="right" style="display:flex; gap:10px; align-items:center;">
                  <button id="btnReload" style="width:auto;">Reload</button>
                </div>
              </div>
              <div class="body">
                <div class="row">
                  <div>
                    <label for="chat">Chat</label>
                    <select id="chat"></select>
                  </div>
                  <div>
                    <label for="character">Character Scope</label>
                    <select id="character">
                      <option value="">(All)</option>
                    </select>
                  </div>
                  <div>
                    <label>&nbsp;</label>
                    <button id="btnReset">Reset Layout</button>
                  </div>
                  <div>
                    <label>Status</label>
                    <div id="status" class="status">Loading…</div>
                  </div>
                </div>
        
                <div class="split">
                  <div class="panel">
                    <canvas id="canvas"></canvas>
                  </div>
                  <div class="side">
                    <div class="section">
                      <div class="h">
                        <span>Selection</span>
                        <a id="rawLink" href="#" target="_blank" rel="noreferrer">Raw JSON</a>
                      </div>
                      <pre id="details">(click a node)</pre>
                    </div>
                    <div class="section">
                      <div class="h">
                        <span>Hints</span>
                      </div>
                      <pre>Drag node: pin position
        Mouse wheel: zoom
        Click background: clear selection</pre>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        
          <script>
            const API = '/api/extensions/graph-memory';
        
            const els = {
              chat: document.getElementById('chat'),
              character: document.getElementById('character'),
              status: document.getElementById('status'),
              canvas: document.getElementById('canvas'),
              details: document.getElementById('details'),
              rawLink: document.getElementById('rawLink'),
              btnReset: document.getElementById('btnReset'),
              btnReload: document.getElementById('btnReload'),
            };
        
            const ctx = els.canvas.getContext('2d');
            let state = {
              chats: [],
              chatId: null,
              characterName: '',
              nodes: [],
              edges: [],
              simNodes: [],
              simEdges: [],
              selectedNodeId: null,
              draggingId: null,
              dragOffset: { x: 0, y: 0 },
              zoom: 1,
              pan: { x: 0, y: 0 },
            };
        
            function setStatus(text, kind) {
              els.status.textContent = text;
              els.status.className = 'status' + (kind ? ' ' + kind : '');
            }
        
            async function apiJson(path) {
              const res = await fetch(API + path, { headers: { 'accept': 'application/json' } });
              if (!res.ok) throw new Error(await res.text());
              return res.json();
            }
        
            function resizeCanvas() {
              const rect = els.canvas.getBoundingClientRect();
              const dpr = window.devicePixelRatio || 1;
              els.canvas.width = Math.max(10, Math.floor(rect.width * dpr));
              els.canvas.height = Math.max(10, Math.floor(rect.height * dpr));
              ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            }
        
            function screenToWorld(x, y) {
              const rect = els.canvas.getBoundingClientRect();
              const sx = x - rect.left;
              const sy = y - rect.top;
              const cx = rect.width / 2;
              const cy = rect.height / 2;
              return {
                x: (sx - cx - state.pan.x) / state.zoom,
                y: (sy - cy - state.pan.y) / state.zoom
              };
            }
        
            function worldToScreen(x, y) {
              const rect = els.canvas.getBoundingClientRect();
              const cx = rect.width / 2;
              const cy = rect.height / 2;
              return {
                x: cx + state.pan.x + x * state.zoom,
                y: cy + state.pan.y + y * state.zoom
              };
            }
        
            function nodeColor(n) {
              const t = (n.type || '').toLowerCase();
              if (t.includes('character') || t.includes('person') || t.includes('user')) return '#4f8cff';
              return '#42d392';
            }
        
            function pickNode(worldX, worldY) {
              let best = null;
              let bestDist = 1e9;
              for (const n of state.simNodes) {
                const dx = n.x - worldX;
                const dy = n.y - worldY;
                const d = Math.sqrt(dx*dx + dy*dy);
                if (d < 18 && d < bestDist) {
                  best = n;
                  bestDist = d;
                }
              }
              return best;
            }
        
            function setSelection(node) {
              state.selectedNodeId = node ? node.id : null;
              if (!node) {
                els.details.textContent = '(click a node)';
                return;
              }
        
              const edges = state.edges.filter(e => e.sourceId === node.id || e.targetId === node.id);
              const payload = {
                node: state.nodes.find(x => x.id === node.id) || node,
                edges: edges.slice(0, 40)
              };
              els.details.textContent = JSON.stringify(payload, null, 2);
            }
        
            function resetLayout() {
              const rect = els.canvas.getBoundingClientRect();
              const w = rect.width, h = rect.height;
              for (const n of state.simNodes) {
                n.x = (Math.random() - 0.5) * (w * 0.6);
                n.y = (Math.random() - 0.5) * (h * 0.6);
                n.vx = 0;
                n.vy = 0;
              }
              state.zoom = 1;
              state.pan = { x: 0, y: 0 };
            }
        
            function buildSimulation() {
              const rect = els.canvas.getBoundingClientRect();
              const w = rect.width, h = rect.height;
        
              const simNodes = state.nodes.map(n => ({
                ...n,
                x: (Math.random() - 0.5) * (w * 0.6),
                y: (Math.random() - 0.5) * (h * 0.6),
                vx: 0,
                vy: 0,
                pinned: false,
              }));
        
              const byId = new Map(simNodes.map(n => [n.id, n]));
              const simEdges = state.edges
                .map(e => ({
                  ...e,
                  s: byId.get(e.sourceId),
                  t: byId.get(e.targetId),
                }))
                .filter(e => e.s && e.t);
        
              state.simNodes = simNodes;
              state.simEdges = simEdges;
              resetLayout();
            }
        
            function tick() {
              const nodes = state.simNodes;
              const edges = state.simEdges;
              if (nodes.length === 0) return;
        
              const repulsion = 12000;
              const spring = 0.015;
              const springLen = 120;
              const damping = 0.85;
              const center = 0.002;
        
              // repulsion (O(n^2) - fine for debug sizes)
              for (let i = 0; i < nodes.length; i++) {
                for (let j = i + 1; j < nodes.length; j++) {
                  const a = nodes[i], b = nodes[j];
                  const dx = a.x - b.x;
                  const dy = a.y - b.y;
                  const d2 = dx*dx + dy*dy + 40;
                  const f = repulsion / d2;
                  const fx = f * dx;
                  const fy = f * dy;
                  if (!a.pinned) { a.vx += fx; a.vy += fy; }
                  if (!b.pinned) { b.vx -= fx; b.vy -= fy; }
                }
              }
        
              // springs
              for (const e of edges) {
                const a = e.s, b = e.t;
                const dx = b.x - a.x;
                const dy = b.y - a.y;
                const dist = Math.sqrt(dx*dx + dy*dy) || 1;
                const diff = dist - springLen;
                const f = spring * diff;
                const fx = f * (dx / dist);
                const fy = f * (dy / dist);
                if (!a.pinned) { a.vx += fx; a.vy += fy; }
                if (!b.pinned) { b.vx -= fx; b.vy -= fy; }
              }
        
              // center + integrate
              for (const n of nodes) {
                if (!n.pinned) {
                  n.vx += (-n.x) * center;
                  n.vy += (-n.y) * center;
                  n.vx *= damping;
                  n.vy *= damping;
                  n.x += n.vx * 0.016;
                  n.y += n.vy * 0.016;
                }
              }
            }
        
            function draw() {
              const rect = els.canvas.getBoundingClientRect();
              const w = rect.width, h = rect.height;
              ctx.clearRect(0, 0, w, h);
        
              // background grid
              ctx.save();
              ctx.globalAlpha = 0.06;
              ctx.strokeStyle = '#4f8cff';
              const step = 80;
              for (let x = 0; x <= w; x += step) {
                ctx.beginPath();
                ctx.moveTo(x, 0);
                ctx.lineTo(x, h);
                ctx.stroke();
              }
              for (let y = 0; y <= h; y += step) {
                ctx.beginPath();
                ctx.moveTo(0, y);
                ctx.lineTo(w, y);
                ctx.stroke();
              }
              ctx.restore();
        
              // edges
              ctx.save();
              ctx.lineWidth = 1;
              ctx.globalAlpha = 0.8;
              ctx.strokeStyle = 'rgba(154, 167, 178, 0.45)';
              for (const e of state.simEdges) {
                const a = worldToScreen(e.s.x, e.s.y);
                const b = worldToScreen(e.t.x, e.t.y);
                ctx.beginPath();
                ctx.moveTo(a.x, a.y);
                ctx.lineTo(b.x, b.y);
                ctx.stroke();
              }
              ctx.restore();
        
              // nodes
              for (const n of state.simNodes) {
                const p = worldToScreen(n.x, n.y);
                const selected = state.selectedNodeId === n.id;
                const r = selected ? 10 : 8;
        
                ctx.beginPath();
                ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
                ctx.fillStyle = nodeColor(n);
                ctx.globalAlpha = n.pinned ? 0.95 : 0.8;
                ctx.fill();
        
                ctx.globalAlpha = 0.9;
                ctx.strokeStyle = selected ? '#ffffff' : 'rgba(0,0,0,0.25)';
                ctx.lineWidth = selected ? 2 : 1;
                ctx.stroke();
        
                ctx.globalAlpha = 0.9;
                ctx.font = '12px ' + getComputedStyle(document.body).fontFamily;
                ctx.fillStyle = '#e6edf3';
                ctx.fillText(n.name, p.x + 10, p.y + 4);
              }
            }
        
            function animate() {
              tick();
              draw();
              requestAnimationFrame(animate);
            }
        
            async function loadChats() {
              const data = await apiJson('/chats');
              state.chats = data && data.chats ? data.chats : [];
        
              els.chat.innerHTML = '';
              for (const c of state.chats) {
                const label = `${c.chatId}  (${c.entities} entities, ${c.relations} relations)`;
                const opt = document.createElement('option');
                opt.value = c.chatId;
                opt.textContent = label;
                els.chat.appendChild(opt);
              }
        
              if (state.chats.length === 0) {
                setStatus('No chats found in graph store yet.', '');
                return;
              }
        
              state.chatId = state.chats[0].chatId;
              els.chat.value = state.chatId;
              await refreshCharactersAndGraph();
            }
        
            function fillCharacterDropdown(characterNames) {
              const keep = els.character.value || '';
              els.character.innerHTML = '<option value=\"\">(All)</option>';
              for (const n of (characterNames || [])) {
                const opt = document.createElement('option');
                opt.value = n;
                opt.textContent = n;
                els.character.appendChild(opt);
              }
              els.character.value = keep;
            }
        
            async function loadGraph() {
              if (!state.chatId) return;
              setStatus('Loading graph…', '');
        
              const q = new URLSearchParams({ chatId: state.chatId });
              if (state.characterName) q.set('characterName', state.characterName);
              const data = await apiJson('/graph?' + q.toString());
        
              state.nodes = data.nodes || [];
              state.edges = data.edges || [];
              fillCharacterDropdown(data.characterNames || []);
        
              const rawQ = new URLSearchParams({ chatId: state.chatId });
              if (state.characterName) rawQ.set('characterName', state.characterName);
              els.rawLink.href = API + '/raw?' + rawQ.toString();
        
              buildSimulation();
              setSelection(null);
              setStatus(`Loaded ${state.nodes.length} nodes, ${state.edges.length} edges.`, 'ok');
            }
        
            async function refreshCharactersAndGraph() {
              state.characterName = '';
              els.character.value = '';
              await loadGraph();
            }
        
            els.chat.addEventListener('change', async () => {
              state.chatId = els.chat.value;
              await refreshCharactersAndGraph();
            });
        
            els.character.addEventListener('change', async () => {
              state.characterName = els.character.value || '';
              await loadGraph();
            });
        
            els.btnReset.addEventListener('click', () => resetLayout());
            els.btnReload.addEventListener('click', () => loadChats().catch(e => setStatus(e.message || String(e), 'err')));
        
            // interactions
            els.canvas.addEventListener('mousedown', (ev) => {
              const w = screenToWorld(ev.clientX, ev.clientY);
              const hit = pickNode(w.x, w.y);
              if (hit) {
                state.draggingId = hit.id;
                hit.pinned = true;
                state.dragOffset.x = hit.x - w.x;
                state.dragOffset.y = hit.y - w.y;
                setSelection(hit);
              } else {
                state.draggingId = null;
                setSelection(null);
              }
            });
        
            window.addEventListener('mousemove', (ev) => {
              if (!state.draggingId) return;
              const w = screenToWorld(ev.clientX, ev.clientY);
              const n = state.simNodes.find(x => x.id === state.draggingId);
              if (!n) return;
              n.x = w.x + state.dragOffset.x;
              n.y = w.y + state.dragOffset.y;
              n.vx = 0;
              n.vy = 0;
            });
        
            window.addEventListener('mouseup', () => {
              state.draggingId = null;
            });
        
            els.canvas.addEventListener('wheel', (ev) => {
              ev.preventDefault();
              const delta = Math.sign(ev.deltaY);
              const factor = delta > 0 ? 0.9 : 1.1;
              state.zoom = Math.max(0.25, Math.min(3, state.zoom * factor));
            }, { passive: false });
        
            window.addEventListener('resize', () => {
              resizeCanvas();
              draw();
            });
        
            resizeCanvas();
            animate();
            loadChats().catch(e => setStatus(e.message || String(e), 'err'));
          </script>
        </body>
        </html>
        """;
}
