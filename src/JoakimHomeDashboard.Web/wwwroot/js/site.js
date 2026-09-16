(() => {
  const render = canvas => {
    const source = document.getElementById(canvas.dataset.chart);
    if (!source) return;
    const data = JSON.parse(source.textContent);
    const rect = canvas.getBoundingClientRect();
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.max(320, rect.width) * ratio;
    canvas.height = Math.max(220, rect.height) * ratio;
    const c = canvas.getContext('2d'); c.scale(ratio, ratio);
    const w = canvas.width / ratio, h = canvas.height / ratio, pad = {l:52,r:14,t:24,b:35};
    const values = data.series.flatMap(s => s.values.filter(v => v !== null));
    let min = Math.min(0, ...values), max = Math.max(0, ...values);
    if (max === min) { max += 1; min -= 1; }
    const x = i => pad.l + i * (w-pad.l-pad.r) / Math.max(1,data.labels.length-1);
    const y = v => pad.t + (max-v) * (h-pad.t-pad.b) / (max-min);
    c.font='11px Inter,system-ui'; c.fillStyle='#8290a3'; c.strokeStyle='#223044'; c.lineWidth=1;
    for(let i=0;i<=4;i++){const v=min+(max-min)*i/4, py=y(v);c.beginPath();c.moveTo(pad.l,py);c.lineTo(w-pad.r,py);c.stroke();c.fillText(v.toFixed(Math.abs(max-min)<20?1:0),4,py+4)}
    c.strokeStyle='#58708f';c.beginPath();c.moveTo(pad.l,y(0));c.lineTo(w-pad.r,y(0));c.stroke();
    data.series.forEach(s=>{c.strokeStyle=s.color;c.lineWidth=2;c.beginPath();let started=false;s.values.forEach((v,i)=>{if(v===null){started=false;return;}if(!started){c.moveTo(x(i),y(v));started=true}else c.lineTo(x(i),y(v))});c.stroke()});
    const labels=[0,Math.floor((data.labels.length-1)/2),data.labels.length-1].filter((v,i,a)=>a.indexOf(v)===i);
    c.fillStyle='#8290a3'; labels.forEach(i=>{const label=data.labels[i]||'';c.fillText(label.length>7?label.slice(0,7):label,Math.max(pad.l,x(i)-24),h-10)});
    let lx=pad.l; data.series.forEach(s=>{c.fillStyle=s.color;c.fillRect(lx,pad.t-15,10,3);c.fillStyle='#aeb9c8';c.fillText(s.name,lx+15,pad.t-9);lx+=c.measureText(s.name).width+44});
  };
  const renderAll=()=>document.querySelectorAll('canvas.energy-chart').forEach(render);
  addEventListener('load',renderAll); let t; addEventListener('resize',()=>{clearTimeout(t);t=setTimeout(renderAll,150)});
})();
