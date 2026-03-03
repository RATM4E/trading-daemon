// =====================================================================
//  Tab: Strategies
// =====================================================================

var StrategiesTab = ({ strategies, terminals, send }) => {
  const conn = terminals.filter(t => t.status === "connected"); const [sel, setSel] = useState({});

  // Drag & drop for available strategies
  const [sDragId, setSDragId] = useState(null);
  const [sDragOver, setSDragOver] = useState(null);
  const [stratOrder, setStratOrder] = useState(null);
  const sDragReady = useRef(null);
  useEffect(() => {
    const clear = () => { sDragReady.current = null; };
    window.addEventListener('mouseup', clear);
    return () => window.removeEventListener('mouseup', clear);
  }, []);

  const discovered = strategies.discovered || [];
  // Apply local reorder
  const ordered = stratOrder
    ? [...discovered].sort((a, b) => {
        const ai = stratOrder.indexOf(a.name);
        const bi = stratOrder.indexOf(b.name);
        return (ai === -1 ? 999 : ai) - (bi === -1 ? 999 : bi);
      })
    : discovered;

  return (<div className="grid grid-cols-2 gap-6"><div>
    <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Available Strategies</h3>
    <div className="space-y-2">{ordered.map(s => (
      <div key={s.name}
        draggable
        onDragStart={e => { if (sDragReady.current !== s.name) { e.preventDefault(); return; } setSDragId(s.name); e.dataTransfer.effectAllowed = 'move'; }}
        onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setSDragOver(s.name); }}
        onDragLeave={() => setSDragOver(null)}
        onDrop={e => {
          e.preventDefault(); setSDragOver(null);
          if (!sDragId || sDragId === s.name) return;
          const names = ordered.map(x => x.name);
          const from = names.indexOf(sDragId), to = names.indexOf(s.name);
          if (from < 0 || to < 0) return;
          names.splice(from, 1); names.splice(to, 0, sDragId);
          setStratOrder(names); setSDragId(null);
        }}
        onDragEnd={() => { setSDragId(null); setSDragOver(null); }}
        className={`bg-zinc-900/50 border rounded-lg p-3 transition-all
          ${s.enabled ? 'border-zinc-700' : 'border-zinc-800/50'}
          ${sDragId === s.name ? 'opacity-50 scale-[0.98]' : ''}
          ${sDragOver === s.name && sDragId !== s.name ? 'border-violet-500/50 bg-violet-500/5' : ''}`}>
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-2">
            <span className="text-zinc-600 hover:text-zinc-400 cursor-grab active:cursor-grabbing select-none text-xs"
              onMouseDown={() => { sDragReady.current = s.name; }} title="Drag to reorder">&#x2630;</span>
            <span className="font-mono text-sm text-zinc-200">{s.name}</span>
          </div>
          <div className="flex items-center gap-2">
            {s.magic_base > 0 && <span className="text-[10px] text-zinc-600 font-mono">magic: {s.magic_base}</span>}
            <button onClick={() => send({ cmd: 'open_strategy_folder', strategy: s.name })} title="Open strategy folder"
              className="text-zinc-600 hover:text-zinc-400 text-xs px-1 py-0.5 rounded hover:bg-zinc-700/30">&#x1F4C2;</button>
          </div>
        </div>
        <div className="flex items-center gap-2 mb-2">
          <button onClick={() => send({ cmd: s.enabled ? 'disable_strategy' : 'enable_strategy', strategy: s.name })}
            className={`px-2 py-0.5 text-[10px] rounded border transition-colors ${s.enabled
              ? 'text-emerald-400 border-emerald-500/30 bg-emerald-600/10 hover:bg-emerald-600/20'
              : 'text-zinc-500 border-zinc-700 hover:bg-zinc-800'}`}>
            {s.enabled ? '\u25CF Enabled' : '\u25CB Disabled'}</button>
        </div>
        <div className={`flex items-center gap-2 transition-opacity ${!s.enabled ? 'opacity-30 pointer-events-none' : ''}`}>
          <select value={sel[s.name] || ""} onChange={e => setSel(p => ({ ...p, [s.name]: e.target.value }))}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 flex-1">
            <option value="">Assign to...</option>{conn.map(t => <option key={t.id} value={t.id}>{t.id}</option>)}</select>
          <button onClick={() => sel[s.name] && send({ cmd: 'start_strategy', strategy: s.name, terminal: sel[s.name] })} disabled={!sel[s.name]}
            className="px-3 py-1 text-xs bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded hover:bg-emerald-600/30 disabled:opacity-30">Start</button>
        </div></div>))}
      {(!strategies.discovered || strategies.discovered.length === 0) && (
        <div className="text-sm text-zinc-600 py-8 text-center border border-dashed border-zinc-800 rounded-lg">No strategies found in strategies/ folder</div>)}</div></div>
    <div><h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Running</h3>
      {(!strategies.running || strategies.running.length === 0) ? (
        <div className="text-sm text-zinc-600 py-8 text-center border border-dashed border-zinc-800 rounded-lg">No strategies running</div>
      ) : (<div className="space-y-2">{strategies.running.map(s => (
        <div key={`${s.name}@${s.terminal}`} className="bg-zinc-900/50 border border-zinc-700/50 rounded-lg p-3">
          <div className="flex items-center justify-between mb-2"><div className="flex items-center gap-2">
            <StatusDot status={s.status || "running"} /><span className="font-mono text-sm text-zinc-200">{s.name}</span>
            <span className="text-xs text-zinc-500">&rarr; {s.terminal}</span></div>
            <div className="flex items-center gap-2">
              <span className="text-xs text-zinc-600">magic: {s.magic}</span>
              <button onClick={() => send({ cmd: 'open_strategy_folder', strategy: s.name })} title="Open strategy folder"
                className="text-zinc-600 hover:text-zinc-400 text-xs px-1 py-0.5 rounded hover:bg-zinc-700/30">&#x1F4C2;</button>
            </div></div>
          <div className="flex gap-2">
            <button onClick={() => { if (confirm(`Reload ${s.name}? (stop \u2192 restart)`)) send({ cmd: 'reload_strategy', strategy: s.name, terminal: s.terminal }); }}
              className="px-3 py-1 text-xs text-blue-400/70 border border-blue-500/20 rounded hover:bg-blue-500/10">&#x21BB; Reload</button>
            <button onClick={() => confirm(`Stop ${s.name}?`) && send({ cmd: 'stop_strategy', strategy: s.name, terminal: s.terminal })}
              className="px-3 py-1 text-xs text-red-400/70 border border-red-500/20 rounded hover:bg-red-500/10">Stop</button>
          </div>
        </div>))}</div>)}</div></div>);
};
