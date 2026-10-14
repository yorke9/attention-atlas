/* Normalized coordinates keep a map aligned when the viewport changes. */
(function (global) {
  'use strict';
  const DURATION = 300000;
  const TRAIL_COLORS=['#76cad5','#92acee','#c19bdb','#eaaa8e','#ddc782','#8dcbaa'];
  for(let i=6;i<60;i++)TRAIL_COLORS.push(`hsl(${((183+i*137.508)%360).toFixed(3)} 52% 68%)`);
  const minuteAt=ms=>Math.floor(Math.max(0,ms)/60000);
  function pathSegments(samples,until=Infinity) {
    const segments=[];
    for(let i=1;i<samples.length;i++) {
      const a=samples[i-1],b=samples[i];
      if(a[2]>until)break;
      if(b[4] || (a[0]===b[0]&&a[1]===b[1]))continue;
      const end=Math.min(b[2],until);
      if(b[2]===a[2]) {segments.push({a:a.slice(0,2),b:b.slice(0,2),minute:minuteAt(a[2])});continue;}
      for(let start=a[2];start<end;) {
        const next=Math.min(end,(minuteAt(start)+1)*60000),f0=(start-a[2])/(b[2]-a[2]),f1=(next-a[2])/(b[2]-a[2]);
        segments.push({a:[a[0]+(b[0]-a[0])*f0,a[1]+(b[1]-a[1])*f0],b:[a[0]+(b[0]-a[0])*f1,a[1]+(b[1]-a[1])*f1],minute:minuteAt(start)});
        start=next;
      }
    }
    return segments;
  }
  function clusterClicks(clicks=[],width,height,until=Infinity) {
    const clusters=[],cells=new Map(),radius=24;
    for(const p of clicks) {
      if(p[2]>until)break;
      const x=p[0]*width,y=p[1]*height,cx=Math.floor(x/radius),cy=Math.floor(y/radius);
      let nearest=null,best=radius*radius+.001;
      for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++) {
        for(const c of cells.get(`${cx+dx},${cy+dy}`)||[]) {
          const d=(x-c.anchorX)**2+(y-c.anchorY)**2;
          if(d<best){best=d;nearest=c;}
        }
      }
      if(!nearest) {
        nearest={x:p[0],y:p[1],anchorX:x,anchorY:y,count:0,ms:p[2]};clusters.push(nearest);
        const key=`${cx},${cy}`;if(!cells.has(key))cells.set(key,[]);cells.get(key).push(nearest);
      }
      nearest.count++;nearest.x+=(p[0]-nearest.x)/nearest.count;nearest.y+=(p[1]-nearest.y)/nearest.count;nearest.ms=p[2];
    }
    return clusters;
  }
  const PALETTES = {
    ember: [[69,53,38],[126,83,47],[206,137,65],[244,176,89],[237,100,68],[255,222,161]],
    spectrum: [[36,48,84],[43,93,133],[49,159,170],[85,197,159],[236,193,94],[247,104,83]]
  };
  class HeatField {
    constructor(width, height) { this.resize(width, height); }
    resize(width, height) {
      this.viewWidth = Math.max(1, width); this.viewHeight = Math.max(1, height);
      this.scale = Math.min(1, 380 / this.viewWidth);
      this.width = Math.max(1, Math.round(this.viewWidth * this.scale));
      this.height = Math.max(1, Math.round(this.viewHeight * this.scale));
      this.values = new Float32Array(this.width * this.height);
      this.radius = Math.max(4, Math.round(38 * this.scale));
      this.kernel = [];
      for (let y = -this.radius; y <= this.radius; y++) for (let x = -this.radius; x <= this.radius; x++) {
        const d = (x*x+y*y)/(this.radius*this.radius);
        if (d <= 1) this.kernel.push([x,y,Math.exp(-d*3.7) - Math.exp(-3.7)]);
      }
    }
    clear() { this.values.fill(0); }
    deposit(x, y, strength) {
      const px = Math.round(x * (this.width-1)), py = Math.round(y * (this.height-1));
      for (const [dx,dy,k] of this.kernel) {
        const xx=px+dx, yy=py+dy;
        if(xx>=0 && xx<this.width && yy>=0 && yy<this.height) this.values[yy*this.width+xx] += k*strength;
      }
    }
    add(sample, previous) {
      const [x,y,,dt,broken] = sample;
      const connected = previous && !broken;
      const length = connected ? Math.hypot((x-previous[0])*this.viewWidth,(y-previous[1])*this.viewHeight) : 0;
      const steps = Math.max(1, Math.ceil(length/8));
      const strength = Math.min(.1, Math.max(0,dt))*10/steps;
      for (let j=1;j<=steps;j++) {
        const f=j/steps;
        this.deposit(connected ? previous[0]+(x-previous[0])*f : x, connected ? previous[1]+(y-previous[1])*f : y, strength);
      }
    }
    rebuild(samples, until=Infinity) {
      this.clear(); let previous=null;
      for(const sample of samples) { if(sample[2]>until)break;this.add(sample,previous);previous=sample; }
    }
    pixels(palette='ember') {
      const colors=PALETTES[palette] || PALETTES.ember;
      const output=new Uint8ClampedArray(this.values.length*4);
      for(let i=0;i<this.values.length;i++) {
        const density=this.values[i]; if(density<.004)continue;
        const intensity=1-Math.exp(-density*.34);
        const p=Math.min(colors.length-1.001,intensity*(colors.length-1));
        const low=Math.floor(p), fraction=p-low, offset=i*4;
        for(let c=0;c<3;c++)output[offset+c]=colors[low][c]*(1-fraction)+colors[low+1][c]*fraction;
        output[offset+3]=Math.min(242,Math.round(Math.pow(intensity,.55)*285));
      }
      return output;
    }
  }
  function summarize(samples, width, height) {
    let distance=0, dwell=0, longestDwell=0, anchor=null, previous=null;
    const cells=new Set(), columns=40, rows=24;
    for(const s of samples) {
      const connected=previous && !s[4];
      const length=connected ? Math.hypot((s[0]-previous[0])*width,(s[1]-previous[1])*height) : 0;
      distance+=length;
      const steps=Math.max(1,Math.ceil(length/12));
      for(let j=1;j<=steps;j++) {
        const x=connected ? previous[0]+(s[0]-previous[0])*j/steps : s[0];
        const y=connected ? previous[1]+(s[1]-previous[1])*j/steps : s[1];
        cells.add(Math.min(columns-1,Math.floor(x*columns))+Math.min(rows-1,Math.floor(y*rows))*columns);
      }
      if(!anchor || s[4] || Math.hypot((s[0]-anchor[0])*width,(s[1]-anchor[1])*height)>24) {anchor=s;dwell=0;}
      dwell+=s[3];longestDwell=Math.max(longestDwell,dwell);previous=s;
    }
    return {distance,coverage:cells.size/(columns*rows)*100,longestDwell};
  }
  function validSession(session) {
    return !!session && typeof session.id==='string' && session.id.length<100 && typeof session.date==='string' &&
      Number.isFinite(session.elapsed) && session.elapsed>=0 && session.elapsed<=DURATION &&
      Number.isFinite(session.width) && session.width>0 && session.width<=20000 &&
      Number.isFinite(session.height) && session.height>0 && session.height<=20000 &&
      (session.clicks===undefined || (Array.isArray(session.clicks) && session.clicks.length<=100000 && session.clicks.every((c,i,all)=>
        Array.isArray(c)&&c.length===4&&Number.isFinite(c[0])&&c[0]>=0&&c[0]<=1&&Number.isFinite(c[1])&&c[1]>=0&&c[1]<=1&&
        Number.isFinite(c[2])&&c[2]>=0&&c[2]<=session.elapsed&&(i===0||c[2]>=all[i-1][2])&&Number.isInteger(c[3])&&c[3]>=0&&c[3]<=2))) &&
      Array.isArray(session.samples) && session.samples.length<=10000 && session.samples.every((s,i,all)=>
        Array.isArray(s) && s.length===5 && Number.isFinite(s[0]) && s[0]>=0 && s[0]<=1 &&
        Number.isFinite(s[1]) && s[1]>=0 && s[1]<=1 && Number.isFinite(s[2]) && s[2]>=0 && s[2]<=DURATION &&
        (i===0 || s[2]>=all[i-1][2]) && Number.isFinite(s[3]) && s[3]>=0 && s[3]<=.101 && (s[4]===0 || s[4]===1));
  }
  global.AttentionEngine={DURATION,HeatField,summarize,validSession,TRAIL_COLORS,minuteAt,pathSegments,clusterClicks};
})(typeof window!=='undefined'?window:globalThis);
