const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
(async()=>{
  const output=path.resolve(__dirname,'../../attention-map-desktop/.local/verification');await fs.mkdir(output,{recursive:true});
  const browser=await chromium.launch({channel:'msedge',headless:true});const reports=[];
  try {
    for(const file of ['../dist/index.html','../../注意力地图-离线版.html']) {
      let context=await browser.newContext({viewport:{width:1440,height:900},acceptDownloads:true}),page=await context.newPage();const errors=[];
      page.on('pageerror',e=>errors.push(e.message));
      await page.goto(pathToFileURL(path.resolve(__dirname,file)).href);
      await page.getByRole('button',{name:'开始探索',exact:false}).click();
      await page.mouse.click(300,320);await page.mouse.click(309,325);await page.waitForTimeout(120);
      await page.getByRole('button',{name:'暂停一下',exact:false}).click();
      const saved=await page.evaluate(()=>JSON.parse(localStorage.getItem('attention-atlas-v1')));
      assert.equal(saved.current.clicks.length,2,'UI clicks must be excluded');assert.ok(saved.current.clicks[1][2]>=saved.current.clicks[0][2]);
      await page.reload();assert.equal(await page.locator('#statusText').innerText(),'记录已暂停');
      await context.close();context=await browser.newContext({viewport:{width:1440,height:900},acceptDownloads:true});page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
      await page.goto(pathToFileURL(path.resolve(__dirname,file)).href);
      await page.getByRole('button',{name:'先看一个示例',exact:false}).click();await page.waitForTimeout(180);
      assert.equal(await page.locator('#minuteLegend>span').count(),5);assert.match(await page.locator('#minuteLegend').innerText(),/5 min/);
      const before=await page.locator('#trail').evaluate(c=>c.toDataURL());
      await page.locator('#clickToggle').click();await page.waitForTimeout(180);assert.equal(await page.locator('#clickToggle').getAttribute('aria-checked'),'false');
      assert.notEqual(await page.locator('#trail').evaluate(c=>c.toDataURL()),before);
      await page.locator('#clickToggle').click();await page.locator('#heatToggle').click();await page.waitForTimeout(180);
      assert.equal(await page.locator('#heat').evaluate(c=>c.getContext('2d').getImageData(0,0,c.width,c.height).data.some(v=>v!==0)),false);
      await page.locator('#heatToggle').click();
      await page.locator('#replaySlider').evaluate(e=>{e.value='60000';e.dispatchEvent(new Event('input',{bubbles:true}));});await page.waitForTimeout(180);
      assert.equal(await page.locator('#minuteLegend>span').count(),1);
      await page.locator('#returnLiveButton').click();await page.waitForTimeout(180);
      const name=file.includes('离线')?'offline':'web';
      await page.screenshot({path:path.join(output,`${name}-ui.png`),fullPage:true});
      const downloadPromise=page.waitForEvent('download');await page.locator('#saveButton').click();const download=await downloadPromise;
      const pngPath=path.join(output,`${name}-map.png`);await download.saveAs(pngPath);assert.equal((await fs.readFile(pngPath)).subarray(1,4).toString(),'PNG');
      await page.setViewportSize({width:390,height:844});await page.waitForTimeout(180);
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
      for(const id of ['pathToggle','heatToggle','clickToggle','paletteButton']) {
        const box=await page.locator('#'+id).boundingBox();assert.ok(box&&box.x>=0&&box.x+box.width<=390&&box.y+box.height<=844,`${id} must remain on screen`);
      }
      await page.screenshot({path:path.join(output,`${name}-mobile.png`),fullPage:true});assert.deepEqual(errors,[]);
      reports.push({file,passed:true,checks:['actual click capture excluding controls','save and reload','five minute demo','independent layers','time-limited replay','PNG download','mobile controls','no runtime errors']});
      await context.close();
    }
    await fs.writeFile(path.join(output,'browser-check.json'),JSON.stringify({passed:true,reports},null,2));console.log(JSON.stringify({passed:true,reports},null,2));
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
