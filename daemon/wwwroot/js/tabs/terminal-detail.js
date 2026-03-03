// =====================================================================
//  Terminal Detail View (opened when clicking on a terminal tile)
//  Phase 10: added Open time column, UTC labels
// =====================================================================

var TerminalDetailView = ({ detail, positions, onClose, send, onTradeChart }) => {
  const [timedOut, setTimedOut] = useState(false);
  useEffect(() => {
    if (detail) return;
    const t = setTimeout(() => setTimedOut(true), 8000);
    return () => clearTimeout(t);
  }, [detail]);

  const backBtn = (
    <button onClick={onClose} className="flex items-center gap-2 text-xs text-zinc-500 hover:text-zinc-300 mb-4 group">
      <span className="group-hover:-translate-x-0.5 transition-transform">&larr;</span> Back to Terminals
    </button>
  );

  if (!detail) return (
    <div className="fade-in">
      {backBtn}
      {timedOut
        ? <div className="text-center py-16"><div className="text-amber-400 text-sm mb-2">Request timed out</div>
            <div className="text-zinc-500 text-xs">Check daemon console for errors</div></div>
        : <div className="text-center text-zinc-400 py-16">Loading terminal data...</div>}
    </div>
  );

  if (detail.error) return (
    <div className="fade-in">
      {backBtn}
      <div className="text-center py-16">
        <div className="text-red-400 text-sm mb-2">Failed to load terminal detail</div>
        <div className="text-zinc-500 text-xs font-mono">{detail.error}</div>
      </div>
    </div>
  );

  const { terminal, stats, equityCurve, openPositions, closedPositions } = detail;
  const livePos = (positions || []).filter(p => p.terminal === terminal);

  const handleClose = (ticket) => { if (confirm(`Close position #${ticket}?`)) send({ cmd: 'close_position', terminal, ticket }); };

  return (
    <div className="fade-in">
      <button onClick={onClose} className="flex items-center gap-2 text-xs text-zinc-500 hover:text-zinc-300 mb-4 group">
        <span className="group-hover:-translate-x-0.5 transition-transform">&larr;</span> Back to Terminals
      </button>

      <h2 className="text-lg font-semibold text-zinc-200 mb-4">{terminal}</h2>

      {/* Stats Grid */}
      {stats && (
        <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-8 gap-2 mb-6">
          <Stat label="Total Trades" value={stats.totalTrades} />
          <Stat label="Wins" value={stats.wins} color="text-emerald-400" />
          <Stat label="Losses" value={stats.losses} color="text-red-400" />
          <Stat label="Win Rate" value={`${stats.winRate}%`} color={stats.winRate >= 50 ? "text-emerald-400" : "text-red-400"} />
          <Stat label="Total P/L" value={`$${stats.totalPnl}`} color={stats.totalPnl >= 0 ? "text-emerald-400" : "text-red-400"} />
          <Stat label="Avg Win" value={`$${stats.avgWin}`} color="text-emerald-400" />
          <Stat label="Avg Loss" value={`$${stats.avgLoss}`} color="text-red-400" />
          <Stat label="Avg Latency" value={`${stats.avgLatencyMs}ms`} />
        </div>
      )}

      {/* Equity Curve — simple SVG */}
      {equityCurve && equityCurve.length > 1 && (() => {
        const W = 800, H = 180, PAD = 40;
        const vals = equityCurve.map(p => p.pnl);
        const minV = Math.min(0, ...vals), maxV = Math.max(0, ...vals);
        const range = maxV - minV || 1;
        const scaleX = (i) => PAD + (i / (vals.length - 1)) * (W - PAD * 2);
        const scaleY = (v) => H - PAD - ((v - minV) / range) * (H - PAD * 2);
        const points = vals.map((v, i) => `${scaleX(i)},${scaleY(v)}`).join(' ');
        const areaPoints = `${scaleX(0)},${scaleY(0)} ${points} ${scaleX(vals.length - 1)},${scaleY(0)}`;
        const zeroY = scaleY(0);
        const last = vals[vals.length - 1];
        return (
          <div className="mb-6 bg-zinc-900/50 border border-zinc-800 rounded-xl p-4">
            <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Equity Curve (Closed P/L)</h3>
            <svg viewBox={`0 0 ${W} ${H}`} className="w-full" style={{ maxHeight: '200px' }}>
              <line x1={PAD} y1={zeroY} x2={W - PAD} y2={zeroY} stroke="#3f3f46" strokeWidth="1" strokeDasharray="4" />
              <polygon points={areaPoints} fill={last >= 0 ? "rgba(16,185,129,0.15)" : "rgba(239,68,68,0.15)"} />
              <polyline points={points} fill="none" stroke={last >= 0 ? "#10b981" : "#ef4444"} strokeWidth="2" />
              <text x={PAD - 4} y={scaleY(maxV) + 4} fill="#71717a" fontSize="10" textAnchor="end">${maxV.toFixed(0)}</text>
              <text x={PAD - 4} y={scaleY(minV) + 4} fill="#71717a" fontSize="10" textAnchor="end">${minV.toFixed(0)}</text>
              <text x={PAD - 4} y={zeroY + 4} fill="#71717a" fontSize="10" textAnchor="end">$0</text>
              <text x={W - PAD} y={H - 8} fill="#71717a" fontSize="10" textAnchor="end">{equityCurve.length} trades</text>
              <circle cx={scaleX(vals.length - 1)} cy={scaleY(last)} r="3" fill={last >= 0 ? "#10b981" : "#ef4444"} />
              <text x={scaleX(vals.length - 1) + 8} y={scaleY(last) + 4} fill={last >= 0 ? "#10b981" : "#ef4444"} fontSize="11" fontWeight="600">${last.toFixed(2)}</text>
            </svg>
          </div>
        );
      })()}

      {/* Live Positions */}
      <div className="mb-6">
        <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Open Positions ({livePos.length})</h3>
        {livePos.length === 0 ? (
          <div className="text-sm text-zinc-600 py-4 text-center border border-dashed border-zinc-800 rounded-lg">No open positions</div>
        ) : (
          <table className="w-full text-xs">
            <thead><tr className="text-zinc-500 border-b border-zinc-800">
              <th className="text-left py-2 px-2 font-normal">Symbol</th>
              <th className="text-left py-2 px-2 font-normal">Dir</th>
              <th className="text-right py-2 px-2 font-normal">Lot</th>
              <th className="text-right py-2 px-2 font-normal">Entry</th>
              <th className="text-right py-2 px-2 font-normal">SL</th>
              <th className="text-right py-2 px-2 font-normal">Current</th>
              <th className="text-right py-2 px-2 font-normal">P/L</th>
              <th className="text-left py-2 px-2 font-normal">Opened</th>
              <th className="text-center py-2 px-2 font-normal">Age</th>
              <th className="text-right py-2 px-2 font-normal"></th>
            </tr></thead>
            <tbody>{livePos.map(p => (
              <tr key={p.ticket} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
                <td className="py-2 px-2 font-mono text-zinc-200 font-medium">
          {p.isVirtual && <span className="text-purple-400 text-[10px] mr-1 font-bold">[V]</span>}{p.symbol}</td>
                <td className="py-2 px-2"><Badge color={p.dir === "LONG" ? "green" : "red"}>{p.dir}</Badge></td>
                <td className="py-2 px-2 text-right font-mono text-zinc-300">{p.lot}</td>
                <td className="py-2 px-2 text-right font-mono text-zinc-400">{p.entry}</td>
                <td className="py-2 px-2 text-right font-mono text-zinc-500">{p.sl}</td>
                <td className="py-2 px-2 text-right font-mono text-zinc-200">{p.current}</td>
                <td className={`py-2 px-2 text-right font-mono font-medium ${(p.pnl || 0) >= 0 ? "text-emerald-400" : "text-red-400"}`}>
                  {(p.pnl || 0) >= 0 ? "+" : ""}${(p.pnl || 0).toFixed(2)}</td>
                <td className="py-2 px-2 text-zinc-600 whitespace-nowrap">{formatUtc(p.openTime)}</td>
                <td className="py-2 px-2 text-center text-zinc-500">{p.age}</td>
                <td className="py-2 px-2 text-right">
                  <button onClick={() => handleClose(p.ticket)}
                    className="px-2 py-0.5 text-red-400/70 border border-red-500/20 rounded hover:bg-red-500/10">Close</button>
          {onTradeChart && <button onClick={() => onTradeChart(p.terminal, p.ticket)} title="Trade chart"
            className="px-1.5 py-0.5 text-zinc-500 border border-zinc-700/50 rounded hover:bg-zinc-700/30 hover:text-zinc-300 ml-1">&#x1F4CA;</button>}</td>
              </tr>))}</tbody>
          </table>
        )}
      </div>

      {/* Closed Positions */}
      {closedPositions && closedPositions.length > 0 && (
        <div>
          <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Recent Closed ({closedPositions.length})</h3>
          <table className="w-full text-xs">
            <thead><tr className="text-zinc-500 border-b border-zinc-800">
              <th className="text-left py-1.5 px-2 font-normal">Symbol</th>
              <th className="text-left py-1.5 px-2 font-normal">Dir</th>
              <th className="text-right py-1.5 px-2 font-normal">Lot</th>
              <th className="text-right py-1.5 px-2 font-normal">Entry</th>
              <th className="text-right py-1.5 px-2 font-normal">Close</th>
              <th className="text-right py-1.5 px-2 font-normal">P/L</th>
              <th className="text-left py-1.5 px-2 font-normal">Reason</th>
              <th className="text-left py-1.5 px-2 font-normal">Opened</th>
              <th className="text-left py-1.5 px-2 font-normal">Closed</th>
              <th className="text-right py-1.5 px-2 font-normal"></th>
            </tr></thead>
            <tbody>{closedPositions.slice(0, 50).map((p, i) => (
              <tr key={i} className="border-b border-zinc-800/30 hover:bg-zinc-800/20">
                <td className="py-1.5 px-2 font-mono text-zinc-300">{p.symbol}</td>
                <td className="py-1.5 px-2"><Badge color={p.dir === "LONG" || p.dir === "BUY" ? "green" : "red"}>{p.dir}</Badge></td>
                <td className="py-1.5 px-2 text-right font-mono text-zinc-400">{p.volume}</td>
                <td className="py-1.5 px-2 text-right font-mono text-zinc-400">{p.priceOpen}</td>
                <td className="py-1.5 px-2 text-right font-mono text-zinc-400">{p.closePrice}</td>
                <td className={`py-1.5 px-2 text-right font-mono font-medium ${(p.pnl || 0) >= 0 ? "text-emerald-400" : "text-red-400"}`}>
                  {(p.pnl || 0) >= 0 ? "+" : ""}${(p.pnl || 0).toFixed(2)}</td>
                <td className="py-1.5 px-2 text-zinc-500">{p.closeReason}</td>
                <td className="py-1.5 px-2 text-zinc-600 whitespace-nowrap">{formatUtc(p.openedAt)}</td>
                <td className="py-1.5 px-2 text-zinc-600 whitespace-nowrap">{formatUtc(p.closedAt)}</td>
                <td className="py-1.5 px-2 text-right">
                  {onTradeChart && p.ticket && <button onClick={() => onTradeChart(detail.terminal, p.ticket)} title="Trade chart"
                    className="px-1.5 py-0.5 text-zinc-600 border border-zinc-700/40 rounded hover:bg-zinc-700/30 hover:text-zinc-400">&#x1F4CA;</button>}
                </td>
              </tr>))}</tbody>
          </table>
        </div>
      )}
    </div>
  );
};
