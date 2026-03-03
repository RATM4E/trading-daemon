// =====================================================================
//  Terminal Discovery Panel
//  Modal for auto-discovering and manually adding MT5 terminals
// =====================================================================

var DiscoveryPanel = ({ discovered, onClose, send }) => {
  const [names, setNames] = useState({});
  const [adding, setAdding] = useState(null);

  // Manual add state
  const [manualPath, setManualPath] = useState("");
  const [probing, setProbing] = useState(false);
  const [probeResult, setProbeResult] = useState(null);
  const [probeError, setProbeError] = useState(null);

  // Listen for probe_terminal responses
  useEffect(() => {
    const onProbe = (msg) => {
      setProbing(false);
      if (msg.ok && msg.data) {
        setProbeResult(msg.data);
        setProbeError(null);
      } else {
        setProbeResult(null);
        setProbeError(msg.error || "Probe failed");
      }
    };
    wsManager.on('cmd:probe_terminal', onProbe);
    return () => wsManager.off('cmd:probe_terminal', onProbe);
  }, []);

  const handleAdd = (path) => {
    const name = names[path];
    if (!name || !name.trim()) { alert("Enter a name for the terminal"); return; }
    setAdding(path);
    send({ cmd: 'add_discovered_terminal', path, name: name.trim() });
    setTimeout(() => { setAdding(null); onClose(); }, 2000);
  };

  const handleProbe = () => {
    if (!manualPath.trim()) return;
    setProbing(true); setProbeResult(null); setProbeError(null);
    send({ cmd: 'probe_terminal', path: manualPath.trim() });
  };

  const unreachable = discovered?.__unreachable || 0;

  return (
    <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={onClose}>
      <div className="bg-zinc-900 border border-zinc-700 rounded-xl p-6 max-w-2xl w-full mx-4 max-h-[80vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg text-zinc-200 font-semibold">Discover MT5 Terminals</h2>
          <button onClick={onClose} className="text-zinc-500 hover:text-zinc-300 text-xl">&times;</button>
        </div>

        {/* Auto-discovered section */}
        {discovered === null && (
          <div className="text-center py-8 text-zinc-500 animate-pulse">Scanning for running MT5 terminals...</div>
        )}

        {discovered && discovered.length === 0 && !unreachable && (
          <div className="text-center py-8">
            <div className="text-zinc-500 mb-2">No running MT5 terminals found</div>
            <div className="text-xs text-zinc-600">Make sure your MT5 terminals are running and logged in</div>
          </div>
        )}

        {discovered && discovered.length > 0 && (
          <div className="space-y-3 mb-4">
            {discovered.map((t, i) => (
              <div key={i} className={`border rounded-lg p-4 ${t.alreadyConfigured
                ? "bg-zinc-800/30 border-zinc-700/30 opacity-60" : "bg-zinc-800/50 border-zinc-700"}`}>
                <div className="flex items-center justify-between mb-2">
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-sm text-zinc-200">{t.company || 'Unknown'}</span>
                    {t.alreadyConfigured && <Badge color="green">Configured as {t.configuredAs}</Badge>}
                  </div>
                  {t.status === "ok" && <Badge color={t.tradeMode === 0 ? "gray" : "blue"}>
                    {t.tradeMode === 0 ? "DEMO" : "REAL"}</Badge>}
                </div>

                {t.status === "ok" ? (
                  <div className="text-xs text-zinc-500 mb-3 space-y-0.5">
                    <div>Account: {t.login} &middot; Server: {t.server} &middot; {t.marginMode === 2 ? 'Hedge' : 'Netting'}</div>
                    <div>Balance: ${t.balance?.toLocaleString(undefined, { minimumFractionDigits: 2 })} {t.currency} &middot; Leverage: 1:{t.leverage}</div>
                    <div className="text-zinc-600 truncate">Path: {t.path}</div>
                  </div>
                ) : (
                  <div className="text-xs text-red-400/70 mb-3">{t.error || 'Failed to probe'}</div>
                )}

                {!t.alreadyConfigured && t.status === "ok" && (
                  <div className="flex items-center gap-2">
                    <input type="text" placeholder="Terminal name (e.g. AudaCity)" value={names[t.path] || ""}
                      onChange={e => setNames(p => ({ ...p, [t.path]: e.target.value }))}
                      className="flex-1 bg-zinc-900 border border-zinc-700 rounded px-3 py-1.5 text-xs text-zinc-300 placeholder-zinc-600" />
                    <button onClick={() => handleAdd(t.path)} disabled={adding === t.path}
                      className="px-4 py-1.5 text-xs bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded hover:bg-emerald-600/30 disabled:opacity-50">
                      {adding === t.path ? "Adding..." : "Add"}
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}

        {/* Unreachable processes warning */}
        {unreachable > 0 && (
          <div className="border border-amber-500/20 bg-amber-500/5 rounded-lg px-4 py-3 mb-4 text-xs text-amber-400/80">
            <span className="font-medium">{unreachable} terminal(s) detected but inaccessible</span>
            <span className="text-amber-400/50"> — try running daemon as Administrator, or add them manually below.</span>
          </div>
        )}

        {/* Divider */}
        {discovered !== null && (
          <div className="border-t border-zinc-800 pt-4">
            <h3 className="text-xs text-zinc-500 uppercase tracking-wider mb-3">Manual Add</h3>
            <div className="text-xs text-zinc-600 mb-3">
              Paste the full path to terminal64.exe (the terminal must be running and logged in)
            </div>
            <div className="flex items-center gap-2 mb-3">
              <input type="text" placeholder="D:\MT5\The5ers\terminal64.exe" value={manualPath}
                onChange={e => { setManualPath(e.target.value); setProbeResult(null); setProbeError(null); }}
                onKeyDown={e => { if (e.key === 'Enter') handleProbe(); }}
                className="flex-1 bg-zinc-900 border border-zinc-700 rounded px-3 py-1.5 text-xs text-zinc-300 placeholder-zinc-600 font-mono" />
              <button onClick={handleProbe} disabled={probing || !manualPath.trim()}
                className="px-4 py-1.5 text-xs bg-blue-600/20 border border-blue-500/30 text-blue-400 rounded hover:bg-blue-600/30 disabled:opacity-40">
                {probing ? "Probing..." : "Probe"}
              </button>
            </div>

            {/* Probe error */}
            {probeError && (
              <div className="text-xs text-red-400/80 bg-red-500/5 border border-red-500/20 rounded px-3 py-2 mb-3">{probeError}</div>
            )}

            {/* Probe result */}
            {probeResult && probeResult.status === "ok" && (
              <div className={`border rounded-lg p-4 ${probeResult.alreadyConfigured
                ? "bg-zinc-800/30 border-zinc-700/30 opacity-60" : "bg-emerald-500/5 border-emerald-500/30"}`}>
                <div className="flex items-center justify-between mb-2">
                  <div className="flex items-center gap-2">
                    <span className="text-emerald-400 text-xs">&#x2713;</span>
                    <span className="font-mono text-sm text-zinc-200">{probeResult.company || 'Unknown'}</span>
                    {probeResult.alreadyConfigured && <Badge color="green">Already configured as {probeResult.configuredAs}</Badge>}
                  </div>
                  <Badge color={probeResult.tradeMode === 0 ? "gray" : "blue"}>
                    {probeResult.tradeMode === 0 ? "DEMO" : "REAL"}</Badge>
                </div>
                <div className="text-xs text-zinc-500 mb-3 space-y-0.5">
                  <div>Account: {probeResult.login} &middot; Server: {probeResult.server} &middot; {probeResult.marginMode === 2 ? 'Hedge' : 'Netting'}</div>
                  <div>Balance: ${probeResult.balance?.toLocaleString(undefined, { minimumFractionDigits: 2 })} {probeResult.currency} &middot; Leverage: 1:{probeResult.leverage}</div>
                </div>
                {!probeResult.alreadyConfigured && (
                  <div className="flex items-center gap-2">
                    <input type="text" placeholder="Terminal name (e.g. The5ers_Copy)" value={names[probeResult.path] || ""}
                      onChange={e => setNames(p => ({ ...p, [probeResult.path]: e.target.value }))}
                      className="flex-1 bg-zinc-900 border border-zinc-700 rounded px-3 py-1.5 text-xs text-zinc-300 placeholder-zinc-600" />
                    <button onClick={() => handleAdd(probeResult.path)} disabled={adding === probeResult.path}
                      className="px-4 py-1.5 text-xs bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded hover:bg-emerald-600/30 disabled:opacity-50">
                      {adding === probeResult.path ? "Adding..." : "Add"}
                    </button>
                  </div>
                )}
              </div>
            )}

            {probeResult && probeResult.status !== "ok" && (
              <div className="text-xs text-red-400/80 bg-red-500/5 border border-red-500/20 rounded px-3 py-2">
                Terminal found but: {probeResult.error || 'not connected'}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
};
