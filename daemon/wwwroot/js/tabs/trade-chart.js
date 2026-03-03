// =====================================================================
//  Trade Chart Modal
// =====================================================================

var TradeChartModal = ({ terminal, ticket, send, onClose }) => {
  const [data, setData] = useState(null);
  const chartRef = useRef(null);
  const chartContainerRef = useRef(null);
  const modalRef = useRef(null);

  // Drag state
  const [dragPos, setDragPos] = useState(null);
  const dragStart = useRef(null);

  useEffect(() => {
    const fetch = () => send({ cmd: 'get_trade_chart', terminal, ticket });
    fetch();
    const onChart = (msg) => {
      if (msg.error) { setData({ error: msg.error }); return; }
      if (msg.raw) {
        try { setData(JSON.parse(msg.raw)); } catch { setData(msg); }
      } else { setData(msg); }
    };
    wsManager.on('cmd:trade_chart', onChart);
    const interval = setInterval(fetch, 10000);
    return () => { wsManager.off('cmd:trade_chart', onChart); clearInterval(interval); };
  }, [terminal, ticket]);

  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  // Drag handlers
  const onDragStart = (e) => {
    if (e.target.closest('button')) return;
    dragStart.current = { x: e.clientX - (dragPos?.x || 0), y: e.clientY - (dragPos?.y || 0) };
    const onMove = (ev) => {
      if (!dragStart.current) return;
      setDragPos({ x: ev.clientX - dragStart.current.x, y: ev.clientY - dragStart.current.y });
    };
    const onUp = () => { dragStart.current = null; window.removeEventListener('mousemove', onMove); window.removeEventListener('mouseup', onUp); };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  useEffect(() => {
    if (!data?.bars?.length || !chartContainerRef.current) return;
    if (chartRef.current) { chartRef.current.remove(); chartRef.current = null; }

    const sym = data.trade?.symbol || '';
    const digits = data.digits || (sym.includes('JPY') ? 3 : 5);
    const minMove = 1 / Math.pow(10, digits);

    const chart = LightweightCharts.createChart(chartContainerRef.current, {
      width: chartContainerRef.current.clientWidth, height: 420,
      layout: { background: { color: '#0a0a0b' }, textColor: '#52525b', fontSize: 9, fontFamily: "'JetBrains Mono', monospace" },
      grid: { vertLines: { color: '#18181b' }, horzLines: { color: '#18181b' } },
      timeScale: { timeVisible: true, borderColor: '#1f1f23', rightOffset: 5 },
      rightPriceScale: { borderColor: '#1f1f23', scaleMargins: { top: 0.08, bottom: 0.08 } },
      crosshair: { mode: 0, vertLine: { color: '#3f3f4633', style: 0, width: 1 }, horzLine: { color: '#3f3f4633', style: 0, width: 1 } },
    });
    chartRef.current = chart;

    const candleSeries = chart.addCandlestickSeries({
      upColor: '#22c55e', downColor: '#ef4444', borderUpColor: '#22c55e', borderDownColor: '#ef4444',
      wickUpColor: '#22c55e66', wickDownColor: '#ef444466',
      priceFormat: { type: 'price', precision: digits, minMove },
    });

    const bars = (data.bars || [])
      .map(b => ({ time: b.time ?? b.Time, open: b.open ?? b.Open, high: b.high ?? b.High, low: b.low ?? b.Low, close: b.close ?? b.Close }))
      .filter(b => b.time != null)
      .sort((a, b) => a.time - b.time);
    candleSeries.setData(bars);

    const trade = data.trade;
    const isLive = data.mode === "live";
    const dir = trade?.direction;

    // Entry line
    if (trade?.entryPrice || trade?.entry_price) {
      const ep = trade.entryPrice || trade.entry_price;
      candleSeries.createPriceLine({ price: ep, color: '#f59e0b88', lineWidth: 1,
        lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: '' });
    }

    // Exit line (closed trades)
    if (trade?.exitPrice || trade?.exit_price) {
      const xp = trade.exitPrice || trade.exit_price;
      const pnl = trade.pnl ?? trade.unrealizedPnl ?? 0;
      candleSeries.createPriceLine({ price: xp, color: pnl >= 0 ? '#22c55e' : '#ef4444', lineWidth: 1,
        lineStyle: LightweightCharts.LineStyle.Dashed, axisLabelVisible: true, title: '' });
    }

    // TP line
    const tp = trade?.tp;
    if (tp && tp > 0) {
      candleSeries.createPriceLine({ price: tp, color: '#22c55e44', lineWidth: 1,
        lineStyle: LightweightCharts.LineStyle.Dotted, axisLabelVisible: true, title: 'TP' });
    }

    // Current SL (live)
    if (isLive && trade?.currentSl && trade.currentSl > 0) {
      candleSeries.createPriceLine({ price: trade.currentSl, color: '#ef4444cc', lineWidth: 1,
        lineStyle: LightweightCharts.LineStyle.Solid, axisLabelVisible: true, title: 'SL' });
    }

    // SL trail
    const slHist = data.slHistory || data.sl_history || [];
    if (slHist.length > 0) {
      const slLine = chart.addLineSeries({ color: '#ef444466', lineWidth: 1,
        lineStyle: LightweightCharts.LineStyle.Solid, lineType: LightweightCharts.LineType.WithSteps,
        priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false,
        priceFormat: { type: 'price', precision: digits, minMove } });
      const slData = slHist.filter(s => (s.time || 0) > 0 && (s.sl || 0) > 0)
        .map(s => ({ time: s.time, value: s.sl })).sort((a, b) => a.time - b.time);
      if (slData.length > 0 && bars.length > 0) {
        const lastBar = bars[bars.length - 1];
        if (lastBar.time > slData[slData.length - 1].time)
          slData.push({ time: lastBar.time, value: slData[slData.length - 1].value });
      }
      if (slData.length > 0) slLine.setData(slData);
    }

    // Entry + Exit markers
    const entryTime = trade?.entryTime || trade?.entry_time;
    if (entryTime && bars.length > 0) {
      const et = typeof entryTime === 'number' ? entryTime : Math.floor(new Date(entryTime).getTime() / 1000);
      const nearestBar = bars.reduce((best, b) => Math.abs(b.time - et) < Math.abs(best.time - et) ? b : best, bars[0]);
      const markers = [{ time: nearestBar.time,
        position: dir === 'BUY' || dir === 'LONG' ? 'belowBar' : 'aboveBar',
        color: '#f59e0b', shape: dir === 'BUY' || dir === 'LONG' ? 'arrowUp' : 'arrowDown',
        text: dir === 'BUY' || dir === 'LONG' ? 'BUY' : 'SELL' }];
      const exitTime = trade?.exit_time || trade?.closedAt;
      if (exitTime) {
        const xt = typeof exitTime === 'number' ? exitTime : Math.floor(new Date(exitTime).getTime() / 1000);
        const nearestExit = bars.reduce((best, b) => Math.abs(b.time - xt) < Math.abs(best.time - xt) ? b : best, bars[0]);
        const pnl = trade.pnl ?? 0;
        markers.push({ time: nearestExit.time,
          position: dir === 'BUY' || dir === 'LONG' ? 'aboveBar' : 'belowBar',
          color: pnl >= 0 ? '#22c55e' : '#ef4444', shape: 'square',
          text: trade.close_reason || trade.closeReason || 'EXIT' });
      }
      markers.sort((a, b) => a.time - b.time);
      candleSeries.setMarkers(markers);
    }

    chart.timeScale().fitContent();
    const ro = new ResizeObserver(() => {
      if (chartContainerRef.current) chart.applyOptions({ width: chartContainerRef.current.clientWidth });
    });
    ro.observe(chartContainerRef.current);
    return () => { ro.disconnect(); chart.remove(); chartRef.current = null; };
  }, [data]);

  const trade = data?.trade;
  const isLive = data?.mode === "live";
  const pnl = isLive ? trade?.unrealizedPnl : (trade?.pnl ?? trade?.pnl_pct);
  const dir = trade?.direction || "?";
  const entryP = trade?.entryPrice || trade?.entry_price;
  const exitP = trade?.exitPrice || trade?.exit_price;
  const closeReason = trade?.closeReason || trade?.close_reason;
  const vol = trade?.volume;
  const slMoves = (data?.slHistory || data?.sl_history || []).length;
  const durSec = trade?.durationSec || trade?.duration_sec || 0;
  const durH = Math.floor(durSec / 3600);
  const durM = Math.floor((durSec % 3600) / 60);
  const tf = trade?.timeframe;
  const currentPrice = trade?.current;

  return (
    <div className="fixed inset-0 bg-black/70 z-50 flex items-center justify-center" onClick={onClose}>
      <div ref={modalRef}
        className="bg-zinc-950 border border-zinc-800/60 rounded-lg w-full max-w-4xl max-h-[88vh] overflow-y-auto shadow-2xl shadow-black/50"
        style={dragPos ? { transform: `translate(${dragPos.x}px, ${dragPos.y}px)` } : undefined}
        onClick={e => e.stopPropagation()}>
        {/* Header — draggable */}
        <div className="flex items-center justify-between px-3 py-2 border-b border-zinc-800/50 cursor-grab active:cursor-grabbing select-none"
          onMouseDown={onDragStart}>
          <div className="flex items-center gap-2">
            {trade?.isVirtual || trade?.is_virtual ? <span className="text-purple-400 text-[9px] font-bold bg-purple-500/10 border border-purple-500/20 px-1 py-px rounded">[V]</span> : null}
            <span className={`text-[9px] font-bold uppercase px-1.5 py-px rounded ${(dir === "BUY" || dir === "LONG") ? "text-emerald-400 bg-emerald-500/10" : "text-red-400 bg-red-500/10"}`}>{dir}</span>
            <span className="font-mono text-sm text-zinc-200">{trade?.symbol || "..."}</span>
            {tf && <span className="text-[9px] text-zinc-600 bg-zinc-800/60 px-1 py-px rounded">{tf}</span>}
            {pnl != null && <span className={`font-mono text-sm font-semibold ${pnl >= 0 ? "text-emerald-400" : "text-red-400"}`}>
              {pnl >= 0 ? "+" : ""}${typeof pnl === 'number' ? pnl.toFixed(2) : pnl}</span>}
            {closeReason && <span className={`text-[9px] px-1.5 py-px rounded font-medium ${closeReason === 'TP' ? 'bg-emerald-500/10 text-emerald-400' : closeReason === 'SL' ? 'bg-red-500/10 text-red-400' : 'bg-zinc-800 text-zinc-500'}`}>{closeReason}</span>}
            {isLive && <span className="text-[9px] text-emerald-400 animate-pulse ml-1">● LIVE</span>}
          </div>
          <div className="flex items-center gap-2">
            <span className="text-[9px] text-zinc-600">{trade?.strategy}</span>
            <span className="text-[9px] text-zinc-700">#{ticket}</span>
            <button onClick={onClose} className="text-zinc-600 hover:text-zinc-400 text-sm leading-none ml-1">&times;</button>
          </div>
        </div>
        {/* Chart */}
        <div className="px-2 py-2">
          {data?.error ? (
            <div className="text-center text-zinc-600 py-12 text-xs">{data.error}</div>
          ) : !data ? (
            <div className="text-center text-zinc-600 py-12 animate-pulse text-xs">Loading chart...</div>
          ) : (
            <div ref={chartContainerRef} className="w-full rounded overflow-hidden" style={{ minHeight: 420 }} />
          )}
          {data && data.bars && data.bars.length === 0 && !data.error && (
            <div className="text-center text-zinc-700 py-4 text-[10px]">No bar data available — bars may have been pruned from cache.</div>
          )}
        </div>
        {/* Footer */}
        {trade && (
          <div className="px-3 pb-2 flex flex-wrap gap-x-5 gap-y-1 text-[10px] border-t border-zinc-800/40 pt-2">
            <div><span className="text-zinc-600">Entry </span><span className="text-zinc-400 font-mono">{entryP}</span></div>
            {exitP ? <div><span className="text-zinc-600">Exit </span><span className="text-zinc-400 font-mono">{exitP}</span></div>
              : currentPrice ? <div><span className="text-zinc-600">Current </span><span className="text-zinc-400 font-mono">{currentPrice}</span></div> : null}
            <div><span className="text-zinc-600">Vol </span><span className="text-zinc-400 font-mono">{vol}</span></div>
            {(trade?.currentSl || trade?.sl) ? <div><span className="text-zinc-600">SL </span><span className="text-red-400/80 font-mono">{trade.currentSl || trade.sl}</span></div> : null}
            {(trade?.tp) && trade.tp > 0 ? <div><span className="text-zinc-600">TP </span><span className="text-emerald-400/80 font-mono">{trade.tp}</span></div> : null}
            {slMoves > 0 && <div><span className="text-zinc-600">SL mv </span><span className="text-orange-400/80 font-mono">{slMoves}</span></div>}
            {durSec > 0 && <div><span className="text-zinc-600">Dur </span><span className="text-zinc-400">{durH}h {durM}m</span></div>}
            <div><span className="text-zinc-600">Term </span><span className="text-zinc-500">{terminal}</span></div>
          </div>
        )}
      </div>
    </div>
  );
};
