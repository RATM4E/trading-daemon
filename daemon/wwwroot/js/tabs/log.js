// =====================================================================
//  Tab: Log
// =====================================================================

var LogTab = ({ logs }) => {
  const [filter, setFilter] = useState("all");
  const tc = {
    ORDER: "text-blue-400", RISK: "text-amber-400", SIGNAL: "text-violet-400", SYSTEM: "text-zinc-400",
    RECON: "text-cyan-400", ERROR: "text-red-400", ALERT: "text-red-400", CONFIG: "text-emerald-400",
    STRATEGY: "text-violet-400", EMERGENCY: "text-red-500", AUDIT: "text-teal-400"
  };
  const isHeartbeat = (l) => l.type === "AUDIT" && (l.msg || '').startsWith("heartbeat_sent");
  const filtered = filter === "HEARTBEAT" ? logs.filter(isHeartbeat)
    : filter === "all" ? logs.filter(l => !isHeartbeat(l))
    : logs.filter(l => l.type === filter && !isHeartbeat(l));
  return (<div><div className="flex gap-1 mb-3">
    {["all", "ORDER", "RISK", "SIGNAL", "SYSTEM", "ALERT", "AUDIT", "HEARTBEAT", "ERROR"].map(f => (
      <button key={f} onClick={() => setFilter(f)} className={`px-2 py-1 text-xs rounded ${filter === f ? "bg-zinc-700 text-zinc-200" : "text-zinc-500 hover:text-zinc-300"}`}>
        {f === "all" ? "All" : f === "HEARTBEAT" ? "\u23f1 HB" : f}</button>))}</div>
    <div className="font-mono text-xs space-y-0.5 max-h-[600px] overflow-y-auto">
      {filtered.map((l, i) => (<div key={l.id || i} className="flex items-start gap-3 py-1 px-2 rounded hover:bg-zinc-800/30">
        <span className="text-zinc-600 shrink-0">{(l.time || '').substring(0, 19)}</span>
        <span className="text-zinc-500 shrink-0 w-20 truncate">{l.terminal}</span>
        <span className={`shrink-0 w-14 ${tc[l.type] || 'text-zinc-400'}`}>{isHeartbeat(l) ? "\u23f1 HB" : l.type}</span>
        {l.strategy && <span className="text-purple-400/70 shrink-0 truncate w-28">{l.strategy}</span>}
        <span className="text-zinc-300 break-all">{l.msg}</span></div>))}
      {filtered.length === 0 && <div className="text-center text-zinc-600 py-8">No events</div>}</div></div>);
};
