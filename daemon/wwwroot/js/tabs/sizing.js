// =====================================================================
//  Tab: Sizing
// =====================================================================

var SizingTab = ({ terminals, strategies, send }) => {
  const [terminal, setTerminal] = useState("");
  const [sizing, setSizing] = useState(null);
  const [loading, setLoading] = useState(false);
  const [scale, setScale] = useState(1.0);
  const [filter, setFilter] = useState("all");
  const [tierFilter, setTierFilter] = useState("all");
  const [activeStrategy, setActiveStrategy] = useState("");

  // Resizable columns
  const defaultWidths = [110, 60, 40, 50, 70, 80, 80, 80, 90, 45];
  const [colWidths, setColWidths] = useState(defaultWidths);
  const resizing = React.useRef(null);

  React.useEffect(() => {
    const onMove = (e) => {
      if (!resizing.current) return;
      const { col, startX, startW } = resizing.current;
      const delta = e.clientX - startX;
      setColWidths(prev => { const next = [...prev]; next[col] = Math.max(35, startW + delta); return next; });
    };
    const onUp = () => { resizing.current = null; document.body.style.cursor = ''; };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => { window.removeEventListener('mousemove', onMove); window.removeEventListener('mouseup', onUp); };
  }, []);

  const startResize = (col, e) => {
    e.preventDefault();
    resizing.current = { col, startX: e.clientX, startW: colWidths[col] };
    document.body.style.cursor = 'col-resize';
  };
  const conn = terminals.filter(t => t.status === "connected");
  const running = strategies.running || [];

  useEffect(() => {
    const onSizing = (msg) => { if (msg.cmd === "sizing") { setSizing(msg); setLoading(false); } };
    const onInitSizing = (msg) => { if (msg.cmd === "init_sizing" && msg.ok && terminal) loadSizing(terminal); };
    const onSaveSizing = (msg) => { if (msg.cmd === "save_sizing" && msg.ok && terminal) loadSizing(terminal); };
    const onResetSizing = (msg) => { if (msg.cmd === "reset_sizing" && msg.ok && terminal) loadSizing(terminal); };
    wsManager.on('cmd:sizing', onSizing); wsManager.on('cmd:init_sizing', onInitSizing); wsManager.on('cmd:save_sizing', onSaveSizing); wsManager.on('cmd:reset_sizing', onResetSizing);
    return () => { wsManager.off('cmd:sizing', onSizing); wsManager.off('cmd:init_sizing', onInitSizing); wsManager.off('cmd:save_sizing', onSaveSizing); wsManager.off('cmd:reset_sizing', onResetSizing); };
  }, [terminal]);

  const loadSizing = (tid) => { setTerminal(tid); setLoading(true); send({ cmd: "get_sizing", terminal: tid }); };
  const initSizing = (strat) => { setActiveStrategy(strat); send({ cmd: "init_sizing", terminal, strategy: strat }); };
  const resetSizing = () => {
    const strat = activeStrategy || running.find(r => r.terminal === terminal)?.name;
    if (strat) send({ cmd: "reset_sizing", terminal, strategy: strat });
  };
  const saveSym = (symbol, field, value) => { send({ cmd: "save_sizing", terminal, symbol, [field]: value }); };

  // Bulk actions
  const bulkEnable = (aclass, enable) => {
    if (!sizing) return;
    let targets = aclass === "all" ? sizing.rows : sizing.rows.filter(r => r.assetClass === aclass);
    if (tierFilter !== "all") targets = targets.filter(r => (r.tier || 'T1') === tierFilter);
    targets.filter(r => r.available !== false).forEach(r => send({ cmd: "save_sizing", terminal, symbol: r.symbol, enabled: enable }));
    setTimeout(() => loadSizing(terminal), 500);
  };
  const applyScale = () => {
    if (!sizing) return;
    let targets = filter === "all" ? sizing.rows : filter === "avail" ? sizing.rows.filter(r => r.available !== false) : sizing.rows.filter(r => r.assetClass === filter);
    if (tierFilter !== "all") targets = targets.filter(r => (r.tier || 'T1') === tierFilter);
    targets.filter(r => r.available !== false).forEach(r => {
      const scaled = Math.round(Math.min(1.0, r.riskFactor * scale) * 100) / 100;
      send({ cmd: "save_sizing", terminal, symbol: r.symbol, risk_factor: Math.max(0, Math.min(1.0, scaled)) });
    });
    setTimeout(() => loadSizing(terminal), 500);
  };

  // Derived
  const allRows = sizing?.rows || [];
  const availableRows = allRows.filter(r => r.available !== false);
  const unavailCount = allRows.length - availableRows.length;
  const classes = [...new Set(allRows.map(r => r.assetClass))].sort();
  const tiers = [...new Set(allRows.map(r => r.tier || 'T1'))].sort();
  const preFiltered = allRows
    .filter(r => filter === "all" ? true : filter === "avail" ? r.available !== false : r.assetClass === filter)
    .filter(r => tierFilter === "all" ? true : (r.tier || 'T1') === tierFilter);
  const filtered = preFiltered.sort((a, b) => {
    if (a.available === b.available) return 0;
    return a.available === false ? 1 : -1;
  });
  const classCounts = {};
  allRows.forEach(r => { const k = r.assetClass; if (!classCounts[k]) classCounts[k] = { total: 0, on: 0, avail: 0 }; classCounts[k].total++; if (r.enabled && r.available !== false) classCounts[k].on++; if (r.available !== false) classCounts[k].avail++; });
  const tierCounts = {};
  allRows.forEach(r => { const k = r.tier || 'T1'; if (!tierCounts[k]) tierCounts[k] = { total: 0, on: 0 }; tierCounts[k].total++; if (r.enabled && r.available !== false) tierCounts[k].on++; });

  if (!conn.length) return (<div className="text-center text-zinc-600 py-12">No terminals connected</div>);

  return (<div>
    {/* Terminal + Init */}
    <div className="flex items-center gap-3 mb-4">
      <select value={terminal} onChange={e => loadSizing(e.target.value)} className="bg-zinc-800 border border-zinc-700 rounded px-3 py-1.5 text-sm text-zinc-300">
        <option value="">Select terminal...</option>{conn.map(t => <option key={t.id} value={t.id}>{t.id}</option>)}</select>
      {terminal && running.filter(r => r.terminal === terminal).map(r => (
        <button key={r.name} onClick={() => initSizing(r.name)} className="px-3 py-1.5 text-xs bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded hover:bg-emerald-600/30">
          Init from {r.name}</button>))}
      {terminal && !running.some(r => r.terminal === terminal) && (strategies.discovered || []).map(s => (
        <button key={s.name} onClick={() => initSizing(s.name)} className="px-3 py-1.5 text-xs bg-zinc-700/50 border border-zinc-600/30 text-zinc-400 rounded hover:bg-zinc-700">
          Init from {s.name}</button>))}
      {loading && <span className="text-xs text-zinc-500">Loading...</span>}
    </div>

    {sizing && allRows.length > 0 && (<div>
      {/* Filter bar */}
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-1 flex-wrap">
          <button onClick={() => { setFilter("all"); setTierFilter("all"); }} className={`px-2.5 py-1 text-xs rounded ${filter === "all" && tierFilter === "all" ? "bg-zinc-700 text-zinc-200" : "text-zinc-500 hover:text-zinc-300"}`}>
            All ({allRows.length})</button>
          {unavailCount > 0 && <button onClick={() => setFilter("avail")} className={`px-2.5 py-1 text-xs rounded ${filter === "avail" ? "bg-zinc-700 text-zinc-200" : "text-zinc-500 hover:text-zinc-300"}`}>
            <span className="text-emerald-400">Available</span> <span className="text-zinc-600">{availableRows.length}/{allRows.length}</span></button>}
          {classes.map(c => (
            <button key={c} onClick={() => setFilter(c)} className={`px-2.5 py-1 text-xs rounded flex items-center gap-1 ${filter === c ? "bg-zinc-700 text-zinc-200" : "text-zinc-500 hover:text-zinc-300"}`}>
              <span className={AC_COLORS[c] || "text-zinc-400"}>{AC_LABEL[c] || c}</span>
              <span className="text-zinc-600">{classCounts[c]?.on}/{classCounts[c]?.total}</span>
            </button>))}
          <span className="mx-1 text-zinc-700">|</span>
          {tiers.map(t => (
            <button key={t} onClick={() => setTierFilter(tierFilter === t ? "all" : t)} className={`px-2 py-1 text-xs rounded ${tierFilter === t ? "bg-amber-600/30 text-amber-300" : "text-zinc-500 hover:text-zinc-300"}`}>
              {t} <span className="text-zinc-600">{tierCounts[t]?.on}/{tierCounts[t]?.total}</span>
            </button>))}
          <span className="mx-1 text-zinc-700">|</span>
          {filter !== "all" && (<>
            <button onClick={() => bulkEnable(filter, true)} className="px-2 py-1 text-xs text-emerald-400/70 border border-emerald-500/20 rounded hover:bg-emerald-600/10">
              Enable {AC_LABEL[filter] || filter}</button>
            <button onClick={() => bulkEnable(filter, false)} className="px-2 py-1 text-xs text-red-400/70 border border-red-500/20 rounded hover:bg-red-500/10">
              Disable {AC_LABEL[filter] || filter}</button>
          </>)}
          {filter === "all" && (<>
            <button onClick={() => bulkEnable("all", true)} className="px-2 py-1 text-xs text-emerald-400/70 border border-emerald-500/20 rounded hover:bg-emerald-600/10">
              Enable all</button>
            <button onClick={() => bulkEnable("all", false)} className="px-2 py-1 text-xs text-red-400/70 border border-red-500/20 rounded hover:bg-red-500/10">
              Disable all</button>
          </>)}
        </div>
      </div>

      {/* Stats + Scale */}
      <div className="flex items-center justify-between mb-3">
        <div className="text-xs text-zinc-500">
          {sizing.enabledCount}/{sizing.totalCount} enabled{unavailCount > 0 ? ` (${unavailCount} unavailable)` : ''} | Balance: ${sizing.balance?.toLocaleString()} | Base risk: ${sizing.baseRisk?.toLocaleString()}{sizing.riskType === 'pct' ? ` (${sizing.riskValue}%)` : ''} | Margin: ${sizing.totalMargin?.toLocaleString()} ({sizing.totalMarginPct}%)
        </div>
        <div className="flex items-center gap-2">
          <span className="text-xs text-zinc-500">Scale{filter !== "all" ? ` (${AC_LABEL[filter] || filter})` : ""}:</span>
          <input type="range" min="10" max="200" value={Math.round(scale * 100)} onChange={e => setScale(e.target.value / 100)}
            className="w-24 h-1 accent-zinc-500" />
          <span className="text-xs text-zinc-300 w-10">{scale.toFixed(2)}x</span>
          <button onClick={applyScale} className="px-2 py-1 text-xs bg-amber-600/20 border border-amber-500/30 text-amber-400 rounded hover:bg-amber-600/30">Apply</button>
          <button onClick={() => { setScale(1.0); resetSizing(); }} className="px-2 py-1 text-xs text-zinc-500 border border-zinc-700 rounded hover:bg-zinc-800">Reset</button>
        </div>
      </div>

      {/* Table */}
      <div className="overflow-auto border border-zinc-800 rounded-lg" style={{maxHeight:'calc(100vh - 280px)'}}>
      <table className="w-full text-xs border-collapse" style={{fontVariantNumeric:'tabular-nums', tableLayout:'fixed'}}>
        <colgroup>
          {colWidths.map((w, i) => <col key={i} style={{width: w + 'px'}} />)}
        </colgroup>
        <thead className="sticky top-0 z-10 bg-zinc-900"><tr className="text-zinc-500 border-b border-zinc-800">
          {["Symbol","Class","Tier","TF","Factor","ATR","Est.SL","Est.Lot","Margin","ON"].map((label, i) => (
            <th key={label} className={`py-2 ${i === 0 ? 'text-left pl-4' : i <= 3 ? 'text-left' : i === 9 ? 'text-center' : 'text-right'} relative select-none`}
              style={{width: colWidths[i] + 'px'}}>
              {label}
              {i < 9 && <span onMouseDown={e => startResize(i, e)}
                className="absolute right-0 top-0 h-full w-1.5 cursor-col-resize hover:bg-zinc-600/50" />}
            </th>))}
        </tr></thead>
        <tbody>{filtered.map(r => (
          <tr key={r.symbol} className={`border-b border-zinc-800/50 ${r.available === false ? 'opacity-25' : !r.enabled ? 'opacity-40' : ''} hover:bg-zinc-800/30`}>
            <td className="py-1.5 font-mono text-zinc-200 truncate pl-4">{r.symbol}</td>
            <td className={`${AC_COLORS[r.assetClass] || 'text-zinc-400'} uppercase truncate`}>{AC_LABEL[r.assetClass] || r.assetClass}</td>
            <td className={`font-mono ${r.tier === 'T1' ? 'text-emerald-400' : r.tier === 'T2' ? 'text-amber-400' : 'text-zinc-500'}`}>{r.tier || 'T1'}</td>
            <td className="text-zinc-400">{r.tf}</td>
            <td className="text-right">
              <input type="number" step="0.05" min="0" max="1.0" value={scale !== 1.0 ? Math.min(1.0, r.riskFactor * scale).toFixed(2) : r.riskFactor}
                onChange={e => { const v = parseFloat(e.target.value); if (!isNaN(v)) saveSym(r.symbol, "risk_factor", Math.max(0, Math.min(1.0, v))); }}
                disabled={r.available === false}
                className="w-16 bg-transparent border border-zinc-700 rounded px-1 py-0.5 text-right text-zinc-200 focus:border-zinc-500 disabled:text-zinc-600" /></td>
            <td className="text-right text-zinc-400">{r.atr > 0 ? r.atr.toFixed(r.atr < 1 ? 5 : 1) : '-'}</td>
            <td className="text-right text-zinc-400">{r.estSl > 0 ? r.estSl.toFixed(r.estSl < 1 ? 5 : 1) : '-'}</td>
            <td className="text-right text-zinc-300">{r.estLot > 0 ? r.estLot.toFixed(2) : '-'}</td>
            <td className="text-right text-zinc-400">{r.estMargin > 0 ? '$' + r.estMargin.toFixed(2) : '-'}</td>
            <td className="text-center">
              {r.available === false
                ? <span className="w-6 h-6 inline-flex items-center justify-center text-zinc-700 text-xs" title="Symbol not available on this terminal"> - </span>
                : <button onClick={() => saveSym(r.symbol, "enabled", !r.enabled)}
                    className={`w-6 h-6 rounded ${r.enabled ? 'bg-emerald-600/30 text-emerald-400' : 'bg-zinc-800 text-zinc-600'}`}>
                    {r.enabled ? '\u2713' : '\u2715'}</button>}</td>
          </tr>))}</tbody>
      </table>
      </div>
    </div>)}

    {sizing && allRows.length === 0 && terminal && (
      <div className="text-center text-zinc-600 py-12 border border-dashed border-zinc-800 rounded-lg">
        No sizing configured. Click "Init from strategy" above to populate defaults.</div>)}

    {!terminal && (<div className="text-center text-zinc-600 py-12">Select a terminal to manage position sizing</div>)}
  </div>);
};
