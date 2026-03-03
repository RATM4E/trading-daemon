// =====================================================================
//  Tab: Positions (global view across all terminals)
//  Phase 10: added Opened column with UTC timestamps
// =====================================================================

var PositionsTab = ({ positions, send, onTradeChart }) => {
  const [filter, setFilter] = useState("all");
  const filtered = filter === "all" ? positions : filter === "virtual" ? positions.filter(p => p.isVirtual) : positions.filter(p => !p.isVirtual);
  if (positions.length === 0) return <div className="text-center text-zinc-600 py-16">No open positions</div>;
  const handleClose = (terminal, ticket) => { if (confirm(`Close #${ticket}?`)) send({ cmd: 'close_position', terminal, ticket }); };
  const totalPnl = filtered.reduce((s, p) => s + (p.pnl || 0), 0);
  const hasVirtual = positions.some(p => p.isVirtual);
  return (<div>
    {hasVirtual && <div className="flex gap-1 mb-3">
      {["all", "real", "virtual"].map(f => (
        <button key={f} onClick={() => setFilter(f)} className={`px-2 py-1 text-xs rounded ${filter === f ? "bg-zinc-700 text-zinc-200" : "text-zinc-500 hover:text-zinc-300"}`}>
          {f === "all" ? "All" : f === "virtual" ? "Virtual" : "Real"}</button>
      ))}
    </div>}
    <table className="w-full text-xs"><thead><tr className="text-zinc-500 border-b border-zinc-800">
      <th className="text-left py-2 px-2 font-normal">Terminal</th><th className="text-left py-2 px-2 font-normal">Symbol</th>
      <th className="text-left py-2 px-2 font-normal">Dir</th><th className="text-right py-2 px-2 font-normal">Lot</th>
      <th className="text-right py-2 px-2 font-normal">Entry</th><th className="text-right py-2 px-2 font-normal">SL</th>
      <th className="text-right py-2 px-2 font-normal">Current</th><th className="text-right py-2 px-2 font-normal">P/L</th>
      <th className="text-left py-2 px-2 font-normal">Opened</th>
      <th className="text-center py-2 px-2 font-normal">Age</th><th className="text-left py-2 px-2 font-normal">Source</th>
      <th className="text-right py-2 px-2 font-normal"></th></tr></thead>
      <tbody>{filtered.map(p => (
        <tr key={`${p.terminal}-${p.ticket}`} className="border-b border-zinc-800/50 hover:bg-zinc-800/30">
          <td className="py-2 px-2 font-mono text-zinc-400">{p.terminal}</td>
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
          <td className="py-2 px-2"><span className={p.source === "unmanaged" ? "text-zinc-500" : "text-blue-400"}>{p.source}</span></td>
          <td className="py-2 px-2 text-right"><button onClick={() => handleClose(p.terminal, p.ticket)}
            className="px-2 py-0.5 text-red-400/70 border border-red-500/20 rounded hover:bg-red-500/10">Close</button></td>
        </tr>))}</tbody></table>
      <div className="flex items-center justify-between mt-4 pt-3 border-t border-zinc-800">
        <div className="text-xs text-zinc-500">{filtered.length} position{filtered.length !== 1 ? 's' : ''} &middot; Total P/L:{" "}
          <span className={totalPnl >= 0 ? "text-emerald-400" : "text-red-400"}>{totalPnl >= 0 ? "+" : ""}${totalPnl.toFixed(2)}</span></div>
        <div className="flex gap-2">{[...new Set(positions.map(p => p.terminal))].map(term => (
          <button key={term} onClick={() => { if (confirm(`Close ALL on ${term}?`)) send({ cmd: 'close_all', terminal: term }); }}
            className="px-3 py-1 text-xs text-amber-400/70 border border-amber-500/20 rounded hover:bg-amber-500/10">Close {term}</button>))}</div>
      </div></div>);
};
