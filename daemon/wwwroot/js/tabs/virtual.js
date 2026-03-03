// =====================================================================
//  Tab: Virtual Trading
// =====================================================================

var VirtualTab = ({ terminals, send, onTradeChart }) => {
  const virtualTerminals = terminals.filter(t => t.mode === "virtual" && t.status === "connected");
  const [sel, setSel] = useState(virtualTerminals[0]?.id || "");
  const [equityData, setEquityData] = useState(null);
  const [stats, setStats] = useState(null);
  const chartRef = useRef(null);
  const chartContainerRef = useRef(null);

  useEffect(() => {
    if (virtualTerminals.length > 0 && !sel) setSel(virtualTerminals[0].id);
  }, [virtualTerminals]);

  useEffect(() => {
    if (!sel) return;
    send({ cmd: 'get_virtual_equity', terminal: sel });
    send({ cmd: 'get_virtual_stats', terminal: sel });

    const onEquity = (msg) => { if (msg.terminal === sel) setEquityData(msg); };
    const onStats = (msg) => { if (msg.terminal === sel) setStats(msg.stats); };
    wsManager.on('cmd:virtual_equity', onEquity);
    wsManager.on('cmd:virtual_stats', onStats);
    return () => { wsManager.off('cmd:virtual_equity', onEquity); wsManager.off('cmd:virtual_stats', onStats); };
  }, [sel]);

  // Lightweight Charts equity curve
  useEffect(() => {
    if (!equityData?.data?.length || !chartContainerRef.current) return;
    if (chartRef.current) { chartRef.current.remove(); chartRef.current = null; }

    const chart = LightweightCharts.createChart(chartContainerRef.current, {
      width: chartContainerRef.current.clientWidth, height: 300,
      layout: { background: { color: '#09090b' }, textColor: '#71717a' },
      grid: { vertLines: { color: '#27272a' }, horzLines: { color: '#27272a' } },
      timeScale: { timeVisible: true, secondsVisible: false },
      rightPriceScale: { borderColor: '#3f3f46' },
      crosshair: { mode: 0 },
    });
    chartRef.current = chart;

    const lineSeries = chart.addLineSeries({
      color: '#a855f7', lineWidth: 2, priceLineVisible: false,
    });

    const points = equityData.data.map(d => ({
      time: Math.floor(new Date(d.time + 'Z').getTime() / 1000),
      value: d.equity
    })).filter(p => !isNaN(p.time)).sort((a, b) => a.time - b.time);

    if (points.length > 0) lineSeries.setData(points);

    // Trade markers
    if (equityData.trades?.length > 0) {
      const markers = equityData.trades
        .map(t => ({
          time: Math.floor(new Date(t.time + 'Z').getTime() / 1000),
          position: t.pnl >= 0 ? 'belowBar' : 'aboveBar',
          color: t.pnl >= 0 ? '#22c55e' : '#ef4444',
          shape: t.pnl >= 0 ? 'arrowUp' : 'arrowDown',
          text: `${t.symbol} ${t.pnl >= 0 ? '+' : ''}$${t.pnl.toFixed(0)}`,
        }))
        .filter(m => !isNaN(m.time))
        .sort((a, b) => a.time - b.time);
      if (markers.length > 0) lineSeries.setMarkers(markers);
    }

    chart.timeScale().fitContent();

    const ro = new ResizeObserver(() => {
      if (chartContainerRef.current) chart.applyOptions({ width: chartContainerRef.current.clientWidth });
    });
    ro.observe(chartContainerRef.current);
    return () => { ro.disconnect(); chart.remove(); chartRef.current = null; };
  }, [equityData]);

  const handleReset = () => {
    if (!confirm(`Reset ALL virtual trading data for ${sel}? This cannot be undone.`)) return;
    send({ cmd: 'reset_virtual', terminal: sel });
    const onReset = (msg) => {
      if (msg.ok) { send({ cmd: 'get_virtual_equity', terminal: sel }); send({ cmd: 'get_virtual_stats', terminal: sel }); send({ cmd: 'get_terminals' }); }
      wsManager.off('cmd:reset_virtual', onReset);
    };
    wsManager.on('cmd:reset_virtual', onReset);
  };

  const handleExport = () => {
    send({ cmd: 'export_virtual_csv', terminal: sel });
    const onCsv = (msg) => {
      if (msg.terminal === sel && msg.csv) {
        const blob = new Blob([msg.csv], { type: 'text/csv' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a'); a.href = url; a.download = `virtual_trades_${sel}.csv`; a.click();
        URL.revokeObjectURL(url);
      }
      wsManager.off('cmd:virtual_csv', onCsv);
    };
    wsManager.on('cmd:virtual_csv', onCsv);
  };

  if (virtualTerminals.length === 0) {
    return (<div className="text-center text-zinc-600 py-16">
      <div className="text-2xl mb-2">No virtual terminals</div>
      <div className="text-sm">Set a terminal to Virtual mode in Settings to start virtual trading.</div>
    </div>);
  }

  const fmt = (v, d=2) => v != null ? (v >= 0 ? '+' : '') + '$' + v.toFixed(d) : '--';
  const fmtPct = (v) => v != null ? v.toFixed(1) + '%' : '--';

  return (<div className="space-y-4">
    {/* Terminal selector */}
    <div className="flex items-center gap-3">
      <select value={sel} onChange={e => setSel(e.target.value)}
        className="bg-zinc-800 border border-zinc-700 rounded px-3 py-1.5 text-sm text-zinc-200">
        {virtualTerminals.map(t => <option key={t.id} value={t.id}>{t.id}</option>)}
      </select>
      <button onClick={() => { send({ cmd: 'get_virtual_equity', terminal: sel }); send({ cmd: 'get_virtual_stats', terminal: sel }); }}
        className="px-2 py-1.5 text-xs text-zinc-500 hover:text-zinc-300 border border-zinc-700 rounded">&#x21bb; Refresh</button>
      <div className="flex-1" />
      <button onClick={handleExport}
        className="px-3 py-1.5 text-xs text-blue-400 border border-blue-500/30 rounded hover:bg-blue-500/10">Export CSV</button>
      <button onClick={handleReset}
        className="px-3 py-1.5 text-xs text-red-400 border border-red-500/30 rounded hover:bg-red-500/10">Reset Virtual</button>
    </div>

    {/* Equity Chart */}
    <div className="border border-zinc-800 rounded-lg p-4">
      <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Equity Curve</h3>
      <div ref={chartContainerRef} className="w-full" style={{ minHeight: 300 }} />
      {(!equityData || !equityData.data?.length) && (
        <div className="text-center text-zinc-600 py-12">No equity data yet. Virtual trading will generate data points every 5 minutes.</div>
      )}
    </div>

    {/* Statistics */}
    {stats && stats.totalTrades > 0 && (
      <div className="border border-zinc-800 rounded-lg p-4">
        <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Virtual Statistics</h3>
        <div className="grid grid-cols-4 gap-4">
          <div><div className="text-xs text-zinc-500 mb-1">Total Trades</div><div className="font-mono text-lg text-zinc-200">{stats.totalTrades}</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Win Rate</div><div className="font-mono text-lg text-zinc-200">{fmtPct(stats.winRate)}</div>
            <div className="text-xs text-zinc-600">{stats.wins}W / {stats.losses}L</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Net P&L</div>
            <div className={`font-mono text-lg ${stats.netPnl >= 0 ? "text-emerald-400" : "text-red-400"}`}>{fmt(stats.netPnl)}</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Profit Factor</div>
            <div className="font-mono text-lg text-zinc-200">{stats.profitFactor > 100 ? "\u221e" : stats.profitFactor.toFixed(2)}</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Avg Win</div><div className="font-mono text-sm text-emerald-400">{fmt(stats.avgWin)}</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Avg Loss</div><div className="font-mono text-sm text-red-400">{fmt(stats.avgLoss)}</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Max Drawdown</div>
            <div className="font-mono text-sm text-red-400">{stats.maxDrawdown > 0 ? `-$${stats.maxDrawdown.toFixed(2)}` : '$0.00'} ({stats.maxDrawdownPct > 0 ? `-${stats.maxDrawdownPct.toFixed(1)}%` : '0.0%'})</div></div>
          <div><div className="text-xs text-zinc-500 mb-1">Expectancy</div>
            <div className={`font-mono text-sm ${stats.expectancy >= 0 ? "text-emerald-400" : "text-red-400"}`}>{fmt(stats.expectancy)}/trade</div></div>
        </div>
      </div>
    )}
  </div>);
};
