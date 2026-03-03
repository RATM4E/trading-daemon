// =====================================================================
//  Terminal Settings Panel (ConfigPanel)
//  Inline config editor shown when "Settings" is clicked on a terminal card
// =====================================================================

var ConfigPanel = ({ terminal, profile, onSave, onSetMode }) => {
  const [draft, setDraft] = useState({ ...profile });
  const upd = (k, v) => setDraft(d => ({ ...d, [k]: v }));
  return (
    <div className="mt-4 pt-4 border-t border-zinc-800 fade-in">
      <div className="grid grid-cols-2 gap-x-8 gap-y-3 mb-4">

        {/* Row 1 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Mode</span>
          <select className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300"
            value={terminal.mode} onChange={e => onSetMode(terminal.id, e.target.value)}>
            <option value="auto">Full Auto</option><option value="semi">Semi-Auto</option><option value="monitor">Monitor Only</option><option value="virtual">Virtual</option></select></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Daily DD limit ($)</span>
          <span className="flex items-center gap-1">
            <button onClick={() => upd('dailyDdMode', draft.dailyDdMode === 'soft' ? 'hard' : 'soft')}
              className={`px-2 py-0.5 rounded text-[10px] font-bold uppercase tracking-wide border ${
                draft.dailyDdMode === 'soft'
                  ? 'border-amber-500/50 bg-amber-500/15 text-amber-400'
                  : 'border-red-500/50 bg-red-500/15 text-red-400'}`}
            >{draft.dailyDdMode || 'hard'}</button>
            <input type="number" value={draft.dailyDDLimit} onChange={e => upd('dailyDDLimit', +e.target.value)}
              className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" />
          </span></label>

        {/* Row 2 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Type</span>
          <select className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300"
            value={terminal.type} onChange={e => onSave(terminal.id, { type: e.target.value })}>
            <option value="prop">Prop</option><option value="real">Real</option><option value="demo">Demo</option><option value="test">Test</option></select></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Daily DD rollover (%)</span>
          <input type="number" step="0.5" min="0" value={draft.dailyDdPercent || 0}
            onChange={e => upd('dailyDdPercent', +e.target.value)}
            title="EOD equity-based recalc. 0 = disabled (fixed $ limit)"
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>

        {/* Row 3 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Server TZ</span>
          <select className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300"
            value={draft.serverTimezone || 'UTC'} onChange={e => upd('serverTimezone', e.target.value)}>
            <option value="UTC">UTC</option>
            <option value="E. Europe Standard Time">EET  -  Cyprus (+2/+3)</option>
            <option value="Eastern Standard Time">EST  -  New York (-5/-4)</option>
            <option value="GMT Standard Time">GMT  -  London (+0/+1)</option>
            <option value="W. Europe Standard Time">CET  -  Frankfurt (+1/+2)</option>
            <option value="FLE Standard Time">FLE  -  Helsinki (+2/+3)</option>
            <option value="Tokyo Standard Time">JST  -  Tokyo (+9)</option>
            <option value="AUS Eastern Standard Time">AEST  -  Sydney (+10/+11)</option>
            <option value="Central Standard Time">CST  -  Chicago (-6/-5)</option>
            <option value="Russian Standard Time">MSK  -  Moscow (+3)</option>
          </select></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">R-cap (R/day)</span>
          <span className="flex items-center gap-1">
            <button onClick={() => {
                if (draft.rCapOn) {
                  upd('rCapOn', false); upd('rCapLimit', -1);
                } else {
                  upd('rCapOn', true); if ((draft.rCapLimit ?? 0) < 0) upd('rCapLimit', 0);
                }
              }}
              className={`px-2 py-0.5 rounded text-[10px] font-bold uppercase tracking-wide border ${
                draft.rCapOn
                  ? 'border-emerald-500/50 bg-emerald-500/15 text-emerald-400'
                  : (draft.rCapLimit ?? 0) < 0
                    ? 'border-red-500/50 bg-red-500/10 text-red-400'
                    : 'border-zinc-600/50 bg-zinc-800 text-zinc-500'}`}
            >{draft.rCapOn ? 'ON' : 'OFF'}</button>
            <input type="number" step="0.1" min="0"
              value={draft.rCapLimit > 0 ? draft.rCapLimit : ''}
              placeholder={terminal.rCapConfigDefault > 0 ? String(terminal.rCapConfigDefault) : '—'}
              onChange={e => upd('rCapLimit', +e.target.value || 0)}
              className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-16 text-right font-mono" />
          </span></label>

        {/* Row 4 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Risk type</span>
          <select className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300"
            value={draft.riskType || "fixed"} onChange={e => upd('riskType', e.target.value)}>
            <option value="fixed">Fixed $</option><option value="percent">% of balance</option></select></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Cumulative DD limit ($)</span>
          <input type="number" value={draft.cumDDLimit} onChange={e => upd('cumDDLimit', +e.target.value)}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>

        {/* Row 5 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Max margin/trade (%)</span>
          <span className="flex items-center gap-1">
            <input type="number" step="0.1" value={draft.maxMarginTrade} onChange={e => upd('maxMarginTrade', +e.target.value)}
              className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-16 text-right font-mono" />
            <select className="bg-zinc-800 border border-zinc-700 rounded px-1 py-1 text-xs text-zinc-300"
              value={draft.marginTradeMode || 'block'} onChange={e => upd('marginTradeMode', e.target.value)}>
              <option value="block">Block</option><option value="reduce">Reduce</option></select>
          </span></label>
        <label className="flex items-center justify-between">
          <span className="text-xs text-zinc-400">Risk/trade {draft.riskType === "percent" ? "(%)" : "($)"}</span>
          <input type="number" step={draft.riskType === "percent" ? "0.1" : "1"} value={draft.maxRiskTrade} onChange={e => upd('maxRiskTrade', +e.target.value)}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>

        {/* Row 6 -------------------------------------------------- */}
        <div className="space-y-3">
          <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">No-trade start</span>
            <input type="text" placeholder="HH:mm" value={draft.noTradeStart || ''} onChange={e => upd('noTradeStart', e.target.value)}
              className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>
          <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">No-trade end</span>
            <input type="text" placeholder="HH:mm" value={draft.noTradeEnd || ''} onChange={e => upd('noTradeEnd', e.target.value)}
              className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>
        </div>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Max deposit load (%)</span>
          <input type="number" step="0.1" value={draft.maxDepositLoad} onChange={e => upd('maxDepositLoad', +e.target.value)}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>

        {/* Row 7 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Block level</span>
          <select value={draft.newsMinImpact || 2} onChange={e => upd('newsMinImpact', +e.target.value)}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-32 text-right font-mono">
            <option value={3}>High only</option>
            <option value={2}>High + Medium</option>
            <option value={1}>All events</option>
          </select></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">News window (min)</span>
          <input type="number" min="1" max="120" value={draft.newsWindowMin || 15} onChange={e => upd('newsWindowMin', +e.target.value)}
            className="bg-zinc-800 border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-300 w-24 text-right font-mono" /></label>

        {/* Row 8 -------------------------------------------------- */}
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">Include USD news</span>
          <button onClick={() => upd('newsIncludeUsd', !draft.newsIncludeUsd)}
            className={`px-3 py-1 rounded border text-xs font-medium ${draft.newsIncludeUsd !== false
              ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-400"
              : "border-zinc-600/30 bg-zinc-800 text-zinc-500"}`}>{draft.newsIncludeUsd !== false ? "ON" : "OFF"}</button></label>
        <label className="flex items-center justify-between"><span className="text-xs text-zinc-400">News auto-BE</span>
          <button onClick={() => upd('newsBeEnabled', !draft.newsBeEnabled)}
            className={`px-3 py-1 rounded border text-xs font-medium ${draft.newsBeEnabled
              ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-400"
              : "border-zinc-600/30 bg-zinc-800 text-zinc-500"}`}>{draft.newsBeEnabled ? "ON" : "OFF"}</button></label>

      </div>

      <button onClick={() => onSave(terminal.id, draft)}
        className="px-3 py-1.5 text-xs bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 rounded hover:bg-emerald-600/30 font-medium">Save Profile</button>
    </div>
  );
};
