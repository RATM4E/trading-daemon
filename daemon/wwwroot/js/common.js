// =====================================================================
//  Shared UI Components  (Phase 10 — logarithmic bars, UTC helpers)
// =====================================================================

// React hooks — declared once here with var, available to all subsequent scripts
var { useState, useEffect, useRef, useCallback, useMemo, useReducer } = React;

var Badge = ({ children, color = "gray" }) => {
  const c = {
    green: "bg-emerald-500/20 text-emerald-400 border-emerald-500/30", red: "bg-red-500/20 text-red-400 border-red-500/30",
    yellow: "bg-amber-500/20 text-amber-400 border-amber-500/30", blue: "bg-blue-500/20 text-blue-400 border-blue-500/30",
    gray: "bg-zinc-700/50 text-zinc-400 border-zinc-600/30", orange: "bg-orange-500/20 text-orange-400 border-orange-500/30",
    purple: "bg-violet-500/20 text-violet-400 border-violet-500/30"
  };
  return <span className={`text-xs font-mono px-2 py-0.5 rounded border ${c[color]}`}>{children}</span>;
};

// Logarithmic progress bar: small values (5-20%) are clearly visible,
// while still showing urgency as it approaches 100%.
// Formula: visual% = ln(1 + ratio * (e^k - 1)) / k  where k controls curvature.
var ProgressBar = ({ value, max, warning = 0.5, danger = 0.8, logarithmic = true }) => {
  const ratio = max > 0 ? Math.min(Math.abs(value) / max, 1) : 0;
  let visualPct;
  if (logarithmic && ratio > 0) {
    const k = 3;
    visualPct = Math.log(1 + ratio * (Math.exp(k) - 1)) / k * 100;
  } else {
    visualPct = ratio * 100;
  }
  const displayPct = Math.round(ratio * 100);
  let color = "bg-emerald-500"; if (ratio > danger) color = "bg-red-500"; else if (ratio > warning) color = "bg-amber-500";
  return (<div className="flex items-center gap-2 w-full"><div className="flex-1 h-2 bg-zinc-800 rounded-full overflow-hidden">
    <div className={`h-full ${color} rounded-full`} style={{ width: `${visualPct}%`, transition: "width 0.5s" }} /></div>
    <span className="text-xs text-zinc-500 font-mono w-10 text-right">{displayPct}%</span></div>);
};

var StatusDot = ({ status }) => {
  const c = {
    connected: "bg-emerald-400 shadow-emerald-400/50", disconnected: "bg-red-400 shadow-red-400/50", error: "bg-amber-400 shadow-amber-400/50",
    running: "bg-emerald-400 shadow-emerald-400/50", stopped: "bg-zinc-500", connecting: "bg-amber-400 shadow-amber-400/50"
  };
  const cls = c[status] || c.stopped; const p = status === "connected" || status === "running" || status === "connecting";
  return <span className={`inline-block w-2 h-2 rounded-full shadow-lg ${cls} ${p ? "pulse-dot" : ""}`} />;
};

var TypeBadge = ({ type }) => {
  const m = { prop: ["PROP", "orange"], real: ["REAL", "blue"], demo: ["DEMO", "gray"], test: ["TEST", "purple"] };
  const [l, c] = m[type] || ["?", "gray"]; return <Badge color={c}>{l}</Badge>;
};

var ModeBadge = ({ mode }) => {
  const m = { auto: ["FULL AUTO", "green"], semi: ["SEMI-AUTO", "yellow"], monitor: ["MONITOR", "gray"], virtual: ["VIRTUAL", "purple"] };
  const [l, c] = m[mode] || ["?", "gray"]; return <Badge color={c}>{l}</Badge>;
};

var Stat = ({ label, value, color }) => (
  <div className="bg-zinc-800/50 rounded-lg p-3"><div className="text-xs text-zinc-500 mb-1">{label}</div>
    <div className={`font-mono text-lg font-semibold ${color || "text-zinc-200"}`}>{value}</div></div>);

var AC_COLORS = { forex: "text-blue-400", index: "text-amber-400", energy: "text-orange-400", metal: "text-yellow-300", crypto: "text-violet-400" };
var AC_LABEL = { forex: "FX", index: "IDX", energy: "OIL", metal: "XAU", crypto: "CRYP" };

// =====================================================================
//  UTC formatting helper — used across all tabs
// =====================================================================
var formatUtc = (isoOrEpoch) => {
  if (!isoOrEpoch) return "\u2014";
  let d;
  if (typeof isoOrEpoch === "number") d = new Date(isoOrEpoch * 1000);
  else {
    let s = String(isoOrEpoch);
    // SQLite datetime('now') returns "YYYY-MM-DD HH:MM:SS" without timezone.
    // JS new Date() would parse that as LOCAL time. Force UTC by appending Z.
    if (s.length >= 19 && !s.includes("T") && !s.includes("Z") && !s.includes("+")) {
      s = s.replace(" ", "T") + "Z";
    }
    d = new Date(s);
  }
  if (isNaN(d.getTime())) return "\u2014";
  const pad = (n) => String(n).padStart(2, "0");
  return `${pad(d.getUTCMonth()+1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`;
};

// =====================================================================
//  Error Boundary
// =====================================================================
class ErrorBoundary extends React.Component {
  constructor(props) { super(props); this.state = { error: null, info: null }; }
  static getDerivedStateFromError(error) { return { error }; }
  componentDidCatch(error, info) {
    console.error('ErrorBoundary caught:', error, info);
    this.setState({ info });
  }
  render() {
    if (this.state.error) {
      return React.createElement('div', { className: 'p-4 border border-red-500/50 bg-red-500/10 rounded-xl m-4' },
        React.createElement('div', { className: 'text-red-400 text-sm font-bold mb-2' }, 'React render error'),
        React.createElement('pre', { className: 'text-xs text-red-300 font-mono whitespace-pre-wrap' },
          String(this.state.error) + '\n' + (this.state.error?.stack || '') + '\n' +
          (this.state.info?.componentStack || '')),
        React.createElement('button', {
          className: 'mt-3 px-3 py-1 text-xs bg-zinc-800 border border-zinc-600 text-zinc-300 rounded hover:bg-zinc-700',
          onClick: () => this.setState({ error: null, info: null })
        }, 'Retry')
      );
    }
    return this.props.children;
  }
}

// =====================================================================
//  WebSocket Manager
// =====================================================================
class WsManager {
  constructor() { this.ws = null; this.handlers = {}; this.reconnectTimer = null; this.connected = false; }
  connect(url) {
    if (this.ws) { try { this.ws.close(); } catch { } }
    this.ws = new WebSocket(url);
    this.ws.onopen = () => {
      this.connected = true; this._emit('connection', { connected: true });
      this.send({ cmd: 'get_terminals' }); this.send({ cmd: 'get_positions' });
      this.send({ cmd: 'get_strategies' }); this.send({ cmd: 'get_events', filter: { limit: 100 } });
    };
    this.ws.onmessage = (e) => {
      try {
        const msg = JSON.parse(e.data);
        if (msg.cmd) this._emit(`cmd:${msg.cmd}`, msg); if (msg.event) this._emit(`event:${msg.event}`, msg.data);
        this._emit('message', msg);
      } catch (err) { console.error('[WS] Message handler error:', err, 'data:', e.data?.substring?.(0, 200)); }
    };
    this.ws.onclose = () => {
      this.connected = false; this._emit('connection', { connected: false });
      this.reconnectTimer = setTimeout(() => this.connect(url), 3000);
    };
    this.ws.onerror = () => { };
  }
  send(data) { if (this.ws && this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(data)); }
  on(event, handler) { if (!this.handlers[event]) this.handlers[event] = []; this.handlers[event].push(handler); }
  off(event, handler) { if (this.handlers[event]) this.handlers[event] = this.handlers[event].filter(h => h !== handler); }
  _emit(event, data) { (this.handlers[event] || []).forEach(h => h(data)); }
}
var wsManager = new WsManager();
