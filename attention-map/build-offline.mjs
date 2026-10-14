import {readFile,writeFile} from 'node:fs/promises';
const base=new URL('./dist/',import.meta.url);
let html=await readFile(new URL('index.html',base),'utf8');
const css=await readFile(new URL('styles.css',base),'utf8');
html=html.replace('<link rel="stylesheet" href="./styles.css">',`<style>\n${css}\n</style>`);
for(const name of ['engine.js','app.js']) {
  const js=await readFile(new URL(name,base),'utf8');
  html=html.replace(`<script src="./${name}" defer></script>`,'');
  html=html.replace('</body>',`<script>\n${js.replace(/<\/script/gi,'<\\/script')}\n</script>\n</body>`);
}
const icon=await readFile(new URL('favicon.svg',base),'utf8');
html=html.replace('href="./favicon.svg"',`href="data:image/svg+xml,${encodeURIComponent(icon)}"`);
await writeFile(new URL('../注意力地图-离线版.html',import.meta.url),html,'utf8');
console.log('已更新 注意力地图-离线版.html');
