/* Attention Atlas v1. No analytics, network calls, or third-party dependencies. */
(function () {
  'use strict';
  const {DURATION,HeatField,summarize,validSession,TRAIL_COLORS,minuteAt,pathSegments,clusterClicks}=window.AttentionEngine;
  const $=id=>document.getElementById(id), map=$('map'), heat=$('heat'), trail=$('trail');
  const hctx=heat.getContext('2d'), tctx=trail.getContext('2d');
  const scratch=document.createElement('canvas'), sctx=scratch.getContext('2d');
  const KEY='attention-atlas-v1', CIRCUMFERENCE=100;
  let field, current=null, history=[], mode='idle', previous=null, palette='ember', showPath=true, showHeat=true, showClicks=true;
  let pointer=null, broken=true, sampleTime=0, lastTick=0, lastRender=0, dirty=true, lastSave=0, lastStats=0;
  let replaying=false, replayPosition=0, replayPrevious=0, replayFrame=0, reviewUntil=Infinity;
  let toastTimeout, storageOK=true, dpr=1;
  const dateKey=(date=new Date())=>[date.getFullYear(),String(date.getMonth()+1).padStart(2,'0'),String(date.getDate()).padStart(2,'0')].join('-');
  const timeString=ms=>{const seconds=Math.max(0,Math.ceil(ms/1000));return `${String(Math.floor(seconds/60)).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`;};
  const clockString=ms=>{const seconds=Math.floor(ms/1000);return `${String(Math.floor(seconds/60)).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`;};
  function toast(message){$('toast').textContent=message;$('toast').classList.add('visible');clearTimeout(toastTimeout);toastTimeout=setTimeout(()=>$('toast').classList.remove('visible'),3600);}
  function load(){
    try {
      const stored=JSON.parse(localStorage.getItem(KEY)||'null');
      if(stored && stored.version===1) {
        history=Array.isArray(stored.history)?stored.history.filter(validSession).slice(0,12):[];
        if(validSession(stored.current) && stored.current.date===dateKey()) {current=stored.current;current.demo=false;mode=current.elapsed>=DURATION?'completed':'paused';}
        if(stored.palette==='spectrum')palette='spectrum';
        showPath=stored.showPath===true;
        showHeat=stored.showHeat!==false;showClicks=stored.showClicks!==false;
      }
    } catch(e) {storageOK=false;}
  }
  function persist(){
    if(current && !current.demo && current.samples.length) {
      history=history.filter(s=>s.id!==current.id);
      history.unshift({...current,samples:current.samples.slice(),clicks:current.clicks?.slice()||[]});history=history.slice(0,12);
    }
    try {localStorage.setItem(KEY,JSON.stringify({version:1,history,current:current&&!current.demo?current:null,palette,showPath,showHeat,showClicks}));storageOK=true;}
    catch(e) {if(storageOK)toast('浏览器存储不可用，请保存图片以保留这张地图。');storageOK=false;}
    $('storageStatus').textContent=storageOK?'本地记录 · 无需登录':'存储不可用 · 请保存图片';updateHistoryCount();
  }
  function newSession(){return {id:typeof crypto.randomUUID==='function'?crypto.randomUUID():String(Date.now())+'-'+Math.random().toString(16).slice(2),date:dateKey(),startedAt:new Date().toISOString(),elapsed:0,width:map.clientWidth,height:map.clientHeight,samples:[],clicks:[],demo:false};}
  function updateHistoryCount(){const count=history.filter(s=>s.date===dateKey()).length;$('historyCount').textContent=`${count} 次探索`;}
  function resize(){
    dpr=Math.min(window.devicePixelRatio||1,2);
    const width=map.clientWidth,height=map.clientHeight;
    for(const canvas of [heat,trail]){canvas.width=Math.round(width*dpr);canvas.height=Math.round(height*dpr);}
    field=new HeatField(width,height);scratch.width=field.width;scratch.height=field.height;
    if(current)field.rebuild(current.samples,reviewUntil);
    pointer=null;broken=true;dirty=true;draw();
  }
  function drawPath(ctx,width,height,until=Infinity,force=false){
    if(!current)return;
    ctx.save();ctx.lineWidth=.85;ctx.lineJoin='round';ctx.lineCap='round';
    if(showPath||force) {
      const groups=new Map(),segments=pathSegments(current.samples,until);
      for(const s of segments){if(!groups.has(s.minute))groups.set(s.minute,[]);groups.get(s.minute).push(s);}
      ctx.globalAlpha=.68;
      for(const [minute,segments] of groups) {
        ctx.strokeStyle=TRAIL_COLORS[minute%TRAIL_COLORS.length];ctx.beginPath();
        for(const s of segments){ctx.moveTo(s.a[0]*width,s.a[1]*height);ctx.lineTo(s.b[0]*width,s.b[1]*height);}ctx.stroke();
      }
      ctx.globalAlpha=1;
      const first=current.samples.find(s=>s[2]<=until),last=segments.at(-1);
      if(first){ctx.fillStyle=TRAIL_COLORS[minuteAt(first[2])%TRAIL_COLORS.length];ctx.beginPath();ctx.arc(first[0]*width,first[1]*height,2.5,0,Math.PI*2);ctx.fill();}
      if(last){ctx.strokeStyle=TRAIL_COLORS[last.minute%TRAIL_COLORS.length];ctx.beginPath();ctx.arc(last.b[0]*width,last.b[1]*height,4,0,Math.PI*2);ctx.stroke();}
    }
    if(showClicks)for(const c of clusterClicks(current.clicks,current.width,current.height,until).sort((a,b)=>a.count-b.count)) {
      const x=c.x*width,y=c.y*height,r=c.count===1?2.2:Math.min(23,5+Math.sqrt(c.count)*2.2),color=TRAIL_COLORS[minuteAt(c.ms)%TRAIL_COLORS.length];
      if(c.count>1){ctx.globalAlpha=.07;ctx.fillStyle=color;ctx.beginPath();ctx.arc(x,y,r+5,0,Math.PI*2);ctx.fill();}
      ctx.globalAlpha=c.count===1?.6:.92;ctx.fillStyle='#0b0e12';ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fill();
      ctx.strokeStyle=color;ctx.lineWidth=1;ctx.stroke();ctx.globalAlpha=1;
      if(c.count>1){ctx.fillStyle=color;ctx.font='10px sans-serif';ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText(c.count>99?'99+':String(c.count),x,y+.5);}
    }
    ctx.restore();
  }
  function drawLegend(ctx,x,y,until=Infinity,scale=1){
    if(!current)return;
    ctx.save();ctx.font=`${10*scale}px sans-serif`;ctx.lineWidth=2*scale;
    if(showPath)for(let i=0;i<Math.max(1,Math.ceil(Math.min(until,current.elapsed)/60000));i++) {
      ctx.strokeStyle=TRAIL_COLORS[i%TRAIL_COLORS.length];ctx.beginPath();ctx.moveTo(x,y);ctx.lineTo(x+15*scale,y);ctx.stroke();
      ctx.fillStyle='#95a3ad';ctx.fillText(`${i+1} min`,x+22*scale,y+3*scale);x+=70*scale;
    }
    ctx.restore();
  }
  function updateMapLegend(){
    const legend=$('minuteLegend');legend.replaceChildren();legend.hidden=!current||(!showPath&&!showClicks);
    if(!current)return;
    if(showPath)for(let i=0;i<Math.max(1,Math.ceil(Math.min(reviewUntil,current.elapsed)/60000));i++) {
      const item=document.createElement('span'),swatch=document.createElement('i');swatch.style.background=TRAIL_COLORS[i%TRAIL_COLORS.length];item.append(swatch,document.createTextNode(`${i+1} min`));legend.append(item);
    }
    if(showClicks){const hint=document.createElement('small');hint.textContent='◎ 点击聚合 · 数字为次数';legend.append(hint);}
  }
  function draw(){
    const image=new ImageData(field.pixels(palette),field.width,field.height);sctx.putImageData(image,0,0);
    hctx.setTransform(1,0,0,1,0,0);hctx.clearRect(0,0,heat.width,heat.height);hctx.imageSmoothingEnabled=true;if(showHeat)hctx.drawImage(scratch,0,0,heat.width,heat.height);heat.style.opacity=showPath?'.24':'.95';
    tctx.setTransform(dpr,0,0,dpr,0,0);tctx.clearRect(0,0,map.clientWidth,map.clientHeight);drawPath(tctx,map.clientWidth,map.clientHeight,reviewUntil);updateMapLegend();dirty=false;
  }
  function updateStats(){
    const stats=current?summarize(current.samples,current.width,current.height):{distance:0,coverage:0,longestDwell:0};
    $('distance').innerHTML=stats.distance>=10000?`${(stats.distance/1000).toFixed(1)} <small>kpx</small>`:`${Math.round(stats.distance).toLocaleString()} <small>px</small>`;
    $('coverage').innerHTML=`${stats.coverage.toFixed(1)}<small>%</small>`;
    $('dwell').innerHTML=`${stats.longestDwell.toFixed(1)} <small>s</small>`;
    return stats;
  }
  function updateTimer(){
    const elapsed=current?.elapsed||0;
    $('timerText').textContent=mode==='completed'?'05:00':mode==='demo'?clockString(elapsed):timeString(DURATION-elapsed);
    $('progressRing').style.strokeDashoffset=String(CIRCUMFERENCE*(1-elapsed/DURATION));
  }
  function updateUI(){
    map.className=mode;
    if(current?.samples.length)map.classList.add('has-data');
    const hasData=!!current?.samples.length;
    const messages={idle:'一段属于你的五分钟',running:'正在记录 · 跟随你的好奇心',paused:'记录已暂停',completed:'五分钟，留成了一张地图',reviewing:'正在回看 · 你的注意力地图',demo:'示例地图 · 非真实记录'};
    $('statusText').textContent=messages[mode];
    $('startText').textContent=mode==='running'?'暂停一下':mode==='paused'?'继续探索':mode==='completed'||mode==='reviewing'?'再探索一次':mode==='demo'?'开始我的探索':'开始探索';
    $('startIcon').innerHTML=mode==='running'?'<path d="M5 4h3v12H5Zm7 0h3v12h-3Z"/>':'<path d="m7 4 9 6-9 6Z"/>';
    $('timerCaption').textContent=mode==='completed'?'这五分钟，属于你':mode==='running'?'探索倒计时':mode==='paused'?'暂停了，余温还在':mode==='demo'?'示例 · 五分钟的游走':'留给自己的时间';
    $('panelDescription').innerHTML=mode==='completed'?'你走过的地方，已经被记住。<br>回看轨迹，或带走这张地图。':mode==='paused'?'不用着急，你的轨迹还在。<br>准备好了，再继续。':mode==='reviewing'?'沿着时间轴，再走一次。<br>看看自己在哪里多停了一会儿。':mode==='demo'?'这是一段模拟的鼠标轨迹。<br>下一张，会是你的样子。':'随意移动，或者停留片刻。<br>让好奇心，带你走一走。';
    $('sessionLabel').textContent=mode==='completed'?'你留下的注意力地图':mode==='demo'?'先感受一下，地图的温度':'一次小小的探索';
    $('saveButton').disabled=!hasData;$('resetButton').disabled=!current;
    $('replayBar').hidden=!hasData||mode==='running';
    $('replaySlider').max=String(current?.elapsed||1);$('replayTotal').textContent=clockString(current?.elapsed||0);
    if(!replaying && reviewUntil===Infinity){$('replaySlider').value=String(current?.elapsed||0);$('replayTime').textContent=clockString(current?.elapsed||0);}
    $('pathToggle').setAttribute('aria-checked',String(showPath));
    $('heatToggle').setAttribute('aria-checked',String(showHeat));$('clickToggle').setAttribute('aria-checked',String(showClicks));updateMapLegend();
    $('paletteName').textContent=palette==='ember'?'余温':'光谱';$('heatScale').classList.toggle('spectrum',palette==='spectrum');
    $('liveMessage').textContent=mode==='demo'?'示例轨迹 · 点击「开始我的探索」，留下自己的地图。':mode==='completed'?'你的五分钟已经完成。现在，回头看一看。':mode==='paused'?'轨迹已保留，随时可以继续。':mode==='reviewing'?'沿着时间，再走一遍刚才的路。':'慢一点也没关系，停留也是一种轨迹。';
    updateTimer();updateStats();
  }
  function stopReplay(){replaying=false;cancelAnimationFrame(replayFrame);$('replayButton').setAttribute('aria-label','播放轨迹');$('replayButton').innerHTML='<svg viewBox="0 0 24 24"><path d="m8 5 11 7-11 7Z"/></svg>';}
  function showCompleteMap(){stopReplay();reviewUntil=Infinity;if(current)field.rebuild(current.samples);dirty=true;updateUI();}
  function start(){
    stopReplay();reviewUntil=Infinity;clearTimeout(toastTimeout);$('toast').classList.remove('visible');
    if(!current||['completed','reviewing','demo'].includes(mode)){if(current&&!current.demo)persist();current=newSession();field.clear();previous=null;}
    else {field.rebuild(current.samples);previous=null;}
    mode='running';lastTick=performance.now();sampleTime=lastTick;lastSave=lastTick;broken=true;pointer=null;dirty=true;updateUI();
  }
  function advance(now){if(mode==='running'){current.elapsed=Math.min(DURATION,current.elapsed+Math.max(0,now-lastTick));lastTick=now;}}
  function pause(reason){if(mode!=='running')return;advance(performance.now());mode=current.elapsed>=DURATION?'completed':'paused';pointer=null;broken=true;$('pointer').classList.remove('visible');persist();updateUI();if(reason)toast(reason);}
  function complete(){mode='completed';pointer=null;broken=true;persist();updateUI();toast('五分钟到了。你的注意力地图已经留在今日记录中。');}
  function sample(now){
    if(!pointer){broken=true;previous=null;sampleTime=now;return;}
    const dt=Math.min(.1,Math.max(0,(now-sampleTime)/1000));sampleTime=now;
    const s=[Math.round(pointer.x*100000)/100000,Math.round(pointer.y*100000)/100000,Math.round(current.elapsed),Math.round(dt*1000)/1000,broken?1:0];
    current.samples.push(s);field.add(s,previous);previous=s;broken=false;dirty=true;
    if(!map.classList.contains('has-data'))map.classList.add('has-data');
  }
  function tick(now){
    if(mode==='running') {
      advance(now);
      if(now-sampleTime>=50)sample(now);
      if(current.elapsed>=DURATION)complete();
      if(now-lastStats>400){updateTimer();updateStats();lastStats=now;}
      if(now-lastSave>5000){persist();lastSave=now;}
    }
    if(dirty&&now-lastRender>65){draw();lastRender=now;}
    requestAnimationFrame(tick);
  }
  function handlePointer(event){
    const target=event.target;
    if(target.closest('[data-ui],dialog')||mode!=='running'){pointer=null;broken=true;$('pointer').classList.remove('visible');return;}
    const rect=map.getBoundingClientRect(),x=(event.clientX-rect.left)/rect.width,y=(event.clientY-rect.top)/rect.height;
    if(x<0||x>1||y<0||y>1)return;
    pointer={x,y};$('pointer').style.left=event.clientX-rect.left+'px';$('pointer').style.top=event.clientY-rect.top+'px';$('pointer').classList.add('visible');
    $('coordinates').textContent=`X ${String(Math.round(x*rect.width)).padStart(4,'0')}  /  Y ${String(Math.round(y*rect.height)).padStart(4,'0')}`;
  }
  function recordClick(event){
    handlePointer(event);
    if(mode!=='running'||!pointer||event.button<0||event.button>2)return;
    advance(performance.now());if(current.elapsed>=DURATION){complete();return;}
    current.clicks??=[];current.clicks.push([pointer.x,pointer.y,Math.floor(current.elapsed),event.button]);
    sample(performance.now());dirty=true;
  }
  function reset(){pause();stopReplay();persist();current=null;previous=null;pointer=null;mode='idle';reviewUntil=Infinity;field.clear();dirty=true;updateUI();}
  function seek(ms){stopReplay();reviewUntil=Number(ms);field.rebuild(current.samples,reviewUntil);dirty=true;$('replayTime').textContent=clockString(reviewUntil);}
  function playReplay(){
    if(!current?.samples.length)return;
    if(replaying){stopReplay();return;}
    showPath=true;$('pathToggle').setAttribute('aria-checked','true');
    replayPosition=Number($('replaySlider').value);if(replayPosition>=current.elapsed)replayPosition=0;
    field.clear();reviewUntil=replayPosition;field.rebuild(current.samples,reviewUntil);replayPrevious=performance.now();replaying=true;
    $('replayButton').setAttribute('aria-label','暂停回放');$('replayButton').innerHTML='<svg viewBox="0 0 24 24"><path d="M8 5v14m8-14v14"/></svg>';
    function frame(now){
      if(!replaying)return;
      const old=reviewUntil;replayPosition=Math.min(current.elapsed,replayPosition+(now-replayPrevious)*10);replayPrevious=now;reviewUntil=replayPosition;
      let prior=null;
      for(const s of current.samples){if(s[2]>reviewUntil)break;if(s[2]>old)field.add(s,prior);prior=s;}
      $('replaySlider').value=String(reviewUntil);$('replayTime').textContent=clockString(reviewUntil);dirty=true;
      if(replayPosition>=current.elapsed){stopReplay();return;}replayFrame=requestAnimationFrame(frame);
    }
    replayFrame=requestAnimationFrame(frame);
  }
  function demo(){
    if(mode==='running')pause();if(current&&!current.demo)persist();stopReplay();current=newSession();current.demo=true;current.elapsed=DURATION;
    let prev=null;
    for(let t=0;t<=DURATION;t+=50){
      const q=t/DURATION;let x,y;
      if(q<.22){const a=q/.22;x=.16+.34*a;y=.37+.14*Math.sin(a*Math.PI*2);}
      else if(q<.4){const a=(q-.22)/.18;x=.5+.055*Math.cos(a*Math.PI*4);y=.37+.07*Math.sin(a*Math.PI*4);}
      else if(q<.65){const a=(q-.4)/.25;x=.5-.21*a;y=.37+.3*a+.04*Math.sin(a*Math.PI*4);}
      else if(q<.81){const a=(q-.65)/.16;x=.29+.02*Math.cos(a*Math.PI*5);y=.67+.025*Math.sin(a*Math.PI*5);}
      else{const a=(q-.81)/.19;x=.29+.26*a;y=.67-.08*Math.sin(a*Math.PI);}
      const s=[x,y,t,.05,t===0?1:0];current.samples.push(s);prev=s;
    }
    for(const [cx,cy,start,count] of [[.51,.38,70000,6],[.29,.67,202000,14],[.54,.66,287000,4]])for(let i=0;i<count;i++)current.clicks.push([cx+Math.sin(i*2.4)*.003,cy+Math.cos(i*2.4)*.003,start+i*350,0]);
    mode='demo';reviewUntil=Infinity;field.rebuild(current.samples);showPath=true;dirty=true;updateUI();toast('正在展示模拟轨迹，不会计入你的今日记录。');
  }
  function exportImage(){
    if(!current?.samples.length)return;
    const scale=Math.min(1,2560/current.width,2560/current.height),width=Math.round(current.width*scale),height=Math.round(current.height*scale);
    const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;const ctx=canvas.getContext('2d');
    ctx.fillStyle='#0b0e12';ctx.fillRect(0,0,width,height);
    ctx.fillStyle='#1b252e';for(let x=20;x<width;x+=26*scale)for(let y=20;y<height;y+=26*scale){ctx.beginPath();ctx.arc(x,y,.6*scale,0,Math.PI*2);ctx.fill();}
    const exportField=new HeatField(width,height);exportField.rebuild(current.samples);
    const tmp=document.createElement('canvas');tmp.width=exportField.width;tmp.height=exportField.height;tmp.getContext('2d').putImageData(new ImageData(exportField.pixels(palette),exportField.width,exportField.height),0,0);ctx.globalAlpha=showPath?.24:1;if(showHeat)ctx.drawImage(tmp,0,0,width,height);ctx.globalAlpha=1;
    drawPath(ctx,width,height,Infinity);
    const padding=34*scale;ctx.fillStyle='#0b0e12d9';ctx.fillRect(0,0,width,90*scale);ctx.fillRect(0,height-70*scale,width,70*scale);
    ctx.fillStyle='#e8b47e';ctx.font=`${18*scale}px sans-serif`;ctx.fillText('注意力地图  /  ATTENTION ATLAS',padding,43*scale);
    ctx.fillStyle='#748794';ctx.font=`${10*scale}px monospace`;ctx.fillText(`${current.date} · ${clockString(current.elapsed)}${current.demo?' · DEMO / 模拟数据':''}`,padding,65*scale);
    drawLegend(ctx,padding,81*scale,Infinity,scale);
    const stats=summarize(current.samples,current.width,current.height);ctx.fillStyle='#a0acb5';ctx.font=`${11*scale}px sans-serif`;ctx.fillText(`游走 ${Math.round(stats.distance).toLocaleString()} px   /   探索 ${stats.coverage.toFixed(1)}%   /   最长停留 ${stats.longestDwell.toFixed(1)} s`,padding,height-37*scale);
    ctx.fillStyle='#566a78';ctx.font=`${9*scale}px sans-serif`;ctx.fillText('鼠标轨迹 ≠ 视线 · 仅记录当前页面',padding,height-18*scale);
    canvas.toBlob(blob=>{if(!blob){toast('图片生成失败，请稍后再试。');return;}const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=`注意力地图-${current.date}-${current.id.slice(0,6)}${current.demo?'-示例':''}.png`;a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);toast('地图已生成，正在保存 PNG 图片。');},'image/png');
  }
  function openDialog(id){if(mode==='running')pause();stopReplay();$(id).showModal();}
  function openHistory(){
    if(mode==='running')pause();persist();const list=$('historyList');list.replaceChildren();
    const today=history.filter(s=>s.date===dateKey());
    if(!today.length){const p=document.createElement('p');p.className='empty-history';p.textContent='今天的地图还是空白的。\n开始一次探索，让注意力留下形状。';list.append(p);}
    for(const s of today){
      const button=document.createElement('button');button.className='history-item';
      const thumb=document.createElement('canvas');thumb.width=150;thumb.height=96;const f=new HeatField(150,96);f.rebuild(s.samples);const tiny=document.createElement('canvas');tiny.width=f.width;tiny.height=f.height;tiny.getContext('2d').putImageData(new ImageData(f.pixels(palette),f.width,f.height),0,0);thumb.getContext('2d').drawImage(tiny,0,0,150,96);
      const words=document.createElement('span'),title=document.createElement('strong'),details=document.createElement('small');
      const date=new Date(s.startedAt);title.textContent=`${Number.isNaN(date.getTime())?'今日':date.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false})} 的探索`;
      const stats=summarize(s.samples,s.width,s.height);details.textContent=`${clockString(s.elapsed)} · 探索 ${stats.coverage.toFixed(1)}% · ${s.elapsed>=DURATION?'已完成':'已保存'}`;
      words.append(title,details);const arrow=document.createElement('span');arrow.textContent='↗';button.append(thumb,words,arrow);button.addEventListener('click',()=>{stopReplay();current=structuredClone(s);mode='reviewing';reviewUntil=Infinity;field.rebuild(current.samples);previous=null;pointer=null;dirty=true;$('historyDialog').close();updateUI();});list.append(button);
    }
    openDialog('historyDialog');
  }
  async function fullscreen(){try{if(document.fullscreenElement)await document.exitFullscreen();else await document.documentElement.requestFullscreen();}catch(e){toast('当前浏览器不支持全屏，可使用浏览器的全屏功能。');}}
  $('startButton').addEventListener('click',()=>mode==='running'?pause():start());
  $('demoButton').addEventListener('click',demo);$('saveButton').addEventListener('click',()=>{pause();exportImage();});
  $('resetButton').addEventListener('click',()=>{pause();openDialog('resetDialog');});
  $('confirmResetButton').addEventListener('click',()=>{$('resetDialog').close();reset();start();});
  $('helpButton').addEventListener('click',()=>openDialog('helpDialog'));$('historyButton').addEventListener('click',openHistory);
  $('fullscreenButton').addEventListener('click',fullscreen);
  $('pathToggle').addEventListener('click',()=>{showPath=!showPath;$('pathToggle').setAttribute('aria-checked',String(showPath));dirty=true;persist();});
  $('heatToggle').addEventListener('click',()=>{showHeat=!showHeat;dirty=true;updateUI();persist();});
  $('clickToggle').addEventListener('click',()=>{showClicks=!showClicks;dirty=true;updateUI();persist();});
  $('paletteButton').addEventListener('click',()=>{palette=palette==='ember'?'spectrum':'ember';dirty=true;updateUI();persist();});
  $('replaySlider').addEventListener('input',event=>seek(event.target.value));$('replayButton').addEventListener('click',playReplay);$('returnLiveButton').addEventListener('click',showCompleteMap);
  document.querySelectorAll('[data-close]').forEach(button=>button.addEventListener('click',()=>$(button.dataset.close).close()));
  document.querySelectorAll('dialog').forEach(dialog=>dialog.addEventListener('click',event=>{const r=dialog.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)dialog.close();}));
  map.addEventListener('pointermove',handlePointer);map.addEventListener('pointerdown',recordClick);
  map.addEventListener('pointerleave',()=>{pointer=null;broken=true;previous=null;$('pointer').classList.remove('visible');});
  map.addEventListener('pointerup',event=>{if(event.pointerType!=='mouse'){pointer=null;broken=true;}});
  document.addEventListener('keydown',event=>{if(event.repeat||event.ctrlKey||event.metaKey||event.altKey||document.querySelector('dialog[open]')||/INPUT|BUTTON|A/.test(event.target.tagName))return;if(event.code==='Space'){event.preventDefault();mode==='running'?pause():start();}else if(event.key.toLowerCase()==='f')fullscreen();});
  document.addEventListener('visibilitychange',()=>{if(document.hidden){pause();stopReplay();}});
  window.addEventListener('blur',()=>{pointer=null;broken=true;previous=null;});
  window.addEventListener('pagehide',()=>{pause();persist();});window.addEventListener('resize',resize);
  document.addEventListener('fullscreenchange',()=>{$('fullscreenButton').setAttribute('aria-label',document.fullscreenElement?'退出全屏':'进入全屏');});
  $('today').textContent=dateKey().replaceAll('-',' . ');
  load();resize();updateHistoryCount();updateUI();requestAnimationFrame(tick);
  if(!storageOK){$('storageStatus').textContent='存储不可用 · 请保存图片';toast('无法读取本地记录。你仍可以探索并导出图片。');}
  if(current)toast('上次的轨迹还在，继续探索或回看都可以。');
})();
