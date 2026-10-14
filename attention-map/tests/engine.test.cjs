const {test}=require('node:test');
const assert=require('node:assert/strict');
require('../dist/engine.js');
const {HeatField,summarize,validSession,DURATION}=globalThis.AttentionEngine;
const {pathSegments,clusterClicks,TRAIL_COLORS}=globalThis.AttentionEngine;
test('minute boundaries split connected lines exactly, including replay inside a segment',()=>{
  const samples=[[0,.5,59950,.05,1],[1,.5,60050,.05,0]];
  const lines=pathSegments(samples);assert.equal(lines.length,2);assert.deepEqual(lines.map(s=>s.minute),[0,1]);assert.equal(lines[0].b[0],.5);assert.equal(lines[1].a[0],.5);
  const replay=pathSegments(samples,60025);assert.equal(replay.at(-1).b[0],.75);
  assert.equal(pathSegments(samples,59900).length,0);
  assert.equal(pathSegments([[0,0,299950,.05,1],[1,1,300000,.05,0]])[0].minute,4);
  assert.equal(new Set(TRAIL_COLORS).size,60);
});
test('broken and stationary paths never create phantom connectors',()=>{
  assert.deepEqual(pathSegments([[0,0,50,.05,1],[1,1,100,.05,1]]),[]);
  assert.deepEqual(pathSegments([[.5,.5,50,.05,1],[.5,.5,100,.05,0]]),[]);
});
test('clicks cluster by original pixels across cell borders, never by viewport or time in the future',()=>{
  const clicks=[[.023,.1,100,0],[.025,.1,200,0],[.04,.1,300,2],[.2,.1,400,0]];
  assert.deepEqual(clusterClicks(clicks,1000,1000).map(c=>c.count),[3,1]);
  assert.deepEqual(clusterClicks(clicks,1000,1000,250).map(c=>c.count),[2]);
  assert.equal(clusterClicks(undefined,1000,1000).length,0);
  const chain=[[.01,.1,100,0],[.03,.1,200,0],[.05,.1,300,0]];
  assert.deepEqual(clusterClicks(chain,1000,1000).map(c=>c.count),[2,1]);
});
test('click validation retains old sessions and rejects malformed events',()=>{
  const good={id:'clicks',date:'2026-10-13',width:1000,height:800,elapsed:500,samples:[]};
  assert.equal(validSession(good),true);assert.equal(validSession({...good,clicks:[[.2,.3,100,0],[.21,.31,300,2]]}),true);
  for(const clicks of [null,[[NaN,.3,100,0]],[[.2,.3,600,0]],[[.2,.3,100,3]],[[.2,.3,200,0],[.2,.3,100,0]]])assert.equal(validSession({...good,clicks}),false);
});
test('dwell accumulates density and heat is rebuilt at normalized coordinates',()=>{
  const f=new HeatField(1200,800),p=[.4,.5,50,.05,1];f.add(p,null);const peak1=Math.max(...f.values);
  f.add([.4,.5,100,.05,0],p);assert.ok(Math.max(...f.values)>peak1);
  f.resize(800,600);f.rebuild([p]);const center=f.values[Math.round(.5*(f.height-1))*f.width+Math.round(.4*(f.width-1))];assert.ok(center>0);
});
test('disconnected segments do not create a false line or distance',()=>{
  const samples=[[.1,.1,0,.05,1],[.9,.9,100,.05,1]],f=new HeatField(1000,1000);f.rebuild(samples);
  assert.equal(f.values[Math.round(f.height/2)*f.width+Math.round(f.width/2)],0);
  assert.equal(summarize(samples,1000,1000).distance,0);
});
test('replay excludes samples in the future and clearing removes all density',()=>{
  const f=new HeatField(1000,1000);f.rebuild([[.1,.1,50,.05,1],[.9,.9,150,.05,1]],100);
  assert.equal(f.values[Math.round(.9*(f.height-1))*f.width+Math.round(.9*(f.width-1))],0);
  f.clear();assert.ok(f.values.every(v=>v===0));
});
test('distance, coverage and longest dwell follow real segments',()=>{
  const s=[[.1,.1,50,.05,1],[.1,.1,100,.05,0],[.2,.1,150,.05,0]];
  const stats=summarize(s,1000,1000);assert.equal(stats.distance,100);assert.ok(stats.coverage>0);assert.equal(stats.longestDwell,.1);
});
test('saved session validation rejects corrupt, oversized and unordered data',()=>{
  const good={id:'a',date:'2026-10-13',width:1200,height:800,elapsed:200,samples:[[.5,.5,100,.05,1]]};
  assert.equal(validSession(good),true);
  for(const patch of [{elapsed:DURATION+1},{width:Infinity},{samples:[[2,.5,100,.05,1]]},{samples:[[.5,.5,200,.05,1],[.5,.5,100,.05,0]]}])assert.equal(validSession({...good,...patch}),false);
});
test('both heat palettes have a transparent background and bounded channels',()=>{
  const f=new HeatField(100,100);assert.ok(f.pixels().every(v=>v===0));f.add([.5,.5,50,.05,1],null);
  for(const name of ['ember','spectrum']){const pixels=f.pixels(name);assert.equal(pixels.length,f.width*f.height*4);assert.ok(pixels.some(v=>v>0));}
});
