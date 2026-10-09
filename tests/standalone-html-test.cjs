/* node tests/standalone-html-test.cjs [exported.html] [original-assets-directory]
 * Executes the shipping browser script against a deterministic DOM harness, plus
 * audits the actual exported file and compares embedded bytes with originals.
 * This is not a substitute for native browser rendering/playback verification.
 */
'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../src/Stepwise/Core/StandaloneHtmlExporter.cs'),'utf8');
const fixture=process.argv[2]?path.resolve(process.argv[2]):null;
const html=fixture?fs.readFileSync(fixture,'utf8'):null;
const script=html?[...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].at(-1)[1]:source.match(/private const string Script = """\r?\n([\s\S]*?)\r?\n        """;/)[1].split(/\r?\n/).map(line=>line.slice(8)).join('\n');
const output=path.join(__dirname,'../verification/standalone-html');fs.mkdirSync(output,{recursive:true});
const results=[],hash=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
async function test(name,run){try{await run();results.push({name,passed:true});}catch(error){results.push({name,passed:false,error:error.stack});}}
class Element {
  constructor(id=''){this.id=id;this.listeners={};this.dataset={};this.children=[];this.hidden=false;this.disabled=false;this.textContent='';this.src='';this.alt='';this.className='';this.open=true;this.classes=new Set();this.classList={add:key=>this.classes.add(key),remove:key=>this.classes.delete(key),toggle:(key,value)=>value?this.classes.add(key):this.classes.delete(key)};}
  addEventListener(type,callback){(this.listeners[type]??=[]).push(callback);}
  async fire(type,event={}){await Promise.all((this.listeners[type]||[]).map(callback=>callback({target:this,...event})));}
  querySelector(selector){return this.elements?.[selector]??null;}
  append(node){this.children.push(node);}
  remove(){this.removed=true;}
  click(){this.clicked=true;return this.fire('click');}
  showModal(){this.modal=true;}
  close(){this.modal=false;}
  decode(){return Promise.resolve();}
}
function harness({failPart=false}={}){
  const videoBytes=Buffer.alloc(450000);for(let i=0;i<videoBytes.length;i++)videoBytes[i]=i%251;
  const definitions=[{id:'poster',mime:'image/png',bytes:Buffer.from('poster-frame')},{id:'image',mime:'image/png',bytes:Buffer.from('original-image')},{id:'gif',mime:'image/gif',bytes:Buffer.from('animated-gif')},{id:'video',mime:'video/mp4',bytes:videoBytes}];
  const nodes={},entries=[];
  for(const definition of definitions){const chunks=[];for(let i=0;i<definition.bytes.length;i+=192*1024)chunks.push(definition.bytes.subarray(i,i+192*1024));entries.push({id:definition.id,mime:definition.mime,name:definition.id+'.bin',size:definition.bytes.length,parts:chunks.length});chunks.forEach((bytes,i)=>{nodes[definition.id+'-'+i]=Object.assign(new Element(),{textContent:bytes.toString('base64')});});}
  if(failPart)delete nodes['video-1'];
  nodes['asset-map']=Object.assign(new Element(),{textContent:JSON.stringify(entries)});
  for(const id of ['toast','print','lightbox','lightbox-close','lightbox-image'])nodes[id]=new Element(id);
  const contents=new Element('contents'),figures=[];
  for(const kind of ['image','gif','video','video']){const figure=new Element();figure.dataset={media:kind,poster:'poster',kind};figure.elements={};for(const selector of ['.poster','.media-status','.visual','.download',...(kind==='video'?['video','.play']:['.zoom'])])figure.elements[selector]=new Element(selector);if(kind==='video')figure.elements.video.play=async()=>{figure.played=true;};figures.push(figure);}
  const document=new Element('document');document.body=new Element('body');document.getElementById=id=>nodes[id];document.querySelectorAll=()=>figures;document.querySelector=selector=>selector==='.contents'?contents:null;document.createElement=()=>new Element();
  const window=new Element('window');window.matchMedia=()=>({matches:false});window.print=()=>{window.printCalls=(window.printCalls||0)+1;};window.IntersectionObserver=true;
  const blobs=new Map(),revoked=[],decodeCalls=[];let observer,created=0,yields=0;
  const context={document,window,console,Map,Uint8Array,Blob,Promise,
    URL:{createObjectURL:blob=>{const url='blob:test-'+(++created);blobs.set(url,blob);return url;},revokeObjectURL:url=>revoked.push(url)},
    atob:value=>{decodeCalls.push(value.length);return Buffer.from(value,'base64').toString('latin1');},
    setTimeout:(callback,delay)=>{if(delay>10)return 1;yields++;return setTimeout(callback,0);},clearTimeout:()=>{},
    IntersectionObserver:class{constructor(callback){this.callback=callback;this.observed=[];observer=this;}observe(figure){this.observed.push(figure);}unobserve(){} }
  };
  vm.runInNewContext(script,context,{filename:'StandaloneHtmlExporter.shipping.js'});
  return {nodes,figures,document,window,blobs,revoked,decodeCalls,definitions,observer,visible:figure=>observer.callback([{target:figure,isIntersecting:true}]),get yields(){return yields;}};
}
const settle=()=>new Promise(resolve=>setTimeout(resolve,30));
(async()=>{
  await test('Writer uses bounded aligned chunks and never materializes a whole media file',()=>{
    assert.match(source,/ChunkBytes = 192 \* 1024/);assert.match(source,/ReadAtLeastAsync/);assert.match(source,/ToBase64CharArray/);assert.doesNotMatch(source,/ReadAllBytes|ReadAllText|ReadToEnd|Convert\.ToBase64String\(File/);
  });
  await test('Page startup never decodes offscreen images or any video',async()=>{const h=harness();await settle();assert.equal(h.decodeCalls.length,0);assert.equal(h.observer.observed.length,4);assert.ok(h.figures.every(figure=>!figure.elements.video?.src));});
  await test('Visible video loads only its first frame, keeps MP4 untouched',async()=>{const h=harness();h.visible(h.figures[2]);await settle();assert.equal(h.blobs.size,1);assert.equal(await [...h.blobs.values()][0].text(),'poster-frame');assert.equal(h.figures[2].elements.video.src,'');});
  await test('Visible image and GIF load exact original bytes without fetching video',async()=>{const h=harness();h.visible(h.figures[0]);h.visible(h.figures[1]);await settle();const values=await Promise.all([...h.blobs.values()].map(blob=>blob.text()));assert.deepEqual(values.sort(),['animated-gif','original-image','poster-frame']);assert.equal(h.figures[0].elements['.visual'].children.length,1);assert.equal(h.figures[1].elements['.visual'].children.length,1);});
  await test('Concurrent playback shares a single decode and exact bytes, yielding for every chunk',async()=>{const h=harness();await Promise.all([h.figures[2].elements['.play'].fire('click'),h.figures[3].elements['.play'].fire('click')]);assert.equal(h.blobs.size,1);assert.equal(h.figures[2].elements.video.src,h.figures[3].elements.video.src);assert.equal(hash(Buffer.from(await [...h.blobs.values()][0].arrayBuffer())),hash(h.definitions[3].bytes));assert.ok(h.decodeCalls.every(size=>size<=256*1024));assert.equal(h.yields,3);assert.ok(h.figures[2].played&&h.figures[3].played);});
  await test('Saving media reuses the playback blob and filename, no second decode',async()=>{const h=harness();await h.figures[2].elements['.play'].fire('click');const count=h.decodeCalls.length;await h.figures[2].elements['.download'].fire('click');assert.equal(h.decodeCalls.length,count);const anchor=h.document.body.children[0];assert.equal(anchor.download,'video.bin');assert.ok(anchor.clicked&&anchor.removed);assert.equal(h.figures[2].elements['.download'].disabled,false);});
  await test('Print prepares only first frames and never decodes animated payloads',async()=>{const h=harness();await h.nodes.print.fire('click');assert.equal(h.window.printCalls,1);assert.equal(h.blobs.size,1);assert.ok(h.figures.every(figure=>figure.elements['.poster'].src));assert.equal(h.nodes.print.disabled,false);});
  await test('Ctrl+P routes through the ready-first-frame print flow',async()=>{const h=harness();let prevented=false;await h.document.fire('keydown',{ctrlKey:true,key:'p',preventDefault(){prevented=true;}});await settle();assert.ok(prevented);assert.equal(h.window.printCalls,1);assert.equal(h.blobs.size,1);});
  await test('Unavailable embedded chunk restores controls and exposes a recoverable error',async()=>{const h=harness({failPart:true});await h.figures[2].elements['.play'].fire('click');assert.equal(h.figures[2].elements['.play'].disabled,false);assert.equal(h.figures[2].elements['.play'].hidden,false);assert.equal(h.figures[2].elements.video.hidden,true);assert.match(h.figures[2].elements['.media-status'].textContent,/无法播放/);assert.equal(h.blobs.size,0);});
  await test('Page exit releases each shared URL once and preserves back-forward cache',async()=>{const h=harness();await h.figures[2].elements['.play'].fire('click');await h.window.fire('pagehide',{persisted:true});assert.equal(h.revoked.length,0);await h.window.fire('pagehide',{persisted:false});assert.equal(h.revoked.length,1);});
  if(html){
    const assets=JSON.parse(html.match(/<script id="asset-map" type="application\/json">([\s\S]*?)<\/script>/)[1]);
    await test('Actual export CSP matches the executable script and forbids network resources',()=>{const digest=crypto.createHash('sha256').update(script).digest('base64');assert.ok(html.includes("script-src 'sha256-"+digest+"'"));assert.ok(html.includes("default-src 'none'"));if(assets.some(asset=>asset.mime==='video/mp4'))assert.ok(html.includes('preload="none"'));assert.doesNotMatch(html,/(?:src|href)="(?:https?:|file:|[A-Z]:\\)/i);assert.doesNotMatch(html,/<script[^>]*\bsrc=/i);});
    await test('Actual export cannot turn embedded script-like titles into executable markup',()=>{assert.doesNotMatch(html,/<script>window\.injected=true<\/script>/);assert.equal([...html.matchAll(/<script>/g)].length,1);});
    await test('Actual export metadata has no local paths or unnecessary duplicate assets',()=>{assert.equal(new Set(assets.map(asset=>asset.id)).size,assets.length);assert.ok(assets.every(asset=>!Object.hasOwn(asset,'path')&&!/[\\/]/.test(asset.name)));assert.match(html,/@media\(max-width:650px\)/);assert.match(html,/@media print/);});
    const embedded=[];
    await test('Every exported chunk is independently valid base64 and reconstructs declared size',()=>{for(const asset of assets){const parts=[];for(let i=0;i<asset.parts;i++){const match=html.match(new RegExp('<script id="'+asset.id+'-'+i+'" type="application/octet-stream">([^<]*)<\\/script>'));assert.ok(match,asset.id+' part '+i);assert.match(match[1],/^[A-Za-z0-9+/]*={0,2}$/);assert.ok(match[1].length<=256*1024);const bytes=Buffer.from(match[1],'base64');assert.equal(bytes.toString('base64'),match[1]);parts.push(bytes);}const bytes=Buffer.concat(parts);assert.equal(bytes.length,asset.size);embedded.push({id:asset.id,name:asset.name,mime:asset.mime,size:bytes.length,sha256:hash(bytes)});}});
    if(process.argv[3])await test('All embedded originals and first frames match source SHA256 byte for byte',()=>{const sourceDir=path.resolve(process.argv[3]);const files=[];function walk(dir){for(const entry of fs.readdirSync(dir,{withFileTypes:true})){const full=path.join(dir,entry.name);if(entry.isDirectory())walk(full);else files.push(full);}}walk(sourceDir);for(const asset of embedded){const matches=files.filter(file=>path.basename(file)===asset.name);assert.ok(matches.length,asset.name+' source exists');assert.ok(matches.some(file=>hash(fs.readFileSync(file))===asset.sha256),asset.name+' SHA256 matches');}});
    fs.writeFileSync(path.join(output,'embedded-assets.json'),JSON.stringify(embedded,null,2));
  }
  fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({fixture,passed:results.filter(item=>item.passed).length,total:results.length,results},null,2));
  console.log(JSON.stringify({passed:results.filter(item=>item.passed).length,total:results.length,failed:results.filter(item=>!item.passed)},null,2));
  if(results.some(item=>!item.passed))process.exitCode=1;
})();
