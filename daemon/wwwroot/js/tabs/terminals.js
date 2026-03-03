// =====================================================================
//  Tab: Terminals (tile grid with drag & drop)
//  Dependencies: ConfigPanel (terminal-settings.js),
//                DiscoveryPanel (terminal-discovery.js)
// =====================================================================

var TerminalsTab = ({ terminals, strategies, send, onOpenDetail, onDiscover, onGoToStrategies }) => {
  const [configuring, setConfiguring] = useState(null);
  const [dragId, setDragId] = useState(null);
  const [dragOverId, setDragOverId] = useState(null);
  const [deleteConfirm, setDeleteConfirm] = useState(null);
  const [deleteInput, setDeleteInput] = useState("");

  // --- Drag & Drop (handle-only) ---
  const dragReadyRef = useRef(null);
  useEffect(() => {
    const clearReady = () => { dragReadyRef.current = null; };
    window.addEventListener('mouseup', clearReady);
    return () => window.removeEventListener('mouseup', clearReady);
  }, []);
  const handleDragStart = (e, id) => {
    if (dragReadyRef.current !== id) { e.preventDefault(); return; }
    setDragId(id); e.dataTransfer.effectAllowed = 'move';
  };
  const handleDragOver = (e, id) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDragOverId(id); };
  const handleDragLeave = () => setDragOverId(null);
  const handleDrop = (e, targetId) => {
    e.preventDefault(); setDragOverId(null);
    if (!dragId || dragId === targetId) return;
    const ids = terminals.map(t => t.id);
    const fromIdx = ids.indexOf(dragId);
    const toIdx = ids.indexOf(targetId);
    if (fromIdx < 0 || toIdx < 0) return;
    ids.splice(fromIdx, 1);
    ids.splice(toIdx, 0, dragId);
    send({ cmd: 'reorder_terminals', order: ids });
    setDragId(null);
  };
  const handleDragEnd = () => { setDragId(null); setDragOverId(null); };

  // --- Delete ---
  const handleDelete = () => {
    if (!deleteConfirm || deleteInput !== deleteConfirm) return;
    send({ cmd: 'delete_terminal', terminal: deleteConfirm, confirm: true });
    setDeleteConfirm(null); setDeleteInput("");
  };

  if (terminals.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-20">
        <div className="w-16 h-16 rounded-2xl bg-zinc-800 border border-zinc-700 flex items-center justify-center mb-6">
          <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="text-zinc-500">
            <rect x="2" y="3" width="20" height="14" rx="2" /><path d="M8 21h8M12 17v4" /></svg>
        </div>
        <h2 className="text-lg text-zinc-300 font-medium mb-2">No terminals connected</h2>
        <p className="text-sm text-zinc-500 mb-6 text-center max-w-sm">Start MT5 terminals and scan for them.</p>
        <button onClick={onDiscover}
          className="px-4 py-2 bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded-lg text-sm hover:bg-emerald-600/30">
          Scan for MT5 Terminals
        </button>
      </div>
    );
  }

  return (
    <div>
      <div className="flex justify-end mb-3">
        <button onClick={onDiscover}
          className="px-3 py-1 text-xs bg-zinc-800 border border-zinc-700 rounded hover:bg-zinc-700 text-zinc-400 hover:text-zinc-200">
          + Discover Terminals
        </button>
      </div>
      <div className="space-y-3">
        {terminals.map(t => (
          <div key={t.id}
            draggable
            onDragStart={e => handleDragStart(e, t.id)}
            onDragOver={e => handleDragOver(e, t.id)}
            onDragLeave={handleDragLeave}
            onDrop={e => handleDrop(e, t.id)}
            onDragEnd={handleDragEnd}
            className={`relative border rounded-xl p-4 transition-all
              ${t.status === "connected" ? "bg-zinc-900/50 border-zinc-700/50" :
                t.status === "disabled" ? "bg-zinc-900/20 border-zinc-800/30 opacity-40" :
                "bg-zinc-900/30 border-zinc-800/50 opacity-60"}
              ${dragId === t.id ? "opacity-50 scale-[0.98]" : ""}
              ${dragOverId === t.id && dragId !== t.id ? "border-emerald-500/50 bg-emerald-500/5" : ""}`}>
            {/* Header */}
            <div className="flex items-center justify-between mb-3 flex-wrap gap-2">
              <div className="flex items-center gap-3 flex-wrap">
                <span className="text-zinc-600 hover:text-zinc-400 cursor-grab active:cursor-grabbing select-none" title="Drag to reorder"
                  onMouseDown={() => { dragReadyRef.current = t.id; }}>&#x2630;</span>
                <StatusDot status={t.status === "disabled" ? "stopped" : t.status} />
                <span className="font-mono font-semibold text-zinc-200">{t.id}</span>
                <TypeBadge type={t.type} /> <ModeBadge mode={t.mode} />
                {t.status === "disabled" && <Badge color="gray">DISABLED</Badge>}
              </div>
              <div className="flex items-center gap-2">
                <span className="text-xs text-zinc-500 font-mono">#{t.account}</span>
                {t.leverageByClass && Object.keys(t.leverageByClass).length > 0 ? (
                  <span className="text-xs font-mono text-zinc-600">
                    {Object.entries(t.leverageByClass).map(([cls, lev]) =>
                      `${cls}\u00a01:${lev}`).join(' \u2502 ')}
                  </span>
                ) : t.leverage > 0 ? (
                  <span className="text-xs text-zinc-500 font-mono">1:{t.leverage}</span>
                ) : null}
                {t.status === "connected" && (
                  <button onClick={() => send({ cmd: 'detect_leverage', terminal: t.id })}
                    title="Re-detect per-class leverage"
                    className="text-zinc-600 hover:text-zinc-400 text-xs">&#x21bb;</button>
                )}
                <button onClick={() => send({ cmd: 'toggle_terminal_enabled', terminal: t.id })}
                  title={t.enabled === false ? "Enable terminal" : "Disable terminal"}
                  className={`px-2 py-1 text-xs rounded border ${t.enabled === false
                    ? "border-emerald-500/30 text-emerald-400/70 hover:bg-emerald-500/10"
                    : "border-zinc-600/30 text-zinc-500 hover:bg-zinc-700/30"}`}>
                  {t.enabled === false ? "\u23FB Enable" : "\u23FB"}</button>
                <button onClick={() => { setDeleteConfirm(t.id); setDeleteInput(""); }}
                  title="Delete terminal"
                  className="px-2 py-1 text-xs text-red-400/50 border border-transparent hover:border-red-500/20 rounded hover:bg-red-500/10">
                  &#x2715;</button>
                <button onClick={() => setConfiguring(configuring === t.id ? null : t.id)}
                  className="px-2 py-1 text-xs text-zinc-500 hover:text-zinc-300 border border-transparent hover:border-zinc-700 rounded">Settings</button>
                {(t.guards?.sl3Blocked || t.rCapReached || (Math.abs(t.dailyPnl || 0) > 0)) &&
                  <button onClick={() => { if (confirm('Reset all blocking flags (R-cap, 3SL, Daily DD) for ' + t.name + '?')) { send({ cmd: 'reset_flags', terminal: t.id }); setTimeout(() => send({ cmd: 'get_terminals' }), 300); }}}
                    className="px-2 py-0.5 rounded border text-xs border-amber-500/30 text-amber-400/70 hover:bg-amber-500/10">Reset Flags</button>}
              </div>
            </div>

            {/* Strategy row  -  only when strategies are running on this terminal */}
            {(() => {
              const termStrats = (strategies.running || []).filter(s => s.terminal === t.id);
              if (termStrats.length === 0) return null;
              return (
                <div className="flex items-center gap-2 mb-3 ml-1">
                  {termStrats.map(s => (
                    <button key={s.name} onClick={onGoToStrategies}
                      className="flex items-center gap-1.5 px-2 py-0.5 rounded border border-violet-500/30 bg-violet-500/10 hover:bg-violet-500/15 transition-colors group"
                      title={`Strategy ${s.name}  -  magic ${s.magic}. Click to view strategies.`}>
                      <StatusDot status={s.status || "running"} />
                      <span className="text-xs font-mono text-violet-300 group-hover:text-violet-200">{s.name}</span>
                      <span className="text-xs text-zinc-600 font-mono">#{s.magic}</span>
                    </button>
                  ))}
                </div>
              );
            })()}

            {t.status === "connected" && (<>
              <div className="grid grid-cols-3 gap-4 mb-3">
                <div><div className="text-xs text-zinc-500 mb-1">Balance</div>
                  <div className="font-mono text-sm text-zinc-200">${(t.balance || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</div></div>
                <div><div className="text-xs text-zinc-500 mb-1">Equity</div>
                  <div className="font-mono text-sm text-zinc-200">${(t.equity || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</div></div>
                <div><div className="text-xs text-zinc-500 mb-1">Floating P/L</div>
                  <div className={`font-mono text-sm ${(t.profit || 0) >= 0 ? "text-emerald-400" : "text-red-400"}`}>
                    {(t.profit || 0) >= 0 ? "+" : ""}${(t.profit || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</div></div>
              </div>
              {/* Phase 9.V: Virtual trading data */}
              {t.mode === "virtual" && t.virtualBalance != null && (
                <div className="grid grid-cols-4 gap-3 mb-3 bg-purple-500/5 border border-purple-500/20 rounded-lg p-2.5">
                  <div><div className="text-xs text-purple-400/70 mb-0.5">V.Balance</div>
                    <div className="font-mono text-sm text-purple-300">${(t.virtualBalance || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</div></div>
                  <div><div className="text-xs text-purple-400/70 mb-0.5">V.Equity</div>
                    <div className="font-mono text-sm text-purple-300">${(t.virtualEquity || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</div></div>
                  <div><div className="text-xs text-purple-400/70 mb-0.5">V.Unrealized</div>
                    <div className={`font-mono text-sm ${(t.virtualUnrealized || 0) >= 0 ? "text-emerald-400" : "text-red-400"}`}>
                      {(t.virtualUnrealized || 0) >= 0 ? "+" : ""}${(t.virtualUnrealized || 0).toFixed(2)}</div></div>
                  <div><div className="text-xs text-purple-400/70 mb-0.5">V.Positions</div>
                    <div className="font-mono text-sm text-purple-300">{t.virtualPositions || 0}</div></div>
                </div>
              )}
              <div className="space-y-2 mb-3">
                <div className="flex items-center gap-3"><span className="text-xs text-zinc-500 w-28 shrink-0">Daily DD <span className={`text-[9px] font-bold uppercase ${t.dailyDdMode === 'soft' ? 'text-amber-500' : 'text-red-500'}`}>{t.dailyDdMode || 'hard'}</span>{(t.rCapOn || t.rCapConfigDefault > 0) && <span className={`text-[9px] font-bold uppercase ml-0.5 ${t.rCapReached ? 'text-red-500' : ((t.rCapOn || ((t.rCapLimit ?? 0) >= 0 && t.rCapConfigDefault > 0)) ? 'text-emerald-500' : 'text-zinc-500')}`}>RCap</span>}</span>
                  <ProgressBar value={Math.abs(t.dailyPnl || 0)} max={t.dailyLimit || 5000} />
                  {t.dailyDdSnapshot > 0 && t.dailyDdSnapshot < (t.profile?.dailyDDLimit || t.dailyLimit || 5000) &&
                    <span className="text-[9px] font-bold uppercase text-cyan-500 shrink-0" title={`EOD rollover: ${t.dailyDdPercent || 0}% of equity`}>EOD</span>}
                  <span className="text-xs text-zinc-600 font-mono w-36 text-right shrink-0">${Math.abs(t.dailyPnl || 0).toLocaleString(undefined, { maximumFractionDigits: 0 })} / ${(t.dailyLimit || 5000).toLocaleString()}</span></div>
                <div className="flex items-center gap-3"><span className="text-xs text-zinc-500 w-28 shrink-0">Deposit load</span>
                  <ProgressBar value={t.marginUsed || 0} max={t.maxMargin || 50} />
                  <span className="text-xs text-zinc-600 font-mono w-36 text-right shrink-0">{(t.marginUsed || 0).toFixed(1)}% / {t.maxMargin || 50}%</span></div>
              </div>
              <div className="flex items-center gap-4 text-xs">
                {t.guards && (<>
                  {t.guards.noTradeDesc && (<>
                    <button onClick={() => { send({ cmd: 'toggle_no_trade', terminal: t.id }); send({ cmd: 'get_terminals' }); }}
                      className={`shrink-0 px-2 py-0.5 rounded border text-xs ${t.guards.noTradeOn
                        ? "border-emerald-500/30 text-emerald-400/70 hover:bg-emerald-500/10"
                        : "border-zinc-600/30 text-zinc-500 hover:bg-zinc-700/30"}`}>Hours {t.guards.noTradeOn ? "ON" : "OFF"}</button>
                    <span className={`shrink-0 ${t.guards.noTradeActive ? "text-amber-400" : "text-zinc-500"}`}>
                      {t.guards.noTradeActive ? `\u26d4 NO-TRADE ${t.guards.noTradeDesc}` : t.guards.noTradeDesc}</span>
                  </>)}
                  <button onClick={() => { send({ cmd: 'toggle_3sl_guard', terminal: t.id }); send({ cmd: 'get_terminals' }); }}
                    className={`shrink-0 px-2 py-0.5 rounded border text-xs ${t.guards.sl3
                      ? "border-emerald-500/30 text-emerald-400/70 hover:bg-emerald-500/10"
                      : "border-zinc-600/30 text-zinc-500 hover:bg-zinc-700/30"}`}>3SL {t.guards.sl3 ? "ON" : "OFF"}</button>
                  <span className={`shrink-0 ${t.guards.sl3Blocked ? "text-red-400" : "text-zinc-500"}`}>
                    {t.guards.sl3Count}/3 {t.guards.sl3Blocked ? (
                      <button onClick={() => send({ cmd: 'unblock_3sl', terminal: t.id })}
                        className="ml-1 text-amber-400 underline hover:text-amber-300">UNBLOCK</button>
                    ) : "\u2714"}</span>
                  <button onClick={() => { send({ cmd: 'toggle_news_guard', terminal: t.id }); send({ cmd: 'get_terminals' }); }}
                    className={`shrink-0 px-2 py-0.5 rounded border text-xs ${t.guards.news
                      ? "border-emerald-500/30 text-emerald-400/70 hover:bg-emerald-500/10"
                      : "border-zinc-600/30 text-zinc-500 hover:bg-zinc-700/30"}`}>News {t.guards.news ? "ON" : "OFF"}</button>
                  <span className={t.guards.newsBlock ? "text-amber-400" : t.guards.activeNews ? "text-red-400" : "text-zinc-500"}>
                    {t.guards.newsBlock ? "\ud83d\udfe5 NEWS BLOCK" : t.guards.activeNews ? `\ud83d\udfe5 ${t.guards.activeNews}` : t.guards.nextNews ? `${t.guards.nextNews}` : "--"}</span>
                </>)}
              </div>
            </>)}

            {/* Disabled state */}
            {t.status === "disabled" && t.terminalPath && (
              <div className="flex items-center justify-between">
                <span className="text-xs text-zinc-600 font-mono truncate max-w-xs" title={t.terminalPath}>
                  {t.terminalPath.split(/[\\\/]/).slice(-2).join('/')}</span>
                <span className="text-xs text-zinc-600">Terminal disabled</span>
              </div>
            )}

            {/* Disconnected / Error */}
            {t.status !== "connected" && t.status !== "disabled" && t.terminalPath && (
              <div className="flex items-center justify-between">
                <span className="text-xs text-zinc-600 font-mono truncate max-w-xs" title={t.terminalPath}>
                  {t.terminalPath.split(/[\\\/]/).slice(-2).join('/')}</span>
                {t.status === "connecting" ? (
                  <span className="px-3 py-1.5 text-xs text-amber-400 animate-pulse flex items-center gap-1.5">
                    <StatusDot status="connecting" /> Connecting...</span>
                ) : (
                  <button onClick={() => send({ cmd: 'start_terminal', terminal: t.id })}
                    className="px-3 py-1.5 text-xs bg-blue-600/15 border border-blue-500/30 text-blue-400 rounded hover:bg-blue-600/25 font-medium flex items-center gap-1.5">
                    <span>&#9654;</span> Start Terminal</button>
                )}
              </div>
            )}

            {/* Detail corner button */}
            {t.status === "connected" && (
              <button onClick={() => onOpenDetail(t.id)} title="View positions & stats"
                className="absolute bottom-0 right-0 w-12 h-12 overflow-hidden rounded-br-xl group">
                <svg className="absolute bottom-0 right-0 w-12 h-12" viewBox="0 0 48 48">
                  <polygon points="48,0 48,48 0,48" className="fill-zinc-800/40 group-hover:fill-emerald-500/15 transition-colors duration-200" />
                </svg>
                <svg className="absolute bottom-1.5 right-1.5 w-4 h-4 text-zinc-600 group-hover:text-emerald-400 transition-colors duration-200" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                  <path d="M5 11l6-6M11 11V5H5" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
            )}

            {/* Config Panel */}
            {configuring === t.id && t.profile && (
              <ConfigPanel terminal={t} profile={t.profile}
                onSave={(tid, data) => { send({ cmd: 'save_profile', terminal: tid, profile: data }); setConfiguring(null); }}
                onSetMode={(tid, mode) => send({ cmd: 'set_mode', terminal: tid, mode })} />
            )}
          </div>
        ))}
      </div>

      {/* Delete Confirmation Modal */}
      {deleteConfirm && (
        <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={() => setDeleteConfirm(null)}>
          <div className="bg-zinc-900 border border-red-500/30 rounded-xl p-6 max-w-md" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg text-red-400 font-semibold mb-2">Delete Terminal</h2>
            <p className="text-sm text-zinc-400 mb-2">This will permanently delete <span className="text-zinc-200 font-mono">{deleteConfirm}</span> and all its data:</p>
            <ul className="text-xs text-zinc-500 mb-4 ml-4 space-y-1">
              <li>*  Position history</li><li>*  Events and logs</li><li>*  Sizing configuration</li>
              <li>*  Terminal profile</li><li>*  Strategy state</li>
            </ul>
            <p className="text-xs text-zinc-400 mb-3">Type the terminal name to confirm:</p>
            <input type="text" value={deleteInput} onChange={e => setDeleteInput(e.target.value)}
              placeholder={deleteConfirm} autoFocus
              className="w-full bg-zinc-800 border border-zinc-700 rounded px-3 py-2 text-sm text-zinc-300 font-mono placeholder-zinc-700 mb-4" />
            <div className="flex gap-3">
              <button onClick={() => setDeleteConfirm(null)}
                className="flex-1 px-4 py-2 text-sm bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-300 hover:bg-zinc-700">Cancel</button>
              <button onClick={handleDelete} disabled={deleteInput !== deleteConfirm}
                className="flex-1 px-4 py-2 text-sm bg-red-600/30 border border-red-500/50 rounded-lg text-red-300 hover:bg-red-600/40 font-medium disabled:opacity-30 disabled:cursor-not-allowed">
                DELETE</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
