/* Run: node tests/preferences-web-test.cjs [output-directory]
 * Executes the shipping WebView scripts against a minimal DOM/bridge harness.
 * Native disk persistence and real browser rendering are verified separately.
 */
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const web=path.join(__dirname,'../src/Stepwise/Web');
const output=path.resolve(process.argv[2]||path.join(__dirname,'../verification/preferences-web'));
const results=[];
class Element {
  constructor(id=''){this.id=id;this.dataset={};this.style={};this.listeners={};this.attributes={};this.children=[];this.value='';this.hidden=false;this.disabled=false;this.checked=false;this.lastChild={textContent:''};this.clientWidth=1200;this.clientHeight=760;this.options=[];const classes=new Set();this.classList={add:value=>classes.add(value),remove:value=>classes.delete(value),contains:value=>classes.has(value),toggle:(value,force)=>{const on=force??!classes.has(value);on?classes.add(value):classes.delete(value);return on;}};}
  addEventListener(type,callback){(this.listeners[type]??=[]).push(callback);}
  async fire(type,event={}){for(const callback of this.listeners[type]||[])await callback({target:this,preventDefault(){},stopPropagation(){},...event});}
  setAttribute(key,value){this.attributes[key]=value;}
  hasAttribute(key){return Object.hasOwn(this.attributes,key);}
  getAttribute(key){return this.attributes[key];}
  closest(){return null;}
  focus(){this.owner.activeElement=this;}
  select(){}
  replaceChildren(...children){this.children=children;}
  append(...children){this.children.push(...children);}
  getBoundingClientRect(){return {left:0,top:0,width:this.clientWidth,height:this.clientHeight};}
  hasPointerCapture(){return false;}
  setPointerCapture(){}
  releasePointerCapture(){}
  getContext(){return {measureText:text=>({width:String(text).length*16})};}
}
function harness(script){
  const html=fs.readFileSync(path.join(web,script.replace('.js','.html')),'utf8');
  const elements=Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(match=>[match[1],new Element(match[1])]));
  const tools=['select','rect','ellipse','pen','arrow','text'].map(tool=>Object.assign(new Element(),{dataset:{tool}}));
  const colors=['#f04d4d','#ffc644','#36c991','#4e9cff','#ffffff','#20272d'].map(color=>Object.assign(new Element(),{dataset:{color}}));
  const formats=['mp4','gif'].map(format=>Object.assign(new Element(),{dataset:{format}}));
  const document=new Element('document');document.body=new Element('body');document.scrollingElement={scrollTop:0};document.fonts={load:async()=>[{}],check:()=>true,ready:Promise.resolve()};
  document.getElementById=id=>elements[id]||(()=>{throw new Error(`Missing real HTML element ${id}`);})();
  document.querySelectorAll=selector=>selector==='[data-tool]'?tools:selector==='[data-color]'?colors:selector==='[data-format]'?formats:selector==='button, input, textarea'?Object.values(elements):[];
  document.querySelector=()=>null;document.createElement=()=>Object.assign(new Element(),{owner:document});
  [...Object.values(elements),...tools,...colors,...formats,document.body].forEach(element=>element.owner=document);
  if(elements.color){elements.color.value='#f04d4d';elements['line-width'].value='5';elements['line-width'].options=[2,3,5,8,12,18].map(value=>({value:String(value)}));elements['font-size'].value='28';elements['font-size'].options=[16,20,24,28,36,48,64,96].map(value=>({value:String(value)}));}
  const messages=[],timers=new Map();let nextTimer=0,resize=null,engine=null;
  const window=new Element('window');window.chrome={webview:{postMessage:message=>messages.push(JSON.parse(JSON.stringify(message))),addEventListener(){}}};
  const context={window,document,location:{hostname:'app.stepwise.local',search:''},URL,URLSearchParams,console,HTMLInputElement:class{},HTMLTextAreaElement:class{},HTMLSelectElement:class{},requestAnimationFrame:()=>1,
    setTimeout:callback=>{timers.set(++nextTimer,callback);return nextTimer;},clearTimeout:id=>timers.delete(id),
    ResizeObserver:class{constructor(callback){resize=callback;}observe(){}},
    Image:class{constructor(){this.naturalWidth=960;this.naturalHeight=540;}set src(value){queueMicrotask(()=>this.onload());}},
    AnnotationEngine:class{constructor(options){Object.assign(this,options);engine=this;}select(id){this.selectedId=id;}cancelGesture(){}hitTest(){return {id:'ink-layer'};}flattenPNG(){return 'data:image/png;base64,example';}serialize(){return {layers:[]};}}
  };
  vm.runInNewContext(fs.readFileSync(path.join(web,script),'utf8'),context,{filename:script});
  return {elements,tools,colors,formats,document,window,messages,timers,resize:()=>resize?.(),engine:()=>engine,preferences:()=>messages.filter(message=>/Preferences$/.test(message.type)),runTimers(){const callbacks=[...timers.values()];timers.clear();callbacks.forEach(callback=>callback());}};
}
const settle=()=>new Promise(resolve=>setImmediate(resolve));
async function annotation(preferences){const h=harness('annotation.js');h.window.stepwiseAnnotation.receive({type:'init',imageUrl:'https://capture.stepwise.local/image.png',preferences});await settle();return h;}
function editor(h,step,recordingDefaults){h.window.stepwise.receive({type:'init',mode:'editor',project:{title:'测试项目',steps:[step]},step,recordingDefaults});}
function recording(id='new',kind='video',muted=false){return {id,number:1,kind,muted,mediaPath:`media/${id}.${kind==='gif'?'gif':'mp4'}`,isDraft:true};}
async function test(name,run){try{await run();results.push({name,passed:true});}catch(error){results.push({name,passed:false,error:error.stack});}}
(async()=>{
  await test('Restores annotation controls without overwriting persisted defaults',async()=>{
    const h=await annotation({tool:'arrow',color:'#123456',lineWidth:12,fontSize:48,zoomMode:'custom',zoomScale:1.5});
    assert.equal(h.elements.stage.dataset.tool,'arrow');assert.equal(h.elements.color.value,'#123456');assert.equal(h.elements['line-width'].value,'12');assert.equal(h.elements['font-size'].value,'48');assert.equal(h.elements['zoom-value'].textContent,'150%');assert.equal(h.preferences().length,0);
    h.resize();assert.equal(h.preferences().length,0);
  });
  await test('Missing preferences preserve original demo defaults without writes',async()=>{
    const h=await annotation();assert.equal(h.elements.stage.dataset.tool,'rect');assert.equal(h.elements['line-width'].value,'5');assert.equal(h.elements['font-size'].value,'28');assert.equal(h.preferences().length,0);
  });
  await test('Explicit tool, palette, width and font changes immediately send only changed fields',async()=>{
    const h=await annotation();await h.tools.find(button=>button.dataset.tool==='pen').fire('click');await h.colors[3].fire('click');h.elements['line-width'].value='18';await h.elements['line-width'].fire('change');h.elements['font-size'].value='64';await h.elements['font-size'].fire('change');
    assert.deepEqual(h.preferences().map(message=>message.preferences),[{tool:'pen'},{color:'#4e9cff'},{lineWidth:18},{fontSize:64}]);
  });
  await test('Automatically selecting painted content does not replace the preferred drawing tool',async()=>{
    const h=await annotation({tool:'arrow'});await h.elements.canvas.fire('pointerdown',{button:0,pointerId:1,clientX:200,clientY:200});await h.elements.canvas.fire('pointerup',{pointerId:1,clientX:200,clientY:200});
    assert.equal(h.elements.stage.dataset.tool,'select');assert.equal(h.preferences().length,0);
  });
  await test('High frequency custom color input coalesces and cancel flushes before cancel message',async()=>{
    const h=await annotation();for(const color of ['#000001','#000002','#102030']){h.elements.color.value=color;await h.elements.color.fire('input');}assert.equal(h.preferences().length,0);assert.equal(h.timers.size,1);
    await h.elements.cancel.fire('click');assert.deepEqual(h.messages.slice(-2),[{type:'annotationPreferences',preferences:{color:'#102030'}},{type:'cancel'}]);assert.equal(h.timers.size,0);
  });
  await test('Native close takes only unflushed fields and never sends them twice',async()=>{
    const h=await annotation();h.elements.color.value='#123abc';await h.elements.color.fire('input');
    assert.deepEqual(JSON.parse(JSON.stringify(h.window.stepwiseAnnotation.takePendingPreferences())),{color:'#123abc'});assert.equal(h.window.stepwiseAnnotation.takePendingPreferences(),null);h.runTimers();assert.equal(h.preferences().length,0);
  });
  await test('Color change, focus loss and confirmation flush preferences reliably',async()=>{
    const h=await annotation();h.elements.color.value='#112233';await h.elements.color.fire('input');await h.elements.color.fire('change');assert.equal(h.preferences().length,1);
    h.elements.color.value='#223344';await h.elements.color.fire('input');await h.window.fire('blur');assert.equal(h.preferences().length,2);
    h.elements.color.value='#334455';await h.elements.color.fire('input');await h.elements.confirm.fire('click');assert.equal(h.messages.at(-2).type,'annotationPreferences');assert.equal(h.messages.at(-1).type,'confirm');
  });
  await test('Only deliberate zoom choices persist, including wheel flush and Fit mode',async()=>{
    const h=await annotation();await h.elements.stage.fire('wheel',{deltaY:-100,clientX:300,clientY:200});assert.equal(h.preferences().length,0);h.runTimers();assert.equal(h.preferences()[0].preferences.zoomMode,'custom');assert.ok(h.preferences()[0].preferences.zoomScale>1);
    await h.elements['zoom-fit'].fire('click');assert.equal(h.preferences().at(-1).preferences.zoomMode,'fit');const count=h.preferences().length;h.resize();assert.equal(h.preferences().length,count);
  });
  await test('Preference error keeps annotation save busy and visible',async()=>{
    const h=await annotation();await h.elements.confirm.fire('click');h.window.stepwiseAnnotation.receive({type:'preferencesError',message:'偏好写入失败'});assert.equal(h.elements.confirm.disabled,true);assert.equal(h.elements.loading.hidden,false);assert.equal(h.elements['notice-text'].textContent,'偏好写入失败');
  });
  await test('New recording uses preferences, repeated init preserves in-progress choice, old step uses saved media',async()=>{
    const h=harness('app.js');editor(h,recording(),{format:'gif',muted:false});assert.equal(h.elements.muted.checked,true);assert.equal(h.elements.muted.disabled,true);assert.equal(h.preferences().length,0);
    await h.formats[0].fire('click');assert.equal(h.elements.muted.checked,false);editor(h,recording(),{format:'gif',muted:true});assert.equal(h.elements.muted.checked,false);assert.equal(h.elements.muted.disabled,false);
    editor(h,{...recording('old','video',true),isDraft:false});assert.equal(h.elements.muted.checked,true);assert.equal(h.elements.muted.disabled,false);assert.equal(h.preferences().length,1);
  });
  await test('GIF inherent silence never changes MP4 mute preference',async()=>{
    const h=harness('app.js');editor(h,recording(),{format:'mp4',muted:false});await h.formats[1].fire('click');assert.deepEqual(h.preferences().at(-1),{type:'recordingPreferences',format:'gif',muted:false});await h.formats[0].fire('click');assert.equal(h.elements.muted.checked,false);
    h.elements.muted.checked=true;await h.elements.muted.fire('change');assert.deepEqual(h.preferences().at(-1),{type:'recordingPreferences',format:'mp4',muted:true});await h.formats[1].fire('click');await h.formats[0].fire('click');assert.equal(h.elements.muted.checked,true);
  });
  await test('Recording preferences immediately precede close and errors do not unlock an active save',async()=>{
    const h=harness('app.js');editor(h,recording(),{format:'mp4',muted:false});h.elements.muted.checked=true;await h.elements.muted.fire('change');await h.elements['close-button'].fire('click');assert.ok(h.messages.findIndex(message=>message.type==='recordingPreferences')<h.messages.findIndex(message=>message.type==='close'));
    h.window.stepwise.receive({type:'busy',message:'正在保存'});h.window.stepwise.receive({type:'preferencesError',message:'偏好写入失败'});assert.equal(h.elements['save-button'].disabled,true);assert.equal(h.elements['save-button'].textContent,'正在保存…');
  });
  fs.mkdirSync(output,{recursive:true});fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed:results.every(result=>result.passed),total:results.length,results},null,2));
  for(const result of results)console.log(`${result.passed?'PASS':'FAIL'} ${result.name}${result.error?'\n'+result.error:''}`);
  if(results.some(result=>!result.passed))process.exitCode=1;
})();
