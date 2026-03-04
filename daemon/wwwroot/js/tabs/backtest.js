// =====================================================================
//  Tab: Backtest / Tester — Data Management (Etap 1)
// =====================================================================

var BacktestTab = ({ terminals, send, connected }) => {
  // ── State ──────────────────────────────────────────────────
  const connectedTerminals = terminals.filter(t => t.status === 'connected');
  const [selTerminal, setSelTerminal] = useState(connectedTerminals[0]?.id || '');
  const [strategies, setStrategies] = useState([]);
  const [assignments, setAssignments] = useState([]);
  const [selStrategy, setSelStrategy] = useState('');

  // Period
  const [fromDate, setFromDate] = useState('2025-01-01');
  const [toDate, setToDate] = useState('2025-12-31');

  // Config
  const [deposit, setDeposit] = useState(100000);
  const [commission, setCommission] = useState(7.0);
  const [rcapOff, setRcapOff] = useState(false);

  // Resolved terminal object (for leverage etc.)
  const selTerminalObj = terminals.find(t => t.id === selTerminal);

  // Data coverage
  const [coverage, setCoverage] = useState(null);
  const [costModel, setCostModel] = useState(null);

  // Download state
  const [downloading, setDownloading] = useState(false);
  const [dlProgress, setDlProgress] = useState(null);
  const [dlComplete, setDlComplete] = useState(null);

  // Backtest run state
  const [btRunning, setBtRunning] = useState(false);
  const [btProgress, setBtProgress] = useState(null);
  const [btComplete, setBtComplete] = useState(null);
  const [btResult, setBtResult] = useState(null);

  // Log
  const [btLogs, setBtLogs] = useState([]);
  const btLogRef = useRef(null);
  const addLog = (text) => setBtLogs(prev => [...prev.slice(-200), { t: Date.now(), text }]);

  // ── Initial load (re-fire when WS connects) ───────────────
  useEffect(() => {
    if (!connected) return;
    send({ cmd: 'bt_get_strategies' });
    send({ cmd: 'bt_get_cost_model' });
  }, [connected]);

  // ── WS listeners ───────────────────────────────────────────
  useEffect(() => {
    const onStrategies = (msg) => {
      setStrategies(msg.strategies || []);
      setAssignments(msg.assignments || []);
      if (!selStrategy && msg.strategies?.length > 0) {
        setSelStrategy(msg.strategies[0].name);
      }
    };
    const onCoverage = (msg) => setCoverage(msg);
    const onCostModel = (msg) => setCostModel(msg);
    const onDlProgress = (msg) => {
      setDlProgress(msg);
      addLog(`Download: ${msg.symbol} (${msg.index}/${msg.total})`);
    };
    const onDlComplete = (msg) => {
      setDownloading(false);
      setDlComplete(msg);
      addLog(`Download complete: ${msg.completed}/${msg.total}` + (msg.failed > 0 ? `, ${msg.failed} failed` : ''));
      if (selStrategy) requestCoverage(selStrategy);
    };

    // Backtest run events
    const onBtProgress = (msg) => {
      setBtProgress(msg);
      // Log milestones at every 10%
      if (msg.processed % Math.max(1, Math.floor(msg.total / 10)) < 500)
        addLog(`Replay: ${msg.processed}/${msg.total} bars (${msg.percent}%)`);
    };
    const onBtComplete = (msg) => {
      setBtRunning(false);
      setBtComplete(msg);
      if (msg.error)
        addLog(`❌ Error: ${msg.error}`);
      else if (msg.cancelled)
        addLog(`⚠ Cancelled. ${msg.total_trades} trades processed.`);
      else
        addLog(`✅ Complete: ${msg.total_trades} trades, R=${msg.total_r}, WR=${msg.win_rate}%, PF=${msg.profit_factor}, ${msg.duration_sec}s`);
      if (!msg.error) send({ cmd: 'bt_get_result' });
    };
    const onBtResult = (msg) => {
      if (!msg.error) {
        setBtResult(msg);
        addLog(`Result loaded: ${msg.trades?.length || 0} trades, ${msg.equity_curve?.length || 0} equity points`);
      }
    };
    const onBtRun = (msg) => {
      if (msg.status === 'started') {
        if (msg.sizing_applied) {
          const disabled = msg.sizing_disabled || [];
          const factors = msg.sizing_factors || {};
          const customFactors = Object.entries(factors).filter(([,v]) => v !== 1.0);
          let sizingInfo = `📐 Sizing applied: ${msg.symbols} symbols active`;
          if (disabled.length > 0)
            sizingInfo += `, ${disabled.length} disabled (${disabled.join(', ')})`;
          if (customFactors.length > 0)
            sizingInfo += `, custom factors: ${customFactors.map(([s,f]) => `${s}=${f}`).join(', ')}`;
          addLog(sizingInfo);
        }
      } else if (msg.error) {
        addLog(`❌ ${msg.error}`);
        setBtRunning(false);
      }
    };

    wsManager.on('cmd:bt_strategies', onStrategies);
    wsManager.on('cmd:bt_data_coverage', onCoverage);
    wsManager.on('cmd:bt_cost_model', onCostModel);
    wsManager.on('cmd:bt_download_progress', onDlProgress);
    wsManager.on('cmd:bt_download_complete', onDlComplete);
    wsManager.on('cmd:bt_progress', onBtProgress);
    wsManager.on('cmd:bt_complete', onBtComplete);
    wsManager.on('cmd:bt_result', onBtResult);
    wsManager.on('cmd:bt_run', onBtRun);

    return () => {
      wsManager.off('cmd:bt_strategies', onStrategies);
      wsManager.off('cmd:bt_data_coverage', onCoverage);
      wsManager.off('cmd:bt_cost_model', onCostModel);
      wsManager.off('cmd:bt_download_progress', onDlProgress);
      wsManager.off('cmd:bt_download_complete', onDlComplete);
      wsManager.off('cmd:bt_progress', onBtProgress);
      wsManager.off('cmd:bt_complete', onBtComplete);
      wsManager.off('cmd:bt_result', onBtResult);
      wsManager.off('cmd:bt_run', onBtRun);
    };
  }, [selStrategy]);

  // ── Auto-fill from assignment ──────────────────────────────
  useEffect(() => {
    if (!selStrategy) return;
    const assignment = assignments.find(a => a.strategy === selStrategy);
    if (assignment) {
      setSelTerminal(assignment.terminal);
    }
    requestCoverage(selStrategy);
  }, [selStrategy, fromDate, toDate]);

  // ── Helpers ────────────────────────────────────────────────
  const getStrategyReq = () => {
    const strat = strategies.find(s => s.name === selStrategy);
    return strat?.requirements || null;
  };

  const dateToTs = (dateStr) => Math.floor(new Date(dateStr + 'T00:00:00Z').getTime() / 1000);

  const requestCoverage = (stratName) => {
    const strat = strategies.find(s => s.name === stratName);
    if (!strat?.requirements?.symbols) return;

    // Determine main timeframe
    const tfs = strat.requirements.timeframes || {};
    const mainTf = Object.values(tfs)[0] || 'M30';

    send({
      cmd: 'bt_get_data_coverage',
      symbols: strat.requirements.symbols,
      timeframe: mainTf,
      from_ts: dateToTs(fromDate),
      to_ts: dateToTs(toDate),
      terminal: selTerminal
    });
  };

  const handleDownload = () => {
    const req = getStrategyReq();
    if (!req || !selTerminal) return;

    // Download missing symbols
    const missing = coverage?.symbols?.filter(s => !s.fully_covered).map(s => s.symbol) || req.symbols;
    const tfs = req.timeframes || {};
    const mainTf = Object.values(tfs)[0] || 'M30';

    setDownloading(true);
    setDlComplete(null);
    setDlProgress(null);

    send({
      cmd: 'bt_download_bars',
      terminal: selTerminal,
      timeframe: mainTf,
      from_ts: dateToTs(fromDate),
      to_ts: dateToTs(toDate),
      symbols: missing.length > 0 ? missing : req.symbols
    });
  };

  const handleCancelDownload = () => {
    send({ cmd: 'bt_cancel_download' });
  };

  const handleRunBacktest = () => {
    if (!selStrategy || !selTerminal) return;
    const req = getStrategyReq();
    if (!req) return;

    setBtRunning(true);
    setBtProgress(null);
    setBtComplete(null);
    setBtResult(null);
    addLog(`▶ Starting ${selStrategy} on ${selTerminal}: ${allSymbols.length} symbols, ${fromDate} → ${toDate}, deposit=$${deposit}${rcapOff ? ', R-cap OFF' : ''}`);

    send({
      cmd: 'bt_run',
      strategy: selStrategy,
      terminal: selTerminal,
      from_ts: dateToTs(fromDate),
      to_ts: dateToTs(toDate),
      deposit,
      commission,
      timeframe: mainTf,
      rcap_off: rcapOff,
    });
  };

  const handleCancelBacktest = () => {
    addLog('⏹ Cancelling...');
    send({ cmd: 'bt_cancel' });
  };

  const exportCsv = () => {
    if (!btResult) return;
    const sep = ';';
    const trades = btResult.trades || [];
    const blocked = btResult.blocked_list || [];

    // Gate descriptions for meta
    const GATE_DESC = {
      'LOT_CALC':       'Lot calculator rejected (lot=0, insufficient risk money, or card error)',
      'G11_SAME_SYMBOL':'Netting gate — already has open position on this symbol for this strategy',
      'G12_RCAP':       'Daily R-cap exceeded — sum of closed R for today >= -r_cap limit',
      'G0_PAUSE':       'Global pause active — daemon paused manually or by schedule',
      'G1_DISABLED':    'Symbol disabled in Sizing tab',
      'G2_DD':          'Daily drawdown limit reached',
      'G3_MARGIN':      'Insufficient margin',
      'G5_HOURS':       'Outside allowed trading hours',
      'G7_NEWS':        'High-impact news window',
    };

    // Unified header: all signals (filled + blocked)
    const hdr = [
      'n','status','symbol','direction',
      'bar_time','bar_dt',
      'entry_price','sl','tp',
      'volume','price_open','price_close',
      'open_time','close_time','open_dt','close_dt',
      'reason','pnl_raw','cost','commission','pnl','r_result','cost_r',
      'balance_after',
      'gate','gate_reason',
      'strategy','magic','signal_data'
    ].join(sep);

    const tsFmt = (ts) => ts ? new Date(ts * 1000).toISOString().replace('T',' ').slice(0,19) : '';

    // Build unified rows: trades as FILLED, blocked as BLOCKED
    const filledRows = trades.map(t => ({
      sortTime: t.open_time,
      status: 'FILLED',
      symbol: t.symbol, dir: t.dir,
      bar_time: t.open_time, bar_dt: tsFmt(t.open_time),
      entry_price: t.price_open, sl: t.sl, tp: t.tp,
      volume: t.volume, price_open: t.price_open, price_close: t.price_close,
      open_time: t.open_time, close_time: t.close_time,
      open_dt: tsFmt(t.open_time), close_dt: tsFmt(t.close_time),
      reason: t.reason,
      pnl_raw: t.pnl_raw ?? '', cost: t.cost ?? '',
      commission: t.commission_dollar ?? '', pnl: t.pnl, r: t.r, cost_r: t.cost_r ?? '',
      balance: t.balance,
      gate: '', gate_reason: '',
      strategy: t.strategy || btResult.strategy, magic: t.magic ?? '',
      signal_data: (t.signal_data || '').replace(/;/g, ',')
    }));

    const blockedRows = blocked.map(b => ({
      sortTime: b.bar_time,
      status: 'BLOCKED',
      symbol: b.symbol, dir: b.dir,
      bar_time: b.bar_time, bar_dt: tsFmt(b.bar_time),
      entry_price: b.entry_price, sl: b.sl, tp: b.tp,
      volume: '', price_open: '', price_close: '',
      open_time: '', close_time: '', open_dt: '', close_dt: '',
      reason: '',
      pnl_raw: '', cost: '', commission: '', pnl: '', r: '', cost_r: '',
      balance: '',
      gate: b.gate, gate_reason: (b.reason || '').replace(/;/g, ','),
      strategy: btResult.strategy, magic: '', signal_data: ''
    }));

    // Merge + sort by time
    const allRows = [...filledRows, ...blockedRows].sort((a, b) => a.sortTime - b.sortTime);

    const csvRows = allRows.map((r, i) => [
      i + 1, r.status, r.symbol, r.dir,
      r.bar_time, r.bar_dt,
      r.entry_price, r.sl, r.tp,
      r.volume, r.price_open, r.price_close,
      r.open_time, r.close_time, r.open_dt, r.close_dt,
      r.reason, r.pnl_raw, r.cost, r.commission, r.pnl, r.r, r.cost_r,
      r.balance,
      r.gate, r.gate_reason,
      r.strategy, r.magic, r.signal_data
    ].join(sep));

    // Meta
    const fromStr = btResult.from_ts ? new Date(btResult.from_ts * 1000).toISOString().slice(0,10) : '';
    const toStr = btResult.to_ts ? new Date(btResult.to_ts * 1000).toISOString().slice(0,10) : '';
    const meta = [
      `# strategy=${btResult.strategy}`,
      `# terminal=${btResult.terminal}`,
      `# period=${fromStr} to ${toStr}`,
      `# timeframe=${btResult.timeframe}`,
      `# deposit=${btResult.initial_balance}`,
      `# bar_time_zone=broker_server (EET for The5ers)`,
      `# rcap=${rcapOff ? 'OFF' : (btResult.r_cap ?? 'from_config')}`,
      `#`,
      `# total_r=${btResult.total_r}`,
      `# max_dd_r=${btResult.max_dd_r}`,
      `# calmar_r=${btResult.calmar_r}`,
      `# best_day_r=${btResult.best_day_r}`,
      `# worst_day_r=${btResult.worst_day_r}`,
      `# win_rate=${btResult.win_rate}%`,
      `# profit_factor=${btResult.profit_factor}`,
      `# total_trades=${trades.length}`,
      `# blocked_signals=${blocked.length}`,
      `# total_signals=${allRows.length}`,
      `#`,
      `# === GATE DESCRIPTIONS ===`,
      ...Object.entries(btResult.gate_stats || {}).map(([g, cnt]) =>
        `# ${g} (${cnt}x): ${GATE_DESC[g] || 'Unknown gate'}`
      ),
      `#`,
    ];

    const csv = [...meta, hdr, ...csvRows].join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=ascii' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `bt_${btResult.strategy}_${fromStr}_${toStr}.csv`;
    a.click();
    URL.revokeObjectURL(url);
    addLog(`📁 CSV exported: ${trades.length} trades + ${blocked.length} blocked = ${allRows.length} signals`);
  };

  // Auto-scroll log
  useEffect(() => {
    if (btLogRef.current) btLogRef.current.scrollTop = btLogRef.current.scrollHeight;
  }, [btLogs]);

  // ── Cost lookup ────────────────────────────────────────────
  const getCost = (symbol) => {
    if (!costModel?.symbols) return null;
    return costModel.symbols.find(s => s.symbol === symbol);
  };

  // ── Strategy requirements display ──────────────────────────
  const req = getStrategyReq();
  const allSymbols = req?.symbols || [];
  const tfs = req?.timeframes || {};
  const mainTf = Object.values(tfs)[0] || 'M30';

  // Lock controls during run
  const locked = btRunning || downloading;

  // ── Render ─────────────────────────────────────────────────
  return (
    <div className="flex flex-col" style={{ height: 'calc(100vh - 110px)' }}>

      {/* ── Configuration (sticky) ──────────────────────────── */}
      <div className="flex-shrink-0 bg-zinc-900 border border-zinc-800 rounded-lg p-4 mb-4">
        <h3 className="text-sm font-medium text-zinc-300 mb-3">Configuration</h3>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          {/* Terminal */}
          <label className="text-xs text-zinc-500">
            Terminal
            <select value={selTerminal} onChange={e => setSelTerminal(e.target.value)} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50">
              {connectedTerminals.map(t => <option key={t.id} value={t.id}>{t.id}</option>)}
              {connectedTerminals.length === 0 && <option value="">No terminals connected</option>}
            </select>
          </label>

          {/* Strategy */}
          <label className="text-xs text-zinc-500">
            Strategy
            <select value={selStrategy} onChange={e => setSelStrategy(e.target.value)} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50">
              {strategies.map(s => <option key={s.name} value={s.name}>{s.name}</option>)}
            </select>
          </label>

          {/* Period */}
          <label className="text-xs text-zinc-500">
            From
            <input type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50" />
          </label>
          <label className="text-xs text-zinc-500">
            To
            <input type="date" value={toDate} onChange={e => setToDate(e.target.value)} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50" />
          </label>
        </div>

        {/* Row 2: deposit, commission (leverage from terminal) */}
        <div className="grid grid-cols-4 gap-3 mt-3">
          <label className="text-xs text-zinc-500">
            Deposit ($)
            <input type="number" value={deposit} onChange={e => setDeposit(Number(e.target.value))} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50" />
          </label>
          <label className="text-xs text-zinc-500">
            Leverage
            <div className="mt-1 w-full bg-zinc-800/50 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-400">
              1:{selTerminalObj?.leverage || 100}
            </div>
          </label>
          <label className="text-xs text-zinc-500">
            Commission ($/lot RT)
            <input type="number" step="0.5" value={commission} onChange={e => setCommission(Number(e.target.value))} disabled={locked}
              className="mt-1 w-full bg-zinc-800 border border-zinc-700 rounded px-2 py-1.5 text-sm text-zinc-200 disabled:opacity-50" />
          </label>
          <label className="text-xs text-zinc-500">
            R-cap (G12)
            <div className="mt-1 flex items-center gap-2 h-[30px]">
              <button onClick={() => setRcapOff(!rcapOff)} disabled={locked}
                className={`px-3 py-1 text-xs rounded disabled:opacity-50 ${rcapOff ? 'bg-amber-600 text-white' : 'bg-zinc-700 text-zinc-300'}`}>
                {rcapOff ? 'OFF' : (req?.r_cap != null ? req.r_cap + 'R' : 'N/A')}
              </button>
              {rcapOff && <span className="text-xs text-amber-400">disabled</span>}
            </div>
          </label>
        </div>

        {/* Strategy info */}
        {req && (
          <div className="mt-3 text-xs text-zinc-500">
            <span className="text-zinc-400">{selStrategy}</span>
            {' — '}{allSymbols.length} symbols, {mainTf}, history: {req.history_bars} bars
            {req.r_cap != null && <>, R-cap: {req.r_cap}</>}
          </div>
        )}
      </div>

      {/* ── Scrollable area ────────────────────────────────── */}
      <div className="flex-1 overflow-y-auto space-y-4 min-h-0 pr-1">

      {/* ── Data Coverage ──────────────────────────────────── */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-lg p-4">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-sm font-medium text-zinc-300">
            Data Coverage
            {coverage?.symbols && (() => {
              const allFull = coverage.symbols.every(s => s.fully_covered);
              return (
                <span className={`ml-2 text-xs font-normal ${allFull ? 'text-emerald-400' : 'text-amber-400'}`}>
                  {allFull ? '100%' : `${coverage.symbols.filter(s => s.fully_covered).length}/${coverage.symbols.length} symbols`}
                </span>
              );
            })()}
            {coverage?.db_size_mb > 0 && (
              <span className="ml-2 text-xs font-normal text-zinc-600">{coverage.db_size_mb} MB</span>
            )}
          </h3>
          <div className="flex gap-2">
            {!downloading ? (
              <button onClick={handleDownload} disabled={!selTerminal || allSymbols.length === 0 || locked}
                className="px-3 py-1 text-xs bg-blue-600 hover:bg-blue-500 disabled:bg-zinc-700 disabled:text-zinc-500 text-white rounded">
                {coverage?.symbols?.every(s => s.fully_covered) ? 'Re-download All' : 'Download Missing'}
              </button>
            ) : (
              <button onClick={handleCancelDownload}
                className="px-3 py-1 text-xs bg-red-600 hover:bg-red-500 text-white rounded">
                Cancel
              </button>
            )}
            <button onClick={() => requestCoverage(selStrategy)}
              className="px-3 py-1 text-xs bg-zinc-700 hover:bg-zinc-600 text-zinc-300 rounded">
              Refresh
            </button>
          </div>
        </div>

        {/* Download progress bar */}
        {downloading && dlProgress && (
          <div className="mb-3">
            <div className="flex items-center justify-between text-xs text-zinc-400 mb-1">
              <span>Downloading: {dlProgress.symbol}</span>
              <span>{dlProgress.index}/{dlProgress.total} ({dlProgress.percent}%)</span>
            </div>
            <div className="w-full bg-zinc-800 rounded-full h-1.5">
              <div className="bg-blue-500 h-1.5 rounded-full transition-all duration-300"
                style={{ width: `${dlProgress.percent}%` }}></div>
            </div>
          </div>
        )}

        {/* Download complete message */}
        {dlComplete && !downloading && (
          <div className={`mb-3 text-xs px-3 py-2 rounded ${dlComplete.failed > 0 ? 'bg-amber-900/30 text-amber-400' : 'bg-emerald-900/30 text-emerald-400'}`}>
            Download complete: {dlComplete.completed}/{dlComplete.total} symbols
            {dlComplete.failed > 0 && <>, {dlComplete.failed} failed</>}
            {dlComplete.cancelled && <> (cancelled)</>}
          </div>
        )}

        {/* Symbol coverage table */}
        {allSymbols.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-xs">
              <thead>
                <tr className="text-zinc-500 border-b border-zinc-800">
                  <th className="text-left py-1 pr-3">Symbol</th>
                  <th className="text-left py-1 pr-3">TF</th>
                  <th className="text-left py-1 pr-3 w-40">Coverage</th>
                  <th className="text-right py-1 pr-3">Bars</th>
                  <th className="text-right py-1 pr-3">Spread</th>
                  <th className="text-right py-1 pr-3">Slip</th>
                  <th className="text-right py-1">Total</th>
                </tr>
              </thead>
              <tbody>
                {allSymbols.map(sym => {
                  const cov = coverage?.symbols?.find(s => s.symbol === sym);
                  const cost = getCost(sym);
                  const tf = tfs[sym] || mainTf;
                  const reqFrom = dateToTs(fromDate);
                  const reqTo = dateToTs(toDate);
                  const reqRange = reqTo - reqFrom;
                  let pct = 0;
                  if (cov?.fully_covered) {
                    pct = 100;
                  } else if (cov?.has_data && cov.from && cov.to && reqRange > 0) {
                    const overlapStart = Math.max(cov.from, reqFrom);
                    const overlapEnd = Math.min(cov.to, reqTo);
                    const overlap = Math.max(0, overlapEnd - overlapStart);
                    pct = Math.round(overlap / reqRange * 100);
                  }

                  return (
                    <tr key={sym} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                      <td className="py-1.5 pr-3 text-zinc-300 font-mono">{sym}</td>
                      <td className="py-1.5 pr-3 text-zinc-500">{tf}</td>
                      <td className="py-1.5 pr-3">
                        <div className="flex items-center gap-2">
                          <div className="flex-1 bg-zinc-800 rounded-full h-1.5">
                            <div className={`h-1.5 rounded-full ${pct >= 100 ? 'bg-emerald-500' : pct > 0 ? 'bg-amber-500' : 'bg-zinc-700'}`}
                              style={{ width: `${pct}%` }}></div>
                          </div>
                          <span className={`text-xs min-w-[35px] text-right ${pct >= 100 ? 'text-emerald-400' : pct > 0 ? 'text-amber-400' : 'text-zinc-600'}`}>
                            {pct}%
                          </span>
                        </div>
                      </td>
                      <td className="py-1.5 pr-3 text-right text-zinc-400">
                        {cov?.bar_count ? cov.bar_count.toLocaleString() : '—'}
                      </td>
                      <td className="py-1.5 pr-3 text-right text-zinc-500">
                        {cost ? cost.spread : '—'}
                      </td>
                      <td className="py-1.5 pr-3 text-right text-zinc-500">
                        {cost ? cost.slippage : '—'}
                      </td>
                      <td className="py-1.5 text-right text-zinc-400">
                        {cost ? <>{cost.total} <span className="text-zinc-600">{cost.unit}</span></> : '—'}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {allSymbols.length === 0 && !selStrategy && (
          <div className="text-xs text-zinc-600 py-4 text-center">Select a strategy to see data requirements</div>
        )}

        {allSymbols.length === 0 && selStrategy && (
          <div className="text-xs text-zinc-600 py-4 text-center">Strategy has no config or no symbols defined</div>
        )}
      </div>

      {/* ── Run Backtest ─────────────────────────────── */}
      <div className="bg-zinc-900 border border-zinc-800 rounded-lg p-4">
        <div className="flex items-center gap-3">
          {!btRunning ? (
            <button onClick={handleRunBacktest}
              disabled={!selTerminal || allSymbols.length === 0 || btRunning}
              className="px-4 py-2 text-sm bg-emerald-600 hover:bg-emerald-500 disabled:bg-zinc-700 disabled:text-zinc-500 text-white rounded flex items-center gap-2">
              <span>▶</span> Run Backtest
            </button>
          ) : (
            <button onClick={handleCancelBacktest}
              className="px-4 py-2 text-sm bg-red-600 hover:bg-red-500 text-white rounded flex items-center gap-2">
              <span>■</span> Cancel
            </button>
          )}

          {btRunning && btProgress && (
            <div className="flex-1">
              <div className="flex items-center justify-between text-xs text-zinc-400 mb-1">
                <span>Bar {btProgress.processed}/{btProgress.total}</span>
                <span>{btProgress.percent}%</span>
              </div>
              <div className="w-full bg-zinc-800 rounded-full h-1.5">
                <div className="bg-emerald-500 h-1.5 rounded-full transition-all duration-300"
                  style={{ width: `${btProgress.percent}%` }}></div>
              </div>
            </div>
          )}

          {!btRunning && btComplete && !btComplete.error && (
            <>
              <span className="text-xs text-zinc-400">
                {btComplete.total_trades} trades, R={btComplete.total_r}, WR={btComplete.win_rate}%, PF={btComplete.profit_factor} — {btComplete.duration_sec}s
              </span>
              <button onClick={exportCsv} disabled={!btResult || (!btResult.trades?.length && !btResult.blocked_list?.length)}
                className="px-3 py-1.5 text-xs bg-zinc-700 hover:bg-zinc-600 disabled:opacity-40 text-zinc-300 rounded flex items-center gap-1">
                💾 Save CSV
              </button>
            </>
          )}
          {btComplete?.error && (
            <span className="text-xs text-red-400">{btComplete.error}</span>
          )}
        </div>
      </div>

      {/* ── Results ────────────────────────────────────── */}
      {btResult && (
        <div className="space-y-4">
          {/* Summary cards */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <ResultCard label="Total R" value={btResult.total_r} fmt="r" />
            <ResultCard label="Win Rate" value={btResult.win_rate} fmt="pct" />
            <ResultCard label="Profit Factor" value={btResult.profit_factor} />
            <ResultCard label="Trades" value={btResult.total_trades} />
            <ResultCard label="Max DD (R)" value={btResult.max_dd_r} fmt="r" neg />
            <ResultCard label="Calmar R" value={btResult.calmar_r} />
            <ResultCard label="Best Day (R)" value={btResult.best_day_r} fmt="r" />
            <ResultCard label="Worst Day (R)" value={btResult.worst_day_r} fmt="r" />
          </div>

          {/* Dollar metrics */}
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <ResultCard label="Final Balance" value={btResult.final_balance} fmt="$" />
            <ResultCard label="Total P&L" value={btResult.total_pnl} fmt="$" />
            <ResultCard label="Max DD ($)" value={btResult.max_dd_dollar} fmt="$" neg />
            <ResultCard label="Commission" value={btResult.total_commission} fmt="$" neg />
          </div>

          {/* Per-symbol R breakdown */}
          {btResult.per_symbol_r && Object.keys(btResult.per_symbol_r).length > 0 && (
            <div className="bg-zinc-900 border border-zinc-800 rounded-lg p-4">
              <h3 className="text-sm font-medium text-zinc-300 mb-3">Per-Symbol R</h3>
              <div className="flex flex-wrap gap-2">
                {Object.entries(btResult.per_symbol_r)
                  .sort(([,a],[,b]) => b - a)
                  .map(([sym, r]) => (
                    <div key={sym} className={`px-2 py-1 rounded text-xs font-mono ${r >= 0 ? 'bg-emerald-900/30 text-emerald-400' : 'bg-red-900/30 text-red-400'}`}>
                      {sym} {r >= 0 ? '+' : ''}{r}R
                    </div>
                  ))
                }
              </div>
            </div>
          )}

          {/* Gate stats */}
          {btResult.gate_stats && Object.keys(btResult.gate_stats).length > 0 && (
            <div className="bg-zinc-900 border border-zinc-800 rounded-lg p-4">
              <h3 className="text-sm font-medium text-zinc-300 mb-3">
                Blocked Signals ({btResult.blocked_signals})
              </h3>
              <div className="flex flex-wrap gap-2">
                {Object.entries(btResult.gate_stats).map(([gate, count]) => (
                  <div key={gate} className="px-2 py-1 rounded text-xs bg-amber-900/30 text-amber-400">
                    {gate}: {count}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Trade list */}
          {btResult.trades?.length > 0 && (
            <div className="bg-zinc-900 border border-zinc-800 rounded-lg p-4">
              <h3 className="text-sm font-medium text-zinc-300 mb-3">
                Trades ({btResult.trades.length})
              </h3>
              <div className="overflow-x-auto max-h-80 overflow-y-auto">
                <table className="w-full text-xs">
                  <thead className="sticky top-0 bg-zinc-900">
                    <tr className="text-zinc-500 border-b border-zinc-800">
                      <th className="text-left py-1 pr-2">#</th>
                      <th className="text-left py-1 pr-2">Symbol</th>
                      <th className="text-left py-1 pr-2">Dir</th>
                      <th className="text-right py-1 pr-2">Lot</th>
                      <th className="text-right py-1 pr-2">Open</th>
                      <th className="text-right py-1 pr-2">Close</th>
                      <th className="text-left py-1 pr-2">Reason</th>
                      <th className="text-right py-1 pr-2">P&L</th>
                      <th className="text-right py-1">R</th>
                    </tr>
                  </thead>
                  <tbody>
                    {btResult.trades.map((t, i) => (
                      <tr key={i} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                        <td className="py-1 pr-2 text-zinc-600">{i + 1}</td>
                        <td className="py-1 pr-2 text-zinc-300 font-mono">{t.symbol}</td>
                        <td className={`py-1 pr-2 ${t.dir === 'BUY' ? 'text-emerald-400' : 'text-red-400'}`}>{t.dir}</td>
                        <td className="py-1 pr-2 text-right text-zinc-400">{t.volume}</td>
                        <td className="py-1 pr-2 text-right text-zinc-500">{t.price_open}</td>
                        <td className="py-1 pr-2 text-right text-zinc-500">{t.price_close}</td>
                        <td className={`py-1 pr-2 ${t.reason === 'SL' ? 'text-red-400' : t.reason === 'TP' ? 'text-emerald-400' : 'text-zinc-400'}`}>{t.reason}</td>
                        <td className={`py-1 pr-2 text-right ${t.pnl >= 0 ? 'text-emerald-400' : 'text-red-400'}`}>${t.pnl}</td>
                        <td className={`py-1 text-right ${t.r >= 0 ? 'text-emerald-400' : 'text-red-400'}`}>{t.r >= 0 ? '+' : ''}{t.r}R</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}

      </div>{/* end scrollable area */}

      {/* ── Log panel (sticky bottom) ──────────────────────── */}
      <div className="flex-shrink-0 mt-2 bg-zinc-950 border border-zinc-800 rounded-lg">
        <div className="flex items-center justify-between px-3 py-1.5 border-b border-zinc-800">
          <span className="text-xs text-zinc-500">Log ({btLogs.length})</span>
          <button onClick={() => setBtLogs([])} className="text-xs text-zinc-600 hover:text-zinc-400">Clear</button>
        </div>
        <div ref={btLogRef} className="overflow-y-auto font-mono text-xs text-zinc-500 px-3 py-1" style={{ maxHeight: '100px' }}>
          {btLogs.length === 0 && <div className="text-zinc-700 py-1">No log entries</div>}
          {btLogs.map((l, i) => (
            <div key={i} className="py-0.5 leading-tight">
              <span className="text-zinc-700">{new Date(l.t).toLocaleTimeString()}</span>{' '}
              <span className={l.text.startsWith('❌') ? 'text-red-400' : l.text.startsWith('✅') ? 'text-emerald-400' : ''}>{l.text}</span>
            </div>
          ))}
        </div>
      </div>

    </div>
  );
};

// ── Result card helper ──────────────────────────────────
var ResultCard = ({ label, value, fmt, neg }) => {
  let display = value;
  let color = 'text-zinc-200';

  if (fmt === '$') {
    display = '$' + (typeof value === 'number' ? value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : value);
    if (neg) color = 'text-red-400';
    else color = value >= 0 ? 'text-emerald-400' : 'text-red-400';
  } else if (fmt === 'r') {
    display = (value >= 0 ? '+' : '') + value + 'R';
    if (neg) color = 'text-red-400';
    else color = value >= 0 ? 'text-emerald-400' : 'text-red-400';
  } else if (fmt === 'pct') {
    display = value + '%';
    color = value >= 50 ? 'text-emerald-400' : 'text-amber-400';
  }

  return (
    <div className="bg-zinc-800/50 border border-zinc-700/50 rounded p-3">
      <div className="text-xs text-zinc-500 mb-1">{label}</div>
      <div className={`text-lg font-medium ${color}`}>{display}</div>
    </div>
  );
};
