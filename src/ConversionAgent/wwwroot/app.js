const $ = id => document.getElementById(id);
const dates = ['start','end','baselineStart','baselineEnd'];
let info;
fetch('/api/info').then(r=>{if(!r.ok)throw Error();return r.json();}).then(data=>{
  info=data;
  $('mode').textContent=`${data.mode === 'Demo' ? 'Demo · без LLM' : 'LLM · вызов инструментов'} / ${data.dataProvider}`;
}).catch(()=>{$('mode').textContent='Сервер недоступен';});
$('example').addEventListener('click',()=>{
  const values=info ? [info.current.start,info.current.end,info.baseline.start,info.baseline.end] : ['2026-10-12','2026-10-19','2026-10-05','2026-10-12'];
  dates.forEach((id,i)=>$(id).value=values[i]);
});
$('clear').addEventListener('click',()=>dates.forEach(id=>$(id).value=''));
function node(tag,text){const el=document.createElement(tag);if(text!==undefined)el.textContent=text;return el;}
function number(value){return value===null||value===undefined ? '—' : Number(value).toLocaleString('ru-RU',{maximumFractionDigits:2});}
function table(title,headers,rows){
  $('tables').append(node('h3',title));
  const scroll=node('div');scroll.className='scroll';const t=node('table');
  const head=node('thead'),hr=node('tr');headers.forEach(h=>hr.append(node('th',h)));head.append(hr);t.append(head);
  const body=node('tbody');rows.forEach(r=>{const tr=node('tr');r.forEach(v=>tr.append(node('td',v)));body.append(tr);});t.append(body);scroll.append(t);$('tables').append(scroll);
}
function render(data){
  $('status').textContent=data.status;
  $('answer').textContent=data.answer||'Ответ не получен.';
  for(const t of data.trace||[]){
    if(t.ok&&t.name==='get_funnel')table(`Воронка: ${t.result.period.start} → ${t.result.period.end} (не включая)`,['Сегмент','Посетители','Checkout','Оплатили','Конверсия, %'],t.result.rows.map(r=>[r.segment,number(r.visitors),number(r.checkouts),number(r.purchasers),number(r.conversionPct)]));
    if(t.ok&&t.name==='compare_segments')table(`Сегменты · ${t.result.dimension}`,['Сегмент','Визиты до','Визиты после','До, %','После, %','Δ, п.п.'],t.result.rows.map(r=>[r.segment,number(r.baseline.visitors),number(r.current.visitors),number(r.baseline.conversionPct),number(r.current.conversionPct),number(r.deltaPp)]));
    const detail=node('details');detail.append(node('summary',`${t.ok?'✓':'!'} ${t.name} · ${t.durationMs} мс · ${t.id}`));
    detail.append(node('pre',JSON.stringify({arguments:t.arguments,result:t.result},null,2)));$('trace').append(detail);
  }
}
$('form').addEventListener('submit',async event=>{
  event.preventDefault();$('output').hidden=false;$('submit').disabled=true;$('submit').textContent='Исследование…';
  const timeoutSeconds = info?.researchTimeoutSeconds || 600;
  $('status').textContent='Выполняется';$('answer').textContent=`Получаем расчёты. LLM-запрос может занять до ${timeoutSeconds} секунд.`;$('trace').replaceChildren();$('tables').replaceChildren();
  try{
    const payload={question:$('question').value};dates.forEach(id=>payload[id]=$(id).value||null);
    const response=await fetch('/api/chat',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload),signal:AbortSignal.timeout((timeoutSeconds + 5) * 1000)});
    if(response.status===429)throw Error('Лимит: 10 запросов в минуту. Подождите и повторите.');
    if(!response.ok&&response.status!==504)throw Error(`Ошибка сервера: HTTP ${response.status}`);
    render(await response.json());
  }catch(error){$('status').textContent='Ошибка';$('answer').textContent=error.name==='TimeoutError'?'Время ожидания истекло. Попробуйте снова.':error.message;}
  finally{$('submit').disabled=false;$('submit').textContent='Исследовать ↗';}
});
