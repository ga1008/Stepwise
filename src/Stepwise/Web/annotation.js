(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const stage = $('stage'), canvas = $('canvas'), ctx = canvas.getContext('2d');
  const input = $('text-input');
  const names = {select:'选择与移动',rect:'矩形框',ellipse:'圈选',pen:'画笔',arrow:'实心箭头',text:'文字'};
  const hints = {select:'点击笔迹后拖动 · Delete 删除',rect:'拖动画框 · 轻点笔迹可选中',ellipse:'拖动圈选 · 轻点笔迹可选中',pen:'按住自由绘制 · 每一笔独立移动',arrow:'从起点拖向终点 · 大小随长度变化',text:'点击输入 · 失去焦点后保留文字'};
  let engine = null, image = null, tool = 'select', busy = false, ready = false;
  let view = {scale:1,x:0,y:0,fit:true}, pointer = null, space = false, composing = false;
  let textDraft = null, drawFrame = 0, lastStage = {width:0,height:0}, loadSerial = 0;
  let preferencesReady = false, preferenceTimer = null, pendingPreferences = null;
  const demo = ['127.0.0.1','localhost'].includes(location.hostname) && new URLSearchParams(location.search).get('demo')==='1';
  const post = message => {
    if(window.chrome?.webview)window.chrome.webview.postMessage(message);
    else if(demo && message.type==='confirm'){window.annotationDemoResult=message;setBusy(false);notice(`本地验收：${message.layers.layers?.length??0} 个笔迹，原图尺寸已保留。`);}
    else if(demo && message.type==='cancel'){notice('本地验收：已取消本次截图。');}
  };
  const notice = message => { $('notice-text').textContent = String(message); $('notice').hidden = false; };
  const currentStyle = () => ({color:$('color').value,lineWidth:Number($('line-width').value),fontSize:Number($('font-size').value),fontFamily:'Stepwise Sans'});
  const clamp = (n,a,b) => Math.min(b,Math.max(a,n));
  const source = event => {const r=stage.getBoundingClientRect();return {x:(event.clientX-r.left-view.x)/view.scale,y:(event.clientY-r.top-view.y)/view.scale};};
  const local = event => {const r=stage.getBoundingClientRect();return {x:event.clientX-r.left,y:event.clientY-r.top};};
  const inside = point => engine && point.x>=0 && point.y>=0 && point.x<=engine.width && point.y<=engine.height;

  function takePendingPreferences() {
    clearTimeout(preferenceTimer);preferenceTimer=null;
    const preferences=pendingPreferences;pendingPreferences=null;
    return preferences;
  }
  function flushPreferences() {
    const preferences=takePendingPreferences();
    if(!preferences)return;
    post({type:'annotationPreferences',preferences});
  }
  function rememberPreferences(patch,deferred=false) {
    if(!preferencesReady)return;
    pendingPreferences={...pendingPreferences,...patch};
    clearTimeout(preferenceTimer);
    if(deferred)preferenceTimer=setTimeout(flushPreferences,120);else flushPreferences();
  }
  function restorePreferences(preferences) {
    const saved=preferences&&typeof preferences==='object'?preferences:{};
    if(/^#[0-9a-f]{6}$/i.test(saved.color||''))colorChanged(saved.color);
    for(const [id,key] of [['line-width','lineWidth'],['font-size','fontSize']]){
      const control=$(id),value=String(saved[key]);
      if(Array.from(control.options).some(option=>option.value===value))control.value=value;
    }
    fit();
    if(saved.zoomMode==='custom'&&Number.isFinite(saved.zoomScale))zoom(saved.zoomScale);
    setTool(names[saved.tool]?saved.tool:'rect');
  }

  function refresh() {
    if (!drawFrame) drawFrame=requestAnimationFrame(draw);
    $('undo').disabled=busy || !engine?.canUndo; $('redo').disabled=busy || !engine?.canRedo;
    $('delete').disabled=busy || !engine?.selectedId; $('confirm').disabled=busy || !ready;
    $('zoom-value').textContent=`${Math.round(view.scale*100)}%`;
  }
  function draw() {
    drawFrame=0;
    const ratio=window.devicePixelRatio||1, width=stage.clientWidth, height=stage.clientHeight;
    if (canvas.width!==Math.round(width*ratio)||canvas.height!==Math.round(height*ratio)) {canvas.width=Math.round(width*ratio);canvas.height=Math.round(height*ratio);}
    ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,width,height);
    if (!engine) return;
    const w=engine.width*view.scale,h=engine.height*view.scale;
    ctx.save();ctx.shadowColor='#07131075';ctx.shadowBlur=30;ctx.shadowOffsetY=9;ctx.fillStyle='#fff';ctx.fillRect(view.x,view.y,w,h);ctx.restore();
    ctx.save();ctx.beginPath();ctx.rect(view.x,view.y,w,h);ctx.clip();ctx.translate(view.x,view.y);ctx.scale(view.scale,view.scale);
    engine.draw(ctx,{selection:false,background:true});ctx.restore();
    if (engine.selectedId && !pointer?.drawing) {
      const b=engine.boundsOfLayer(engine.selectedId);
      if(b){const x=view.x+b.x*view.scale-5,y=view.y+b.y*view.scale-5,bw=b.width*view.scale+10,bh=b.height*view.scale+10;
        ctx.save();ctx.strokeStyle='#fff';ctx.lineWidth=3;ctx.setLineDash([5,4]);ctx.strokeRect(x,y,bw,bh);ctx.strokeStyle='#258b6b';ctx.lineWidth=1;ctx.strokeRect(x,y,bw,bh);ctx.setLineDash([]);
        for(const [px,py] of [[x,y],[x+bw,y],[x,y+bh],[x+bw,y+bh]]){ctx.fillStyle='#fff';ctx.fillRect(px-3,py-3,6,6);ctx.strokeStyle='#258b6b';ctx.strokeRect(px-3,py-3,6,6);}ctx.restore();}
    }
    positionText();
  }
  function fit(remember=false) {
    if(!engine)return;
    const padding=engine.width<300 || engine.height<150 ? 45 : 32;
    view.scale=Math.min(1,(stage.clientWidth-padding*2)/engine.width,(stage.clientHeight-padding*2)/engine.height);
    view.scale=Math.max(.01,view.scale);view.x=(stage.clientWidth-engine.width*view.scale)/2;view.y=(stage.clientHeight-engine.height*view.scale)/2;view.fit=true;refresh();
    if(remember)rememberPreferences({zoomMode:'fit',zoomScale:view.scale});
  }
  function zoom(scale,anchor={x:stage.clientWidth/2,y:stage.clientHeight/2},remember=false,deferred=false) {
    if(!engine || busy)return;
    const min=Math.min(.05,Math.min(stage.clientWidth/engine.width,stage.clientHeight/engine.height)*.5),next=clamp(scale,min,8);
    view.x=anchor.x-(anchor.x-view.x)*next/view.scale;view.y=anchor.y-(anchor.y-view.y)*next/view.scale;view.scale=next;view.fit=false;refresh();
    if(remember)rememberPreferences({zoomMode:'custom',zoomScale:view.scale},deferred);
  }
  function setTool(next,remember=false) {
    if(busy || !names[next])return;
    commitText(); abortPointer(); tool=next;
    document.querySelectorAll('[data-tool]').forEach(b=>{const selected=b.dataset.tool===tool;b.classList.toggle('active',selected);b.setAttribute('aria-pressed',String(selected));});
    stage.dataset.tool=tool;$('tool-name').textContent=names[tool];$('hint').textContent=hints[tool];$('text-tip').hidden=tool!=='text';
    if(tool!=='select')engine?.select(null);refresh();stage.focus({preventScroll:true});
    if(remember)rememberPreferences({tool});
  }
  function positionText() {
    if(!textDraft)return;
    input.style.left=`${view.x+textDraft.point.x*view.scale}px`;input.style.top=`${view.y+textDraft.point.y*view.scale}px`;
    input.style.transform=`scale(${view.scale})`;
  }
  function resizeText() {
    if(!textDraft)return;
    const size=textDraft.style.fontSize,lines=input.value.replace(/\r/g,'').split('\n');
    const measure=document.createElement('canvas').getContext('2d');measure.font=`${size}px "Stepwise Sans"`;
    const width=Math.max(size*5,...lines.map(line=>measure.measureText(line||' ').width+size));
    input.style.width=`${Math.ceil(width)}px`;input.style.height=`${Math.ceil(Math.max(1,lines.length)*size*1.35+4)}px`;positionText();
  }
  function startText(point) {
    commitText();engine.select(null);textDraft={point:{...point},style:currentStyle()};input.value='';input.style.fontSize=`${textDraft.style.fontSize}px`;input.style.color=textDraft.style.color;input.hidden=false;resizeText();input.focus({preventScroll:true});refresh();
  }
  function commitText() {
    if(!textDraft || composing)return;
    const draft=textDraft,value=input.value;textDraft=null;input.hidden=true;input.value='';
    if(value.trim())engine.addText(draft.point,value,draft.style);refresh();
  }
  function cancelText() {textDraft=null;input.hidden=true;input.value='';composing=false;refresh();}
  function colorChanged(color) {$('color').value=color;$('color-chip').style.background=color;document.querySelectorAll('[data-color]').forEach(b=>b.classList.toggle('selected',b.dataset.color.toLowerCase()===color.toLowerCase()));}
  function setBusy(value,label='正在保存标注') {busy=value;document.body.classList.toggle('busy',value);$('cancel').disabled=value&&ready;$('loading').hidden=!value;$('loading-text').textContent=label;refresh();}
  function abortPointer() {
    const old=pointer;pointer=null;stage.classList.remove('panning');engine?.cancelGesture();
    if(old && canvas.hasPointerCapture(old.id))canvas.releasePointerCapture(old.id);
    refresh();
  }

  async function initialize(message) {
    const serial=++loadSerial;flushPreferences();preferencesReady=false;ready=false;setBusy(true,'正在准备截图');$('notice').hidden=true;cancelText();
    try {
      const fonts=await document.fonts.load('28px "Stepwise Sans"');
      if(!fonts.length || !document.fonts.check('28px "Stepwise Sans"'))throw new Error('内置字体未能载入，请保留完整的软件文件夹后重新运行。');
      const loaded=new Image();loaded.crossOrigin='anonymous';
      await new Promise((resolve,reject)=>{loaded.onload=resolve;loaded.onerror=()=>reject(new Error('截图未能载入，请取消后重新截图。'));loaded.src=message.imageUrl;});
      if(serial!==loadSerial)return;
      image=loaded;engine=new AnnotationEngine({width:loaded.naturalWidth,height:loaded.naturalHeight,image:loaded,fontFamily:'Stepwise Sans'});engine.onChange=refresh;
      $('dimensions').textContent=`${loaded.naturalWidth} × ${loaded.naturalHeight}`;
      ready=true;setBusy(false);restorePreferences(message.preferences);preferencesReady=true;post({type:'initialized',width:loaded.naturalWidth,height:loaded.naturalHeight});
    } catch(error) {if(serial===loadSerial){setBusy(false);notice(error.message||String(error));}}
  }
  async function confirm() {
    if(!ready||busy||composing)return;
    flushPreferences();commitText();abortPointer();setBusy(true);
    try {await document.fonts.ready;const png=engine.flattenPNG();post({type:'confirm',pngBase64:png.includes(',')?png.slice(png.indexOf(',')+1):png,layers:engine.serialize()});}
    catch(error){setBusy(false);notice(`保存未完成：${error.message||error}`);}
  }
  function cancel() {if(busy&&ready)return;flushPreferences();loadSerial++;cancelText();abortPointer();post({type:'cancel'});}
  window.stepwiseAnnotation={receive(message){if(typeof message==='string'){try{message=JSON.parse(message);}catch{return;}}if(!message||typeof message!=='object')return;if(message.type==='init')initialize(message);if(message.type==='error'){setBusy(false);notice(message.message||'保存未完成，请重试。');}if(message.type==='preferencesError')notice(message.message||'设置暂未保存，请重试。');},flushPreferences,takePendingPreferences};

  canvas.addEventListener('pointerdown',event=>{
    if(!ready||busy || (event.button!==0&&event.button!==1))return;
    if(composing)return;
    commitText();const point=source(event),screen=local(event);stage.focus({preventScroll:true});
    if(space||event.button===1){pointer={id:event.pointerId,pan:true,start:screen,x:view.x,y:view.y};stage.classList.add('panning');}
    else if(!inside(point)){engine.select(null);refresh();return;}
    else if(tool==='text'){startText(point);event.preventDefault();return;}
    else if(tool==='select'){
      const hit=engine.hitTest(point,Math.max(1.5,5/view.scale));engine.select(hit?.id||null);
      if(hit){engine.beginMove(hit.id,point);pointer={id:event.pointerId,move:true,start:screen};}refresh();
    } else {
      const hit=engine.hitTest(point,Math.max(1.5,4/view.scale));pointer={id:event.pointerId,drawing:true,start:screen,startSource:point,hit:hit?.id||null,started:false};
    }
    if(pointer)canvas.setPointerCapture(event.pointerId);event.preventDefault();
  });
  canvas.addEventListener('pointermove',event=>{
    if(!pointer||pointer.id!==event.pointerId||busy)return;
    const point=source(event),screen=local(event);
    if(pointer.pan){view.x=pointer.x+screen.x-pointer.start.x;view.y=pointer.y+screen.y-pointer.start.y;view.fit=false;refresh();}
    else if(pointer.move)engine.updateMove(point);
    else if(pointer.drawing){
      if(!pointer.started && Math.hypot(screen.x-pointer.start.x,screen.y-pointer.start.y)>=2){engine.select(null);engine.begin(tool,pointer.startSource,currentStyle());pointer.started=true;}
      if(pointer.started)engine.update(point);
    }
  });
  function endPointer(event) {
    if(!pointer||pointer.id!==event.pointerId)return;
    const old=pointer;pointer=null;stage.classList.remove('panning');
    if(old.move)engine.endMove(source(event));
    else if(old.drawing){
      if(old.started)engine.end(source(event));
      else if(old.hit){engine.select(old.hit);setTool('select');}
      else if(tool==='pen'){engine.begin(tool,old.startSource,currentStyle());engine.end(old.startSource);}
      else engine.select(null);
    }
    if(canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);refresh();
  }
  canvas.addEventListener('pointerup',endPointer);
  canvas.addEventListener('pointercancel',abortPointer);
  canvas.addEventListener('lostpointercapture',()=>{if(pointer)abortPointer();});
  stage.addEventListener('wheel',event=>{if(!engine||busy)return;event.preventDefault();if(composing)return;zoom(view.scale*Math.exp(-event.deltaY*.0015),local(event),true,true);},{passive:false});
  stage.addEventListener('contextmenu',event=>event.preventDefault());
  input.addEventListener('input',resizeText);input.addEventListener('blur',commitText);
  input.addEventListener('compositionstart',()=>{composing=true;});input.addEventListener('compositionend',()=>{composing=false;resizeText();if(document.activeElement!==input)commitText();});
  input.addEventListener('keydown',event=>{if(event.isComposing||composing||event.keyCode===229)return;if(event.key==='Escape'){event.preventDefault();event.stopPropagation();cancelText();stage.focus();}else if(event.key==='Enter'&&(event.ctrlKey||event.metaKey)){event.preventDefault();event.stopPropagation();commitText();stage.focus();}});
  document.querySelectorAll('[data-tool]').forEach(button=>button.addEventListener('click',()=>setTool(button.dataset.tool,true)));
  document.querySelectorAll('[data-color]').forEach(button=>button.addEventListener('click',()=>{commitText();colorChanged(button.dataset.color);rememberPreferences({color:$('color').value});}));
  $('color').addEventListener('input',event=>{colorChanged(event.target.value);rememberPreferences({color:$('color').value},true);});
  $('color').addEventListener('change',event=>{colorChanged(event.target.value);rememberPreferences({color:$('color').value});});
  $('color').addEventListener('blur',flushPreferences);
  $('line-width').addEventListener('change',()=>rememberPreferences({lineWidth:Number($('line-width').value)}));
  $('font-size').addEventListener('change',()=>rememberPreferences({fontSize:Number($('font-size').value)}));
  $('undo').addEventListener('click',()=>{commitText();engine?.undo();refresh();});$('redo').addEventListener('click',()=>{commitText();engine?.redo();refresh();});
  $('delete').addEventListener('click',()=>{commitText();engine?.deleteSelected();refresh();});
  $('confirm').addEventListener('click',confirm);$('cancel').addEventListener('click',cancel);$('notice-close').addEventListener('click',()=>{$('notice').hidden=true;});
  $('zoom-in').addEventListener('click',()=>zoom(view.scale*1.25,undefined,true));$('zoom-out').addEventListener('click',()=>zoom(view.scale/1.25,undefined,true));$('zoom-fit').addEventListener('click',()=>fit(true));$('zoom-value').addEventListener('click',()=>zoom(1,undefined,true));
  document.addEventListener('keydown',event=>{
    if(event.isComposing||composing||event.keyCode===229)return;
    if(busy){if(!ready&&event.key==='Escape'){event.preventDefault();cancel();}return;}
    const editing=event.target instanceof HTMLInputElement||event.target instanceof HTMLTextAreaElement||event.target instanceof HTMLSelectElement;
    if(editing)return;
    const key=event.key.toLowerCase(),mod=event.ctrlKey||event.metaKey;
    if(mod&&key==='enter'){event.preventDefault();confirm();return;}
    if(mod&&key==='0'){event.preventDefault();fit(true);return;}
    if(mod&&(key==='+'||key==='='||key==='-')){event.preventDefault();zoom(view.scale*(key==='-'?.8:1.25),undefined,true);return;}
    if(mod&&(key==='z'||key==='y')){event.preventDefault();if(key==='y'||event.shiftKey)engine?.redo();else engine?.undo();refresh();return;}
    if(key==='escape'){event.preventDefault();if(pointer)abortPointer();else if(engine?.selectedId){engine.select(null);refresh();}else cancel();return;}
    if(key==='delete'||key==='backspace'){event.preventDefault();engine?.deleteSelected();refresh();return;}
    if(key===' '){event.preventDefault();space=true;stage.classList.add('space-held');return;}
    if(!mod&&!event.altKey){const next={v:'select',r:'rect',o:'ellipse',p:'pen',a:'arrow',t:'text'}[key];if(next){event.preventDefault();setTool(next,true);}}
  });
  document.addEventListener('keyup',event=>{if(event.key===' '){space=false;stage.classList.remove('space-held');}});
  window.addEventListener('blur',()=>{flushPreferences();space=false;stage.classList.remove('space-held');if(pointer)abortPointer();});
  window.addEventListener('beforeunload',flushPreferences);
  document.addEventListener('visibilitychange',()=>{if(document.hidden){flushPreferences();if(pointer)abortPointer();}});
  new ResizeObserver(()=>{const width=stage.clientWidth,height=stage.clientHeight;if(view.fit)fit();else{view.x+=(width-lastStage.width)/2;view.y+=(height-lastStage.height)/2;refresh();}lastStage={width,height};}).observe(stage);
  colorChanged($('color').value);setTool('rect');post({type:'ready'});
  if(demo){
    const sample=document.createElement('canvas');sample.width=960;sample.height=540;const g=sample.getContext('2d');
    g.fillStyle='#f4f6f1';g.fillRect(0,0,960,540);g.fillStyle='#ffffff';g.fillRect(0,0,960,68);g.fillStyle='#236a52';g.font='bold 20px "Microsoft YaHei UI",sans-serif';g.fillText('操作步骤 · 演示画板',34,43);
    g.font='13px "Microsoft YaHei UI",sans-serif';g.fillStyle='#80937f';g.fillText('本地标注验收 · 960 × 540',711,41);
    for(let i=0;i<3;i++){g.fillStyle='#fff';g.fillRect(32+i*310,106,278,240);g.fillStyle=['#d8e9dc','#e6e5f3','#fae7d1'][i];g.fillRect(48+i*310,122,246,105);g.fillStyle='#41634f';g.font='bold 18px "Microsoft YaHei UI",sans-serif';g.fillText(['选择区域','说明重点','保存步骤'][i],53+i*310,265);g.font='13px "Microsoft YaHei UI",sans-serif';g.fillStyle='#82947e';g.fillText('框选、箭头和文字可以独立移动',53+i*310,299);}
    g.fillStyle='#e7eee1';g.fillRect(32,374,896,132);g.fillStyle='#748c6e';g.font='16px "Microsoft YaHei UI",sans-serif';g.fillText('在此标注截图中的关键内容',56,416);g.font='13px "Microsoft YaHei UI",sans-serif';g.fillText('颜色与粗细只影响新笔迹，所有修改均可撤销。',56,453);
    initialize({imageUrl:sample.toDataURL('image/png')});
  }
})();
