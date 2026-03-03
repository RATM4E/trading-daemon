// =====================================================================
//  Main App: useWs hook, App component, ReactDOM render
// =====================================================================

function useWs() {
  const [connected, setConnected] = useState(false);
  const [terminals, setTerminals] = useState([]);
  const [positions, setPositions] = useState([]);
  const [strategies, setStrategies] = useState({ discovered: [], running: [] });
  const [logs, setLogs] = useState([]);
  const [terminalDetail, setTerminalDetail] = useState(null);
  const [discoveredTerminals, setDiscoveredTerminals] = useState(null);
  const [pauseState, setPauseState] = useState({ paused: false, pauseUntil: null, pauseReason: null });

  useEffect(() => {
    const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const host = window.location.host || 'localhost:8080';
    wsManager.connect(`${proto}//${host}/ws`);
    const onConn = ({ connected: c }) => setConnected(c);
    const onTerminals = (msg) => {
      setTerminals(msg.data || []);
      setPauseState({ paused: !!msg.globalPaused, pauseUntil: msg.pauseUntil || null, pauseReason: msg.pauseReason || null });
    };
    const onPositions = (msg) => setPositions(msg.data || []);
    const onStrategies = (msg) => setStrategies(msg);
    const onEvents = (msg) => setLogs(msg.data || []);
    const onTerminalDetail = (msg) => {
      console.log('[DEBUG] terminal_detail received:', JSON.stringify(msg).substring(0, 500));
      if (msg.error) { setTerminalDetail({ error: msg.error, terminal: msg.terminal || '' }); return; }
      setTerminalDetail(msg);
    };
    const onDiscovered = (msg) => {
      const data = msg.data || [];
      if (msg.unreachable > 0) data.__unreachable = msg.unreachable;
      setDiscoveredTerminals(data);
    };
    const onTerminalStatus = (data) => setTerminals(prev => prev.map(t => t.id === data.id ? { ...t, ...data } : t));
    const onPositionClosed = (data) => setPositions(prev => prev.filter(p => !(p.ticket === data.ticket && p.terminal === data.terminal)));
    const onStrategyStatus = (data) => setStrategies(prev => {
      const running = [...prev.running]; const idx = running.findIndex(r => r.name === data.name && r.terminal === data.terminal);
      if (data.status === 'stopped' || data.status === 'error') return { ...prev, running: running.filter((_, i) => i !== idx) };
      if (idx >= 0) running[idx] = { ...running[idx], ...data }; else running.push(data); return { ...prev, running };
    });
    const onStrategyEnabled = (data) => setStrategies(prev => ({
      ...prev, discovered: (prev.discovered || []).map(s => s.name === data.name ? { ...s, enabled: data.enabled } : s)
    }));
    const onStrategyDisabled = (data) => setStrategies(prev => ({
      ...prev, discovered: (prev.discovered || []).map(s => s.name === data.name ? { ...s, enabled: data.enabled } : s)
    }));
    const onLogEntry = (data) => setLogs(prev => [data, ...prev].slice(0, 200));
    const onConfigChanged = (msg) => { if (msg.ok) wsManager.send({ cmd: 'get_terminals' }); };
    const onEmergency = () => { wsManager.send({ cmd: 'get_terminals' }); wsManager.send({ cmd: 'get_positions' }); wsManager.send({ cmd: 'get_strategies' }); };
    const onTerminalDeleted = (data) => setTerminals(prev => prev.filter(t => t.id !== data.id));
    const onTerminalEnabled = (msg) => { if (msg.ok) wsManager.send({ cmd: 'get_terminals' }); };
    const onStrategyToggle = (msg) => { if (msg.ok) { wsManager.send({ cmd: 'get_strategies' }); } };
    const onReorder = (msg) => { if (msg.ok) wsManager.send({ cmd: 'get_terminals' }); };
    const onPauseState = (data) => setPauseState({ paused: !!data.paused, pauseUntil: data.pauseUntil || null, pauseReason: data.pauseReason || null });
    const onPauseToggle = (msg) => { if (msg.ok) setPauseState({ paused: !!msg.paused, pauseUntil: msg.pauseUntil || null, pauseReason: msg.pauseReason || null }); };

    wsManager.on('connection', onConn); wsManager.on('cmd:terminals', onTerminals);
    wsManager.on('cmd:positions', onPositions); wsManager.on('cmd:strategies', onStrategies);
    wsManager.on('cmd:events', onEvents); wsManager.on('cmd:terminal_detail', onTerminalDetail);
    wsManager.on('cmd:get_terminal_detail', onTerminalDetail);  // catch backend errors (cmd echoed as-is)
    wsManager.on('cmd:discover_terminals', onDiscovered);
    wsManager.on('event:terminal_status', onTerminalStatus); wsManager.on('event:position_closed', onPositionClosed);
    wsManager.on('event:strategy_status', onStrategyStatus); wsManager.on('event:log_entry', onLogEntry);
    wsManager.on('event:strategy_enabled', onStrategyEnabled); wsManager.on('event:strategy_disabled', onStrategyDisabled);
    wsManager.on('event:emergency_close_all', onEmergency);
    wsManager.on('event:terminal_deleted', onTerminalDeleted);
    wsManager.on('cmd:toggle_terminal_enabled', onTerminalEnabled);
    wsManager.on('cmd:enable_strategy', onStrategyToggle); wsManager.on('cmd:disable_strategy', onStrategyToggle);
    wsManager.on('cmd:reorder_terminals', onReorder);
    wsManager.on('cmd:toggle_news_guard', onConfigChanged);
    wsManager.on('cmd:toggle_no_trade', onConfigChanged);
    wsManager.on('cmd:unblock_3sl', onConfigChanged);
    wsManager.on('cmd:set_mode', onConfigChanged);
    wsManager.on('event:pause_state', onPauseState);
    wsManager.on('cmd:toggle_pause', onPauseToggle);
    return () => {
      wsManager.off('connection', onConn); wsManager.off('cmd:terminals', onTerminals);
      wsManager.off('cmd:positions', onPositions); wsManager.off('cmd:strategies', onStrategies);
      wsManager.off('cmd:events', onEvents); wsManager.off('cmd:terminal_detail', onTerminalDetail);
      wsManager.off('cmd:get_terminal_detail', onTerminalDetail);
      wsManager.off('cmd:discover_terminals', onDiscovered);
      wsManager.off('event:terminal_status', onTerminalStatus); wsManager.off('event:position_closed', onPositionClosed);
      wsManager.off('event:strategy_status', onStrategyStatus); wsManager.off('event:log_entry', onLogEntry);
      wsManager.off('event:strategy_enabled', onStrategyEnabled); wsManager.off('event:strategy_disabled', onStrategyDisabled);
      wsManager.off('event:emergency_close_all', onEmergency);
      wsManager.off('event:terminal_deleted', onTerminalDeleted);
      wsManager.off('cmd:toggle_terminal_enabled', onTerminalEnabled);
      wsManager.off('cmd:enable_strategy', onStrategyToggle); wsManager.off('cmd:disable_strategy', onStrategyToggle);
      wsManager.off('cmd:reorder_terminals', onReorder);
      wsManager.off('cmd:toggle_news_guard', onConfigChanged);
      wsManager.off('cmd:toggle_no_trade', onConfigChanged);
      wsManager.off('cmd:unblock_3sl', onConfigChanged);
      wsManager.off('cmd:set_mode', onConfigChanged);
      wsManager.off('event:pause_state', onPauseState);
      wsManager.off('cmd:toggle_pause', onPauseToggle);
    };
  }, []);

  useEffect(() => {
    if (!connected) return;
    const p = setInterval(() => wsManager.send({ cmd: 'get_positions' }), 5000);
    const t = setInterval(() => wsManager.send({ cmd: 'get_terminals' }), 10000);
    return () => { clearInterval(p); clearInterval(t); };
  }, [connected]);

  const send = useCallback((data) => wsManager.send(data), []);
  const refresh = useCallback(() => {
    wsManager.send({ cmd: 'get_terminals' }); wsManager.send({ cmd: 'get_positions' });
    wsManager.send({ cmd: 'get_strategies' }); wsManager.send({ cmd: 'get_events', filter: { limit: 100 } });
  }, []);
  const openDetail = useCallback((termId) => { setTerminalDetail(null); wsManager.send({ cmd: 'get_terminal_detail', terminal: termId }); }, []);
  const closeDetail = useCallback(() => setTerminalDetail(null), []);
  const discover = useCallback(() => { setDiscoveredTerminals(null); wsManager.send({ cmd: 'discover_terminals' }); }, []);

  return {
    connected, terminals, positions, strategies, logs, terminalDetail, discoveredTerminals, pauseState,
    send, refresh, openDetail, closeDetail, discover
  };
}

var TABS = [
  { id: "terminals", label: "Terminals", icon: "\u25C8" }, { id: "positions", label: "Positions", icon: "\u25F1" },
  { id: "strategies", label: "Strategies", icon: "\u25B6" }, { id: "sizing", label: "Sizing", icon: "\u2696" },
  { id: "virtual", label: "Virtual", icon: "\u25CF" },
  { id: "tester", label: "Tester", icon: "\u25B7" },
  { id: "log", label: "Log", icon: "\u2261" },
];

function App() {
  const { connected, terminals, positions, strategies, logs, terminalDetail, discoveredTerminals, pauseState,
    send, refresh, openDetail, closeDetail, discover } = useWs();
  const [tab, setTab] = useState("terminals");
  const [showEmergency, setShowEmergency] = useState(false);
  const [showShutdown, setShowShutdown] = useState(false);
  const [killTerminals, setKillTerminals] = useState(false);
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [detailTerminal, setDetailTerminal] = useState(null);
  const [tradeChartTerminal, setTradeChartTerminal] = useState(null);
  const [tradeChartTicket, setTradeChartTicket] = useState(null);
  const [time, setTime] = useState(new Date());
  const [showPauseModal, setShowPauseModal] = useState(false);
  const [pauseDuration, setPauseDuration] = useState(0);
  const [pauseReason, setPauseReason] = useState("");

  useEffect(() => { const t = setInterval(() => setTime(new Date()), 1000); return () => clearInterval(t); }, []);

  const totalPnl = positions.reduce((s, p) => s + (p.pnl || 0), 0);
  const openCount = positions.length;
  const connectedCount = terminals.filter(t => t.status === "connected").length;

  const handleOpenDetail = (termId) => { setDetailTerminal(termId); openDetail(termId); };
  const handleCloseDetail = () => { setDetailTerminal(null); closeDetail(); };
  const handleDiscover = () => { setShowDiscovery(true); discover(); };

  return (
    <div className="min-h-screen">
      {/* Header */}
      <div className="border-b border-zinc-800 bg-zinc-950/95 sticky top-0 z-50">
        <div className="max-w-7xl mx-auto px-4"><div className="flex items-center justify-between h-12">
          <div className="flex items-center gap-4">
            <div className="flex items-center gap-2"><StatusDot status={connected ? "connected" : "disconnected"} />
              <span className="text-sm font-semibold tracking-tight text-zinc-200">TRADING DAEMON</span>
              <span className="text-xs text-zinc-600">v0.7</span></div>
            <div className="h-4 w-px bg-zinc-800" />
            <div className="flex items-center gap-3 text-xs text-zinc-500">
              <span>{connectedCount}/{terminals.length} terminals</span><span>{openCount} positions</span>
              <span className={totalPnl >= 0 ? "text-emerald-400" : "text-red-400"}>P/L: {totalPnl >= 0 ? "+" : ""}${totalPnl.toFixed(2)}</span>
            </div></div>
          <div className="flex items-center gap-3">
            {!connected && <span className="text-xs text-red-400 animate-pulse">Disconnected</span>}
            <span className="text-xs text-zinc-500">UTC {String(time.getUTCHours()).padStart(2, '0')}:{String(time.getUTCMinutes()).padStart(2, '0')}:{String(time.getUTCSeconds()).padStart(2, '0')}</span>
            <span className="text-xs text-zinc-600">{time.toLocaleTimeString("en-GB")}</span>
            <span className="text-xs text-zinc-700">{String(time.getMonth() + 1).padStart(2, '0')}/{String(time.getDate()).padStart(2, '0')}/{time.getFullYear()}</span>
            <div className="h-4 w-px bg-zinc-800" />
            <button onClick={refresh} className="px-2 py-1 text-xs text-zinc-500 hover:text-zinc-300 border border-transparent hover:border-zinc-700 rounded">Refresh</button>
            {pauseState.paused ? (
              <button onClick={() => send({ cmd: 'toggle_pause' })}
                className="px-3 py-1 text-xs bg-amber-600/20 border border-amber-500/40 text-amber-300 rounded hover:bg-amber-600/30 font-medium animate-pulse"
                title="Click to resume trading">{"\u25b6\ufe0f"} RESUME</button>
            ) : (
              <button onClick={() => setShowPauseModal(true)}
                className="px-3 py-1 text-xs bg-amber-600/10 border border-amber-500/30 text-amber-400/70 rounded hover:bg-amber-600/20 hover:text-amber-300 font-medium"
                title="Pause all new trading">{"\u23f8\ufe0f"} PAUSE</button>
            )}
            <button onClick={() => setShowEmergency(true)}
              className="px-3 py-1 text-xs bg-red-600/20 border border-red-500/40 text-red-300 rounded hover:bg-red-600/30 font-medium">EMERGENCY</button>
            <button onClick={() => setShowShutdown(true)}
              className="px-3 py-1 text-xs bg-zinc-800 border border-zinc-600/50 text-zinc-400 rounded hover:bg-zinc-700 hover:text-zinc-300">SHUTDOWN</button>
          </div></div></div></div>

      {/* Paused Banner */}
      {pauseState.paused && (
        <div className="bg-amber-600/15 border-b border-amber-500/30">
          <div className="max-w-7xl mx-auto px-4 py-2 flex items-center justify-between">
            <div className="flex items-center gap-3">
              <span className="text-amber-400 text-sm font-semibold">{"\u23f8\ufe0f"} TRADING PAUSED</span>
              {pauseState.pauseUntil && (() => {
                const remaining = Math.max(0, Math.round((new Date(pauseState.pauseUntil) - time) / 60000));
                return <span className="text-xs text-amber-300/70">{remaining > 0 ? `${remaining}m remaining` : 'expiring...'}</span>;
              })()}
              {pauseState.pauseReason && <span className="text-xs text-zinc-400">{"\u2014"} {pauseState.pauseReason}</span>}
            </div>
            <button onClick={() => send({ cmd: 'toggle_pause' })}
              className="px-3 py-1 text-xs bg-emerald-600/20 border border-emerald-500/40 text-emerald-300 rounded hover:bg-emerald-600/30 font-medium">{"\u25b6\ufe0f"} Resume Trading</button>
          </div>
        </div>
      )}

      {/* Tabs */}
      <div className="border-b border-zinc-800"><div className="max-w-7xl mx-auto px-4"><div className="flex gap-0">
        {TABS.map(t => (<button key={t.id} onClick={() => { setTab(t.id); if (t.id !== "terminals") handleCloseDetail(); }}
          className={`px-4 py-2.5 text-sm border-b-2 ${tab === t.id ? "border-zinc-400 text-zinc-200" : "border-transparent text-zinc-500 hover:text-zinc-300 hover:border-zinc-700"}`}>
          <span className="mr-1.5 opacity-50">{t.icon}</span>{t.label}
          {t.id === "positions" && openCount > 0 && <span className="ml-1.5 text-xs bg-zinc-800 px-1.5 py-0.5 rounded">{openCount}</span>}
        </button>))}
      </div></div></div>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 py-4">
        {tab === "terminals" && !detailTerminal && <TerminalsTab terminals={terminals} strategies={strategies} send={send} onOpenDetail={handleOpenDetail} onDiscover={handleDiscover} onGoToStrategies={() => setTab("strategies")} />}
        {tab === "terminals" && detailTerminal && <ErrorBoundary><TerminalDetailView detail={terminalDetail} positions={positions} onClose={handleCloseDetail} send={send} onTradeChart={(t, ticket) => { setTradeChartTerminal(t); setTradeChartTicket(ticket); }} /></ErrorBoundary>}
        {tab === "positions" && <PositionsTab positions={positions} send={send} onTradeChart={(t, ticket) => { setTradeChartTerminal(t); setTradeChartTicket(ticket); }} />}
        {tab === "strategies" && <StrategiesTab strategies={strategies} terminals={terminals} send={send} />}
        {tab === "sizing" && <SizingTab terminals={terminals} strategies={strategies} send={send} />}
        {tab === "virtual" && <VirtualTab terminals={terminals} send={send} onTradeChart={(t, ticket) => { setTradeChartTerminal(t); setTradeChartTicket(ticket); }} />}
        <div style={{ display: tab === "tester" ? "block" : "none" }}><BacktestTab terminals={terminals} send={send} connected={connected} /></div>
        {tab === "log" && <LogTab logs={logs} />}
      </div>

      {/* Discovery Modal */}
      {showDiscovery && <DiscoveryPanel discovered={discoveredTerminals} onClose={() => setShowDiscovery(false)} send={send} />}

      {/* Trade Chart Modal */}
      {tradeChartTerminal && tradeChartTicket && (
        <TradeChartModal terminal={tradeChartTerminal} ticket={tradeChartTicket} send={send}
          onClose={() => { setTradeChartTerminal(null); setTradeChartTicket(null); }} />
      )}

      {/* Pause Modal */}
      {showPauseModal && (
        <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={() => setShowPauseModal(false)}>
          <div className="bg-zinc-900 border border-amber-500/30 rounded-xl p-6 max-w-md" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg text-amber-400 font-semibold mb-2">{"\u23f8\ufe0f"} Pause Trading</h2>
            <p className="text-sm text-zinc-400 mb-4">Block all new entries on all terminals. Existing positions and SL management continue normally.</p>
            <div className="mb-4">
              <label className="text-xs text-zinc-500 block mb-1.5">Duration</label>
              <div className="flex gap-2">
                {[0, 30, 60, 120, 240].map(m => (
                  <button key={m} onClick={() => setPauseDuration(m)}
                    className={`px-3 py-1.5 text-xs rounded border ${pauseDuration === m
                      ? 'bg-amber-600/20 border-amber-500/50 text-amber-300'
                      : 'bg-zinc-800 border-zinc-700 text-zinc-400 hover:border-zinc-600'}`}>
                    {m === 0 ? '\u221e' : m < 60 ? `${m}m` : `${m/60}h`}
                  </button>
                ))}
              </div>
              <span className="text-xs text-zinc-600 mt-1 block">{pauseDuration === 0 ? 'Indefinite \u2014 manual resume required' : `Auto-resume after ${pauseDuration} minutes`}</span>
            </div>
            <div className="mb-4">
              <label className="text-xs text-zinc-500 block mb-1.5">Reason (optional)</label>
              <input type="text" value={pauseReason} onChange={e => setPauseReason(e.target.value)}
                placeholder="e.g. NFP release, end of day"
                className="w-full px-3 py-1.5 text-sm bg-zinc-800 border border-zinc-700 rounded text-zinc-300 placeholder-zinc-600 focus:border-amber-500/50 focus:outline-none" />
            </div>
            <div className="flex gap-3">
              <button onClick={() => { setShowPauseModal(false); setPauseDuration(0); setPauseReason(""); }}
                className="flex-1 px-4 py-2 text-sm bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-300 hover:bg-zinc-700">Cancel</button>
              <button onClick={() => {
                send({ cmd: 'toggle_pause', duration_min: pauseDuration, reason: pauseReason || undefined });
                setShowPauseModal(false); setPauseDuration(0); setPauseReason("");
              }} className="flex-1 px-4 py-2 text-sm bg-amber-600/30 border border-amber-500/50 rounded-lg text-amber-300 hover:bg-amber-600/40 font-medium">PAUSE</button>
            </div>
          </div></div>
      )}

      {/* Emergency Modal */}
      {showEmergency && (
        <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={() => setShowEmergency(false)}>
          <div className="bg-zinc-900 border border-red-500/30 rounded-xl p-6 max-w-md" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg text-red-400 font-semibold mb-2">Emergency Close All</h2>
            <p className="text-sm text-zinc-400 mb-6">This will immediately close ALL positions on ALL terminals, stop all strategies, and set everything to Monitor mode.</p>
            <div className="flex gap-3">
              <button onClick={() => setShowEmergency(false)} className="flex-1 px-4 py-2 text-sm bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-300 hover:bg-zinc-700">Cancel</button>
              <button onClick={() => { send({ cmd: 'emergency_close_all' }); setShowEmergency(false); }}
                className="flex-1 px-4 py-2 text-sm bg-red-600/30 border border-red-500/50 rounded-lg text-red-300 hover:bg-red-600/40 font-medium">CONFIRM</button>
            </div></div></div>)}

      {/* Shutdown Modal */}
      {showShutdown && (
        <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={() => setShowShutdown(false)}>
          <div className="bg-zinc-900 border border-zinc-600/50 rounded-xl p-6 max-w-md" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg text-zinc-200 font-semibold mb-2">Shutdown Daemon</h2>
            <p className="text-sm text-zinc-400 mb-4">This will stop all trading, disconnect all terminals, and shut down the daemon process.</p>
            <label className="flex items-center gap-2 mb-6 cursor-pointer select-none">
              <input type="checkbox" checked={killTerminals} onChange={e => setKillTerminals(e.target.checked)}
                className="w-4 h-4 rounded border-zinc-600 bg-zinc-800 accent-red-500" />
              <span className="text-sm text-zinc-300">Also close MT5 terminals</span>
            </label>
            <div className="flex gap-3">
              <button onClick={() => setShowShutdown(false)} className="flex-1 px-4 py-2 text-sm bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-300 hover:bg-zinc-700">Cancel</button>
              <button onClick={() => { send({ cmd: 'shutdown_daemon', kill_terminals: killTerminals }); setShowShutdown(false); }}
                className="flex-1 px-4 py-2 text-sm bg-zinc-700/50 border border-zinc-600 rounded-lg text-zinc-200 hover:bg-zinc-600/50 font-medium">Shutdown</button>
            </div></div></div>)}
    </div>
  );
}
ReactDOM.render(<App />, document.getElementById("root"));
