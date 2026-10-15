// Source + minimal DOM simulation only, never browser/screen-reader acceptance.
import { strict as assert } from 'node:assert';
import { AccessibilityManager, RESEARCH_ANNOUNCEMENT_UNITS as A,
  RESEARCH_ECHO_ENTRIES as E, RESEARCH_ECHO_UNITS as U, RESEARCH_ECHO_ENTRY_UNITS as K } from 'research-source/browser/AccessibilityManager';
import { ExperimentalAccessibilityLimitError as Limit } from 'research-source/common/ExperimentalAccessibility';
import { EventEmitter } from 'research-source/common/EventEmitter';
import { WriteBuffer } from 'research-source/common/input/WriteBuffer';
import * as Strings from 'research-source/browser/LocalizableStrings';
class Element {
  textContent = ''; innerText = ''; style = {}; children:any[] = []; classList = {add() {}};
  appendChild(e:any) {this.children.push(e); } setAttribute() {} addEventListener() {}
  removeEventListener() {} insertAdjacentElement() {} remove() {} focus() {}
}
export async function accessibilityChecks() {
  const document = {createElement:()=>new Element(),addEventListener(){},removeEventListener(){}};
  (globalThis as any).document = document;
  (globalThis as any).window = {setTimeout};
  function fresh() {
    const events:any = {};
    for (const name of ['onResize','onRender','onScroll','onA11yChar','onLineFeed','onA11yTab','onKey','onBlur']) events[name] = new EventEmitter();
    let failures = 0;
    const terminal:any = {rows:24,cols:80, element:new Element(),
      buffer:{ydisp:0,lines:{length:24,get:()=>({translateToString:()=>''})}},
      researchFailAccessibility:()=>failures++};
    for (const [key,value] of Object.entries(events)) terminal[key] = (value as any).event;
    const a:any = new AccessibilityManager(terminal, {} as any,
      {mainDocument:document,onDprChange:new EventEmitter().event} as any,
      {dimensions:{css:{cell:{height:0}}},onDimensionsChange:new EventEmitter().event} as any);
    return {a,events,failures:()=>failures};
  }
  let {a,events} = fresh();
  for(let i=0;i<A/2;i++) a._handleChar('🐳');
  assert.equal(a._charsToAnnounce.length,A);
  assert.throws(()=>a._handleChar('X'),Limit); assert.equal(a._charsToAnnounce.length,A); a.dispose();
  ({a}=fresh());
  for(let i=0;i<A/2;i++) a._handleChar('X');
  a._announceCharacters(); assert.equal(a._liveRegion.textContent.length,A/2);
  for(let i=0;i<A/2;i++) a._handleChar('Y');
  a._announceCharacters(); assert.equal(a._liveRegion.textContent.length,A); assert.equal(a._charsToAnnounce,'');
  assert.throws(()=>a._handleChar('🐳'),Limit); assert.equal(a._liveRegion.textContent.length,A); a.dispose();
  ({a}=fresh());
  a._liveRegion.textContent='X'.repeat(A/2); a._charsToAnnounce='Y'.repeat(A/2);
  assert.throws(()=>a._handleChar('Z'),Limit);
  assert.equal(a._liveRegion.textContent.length,A/2); assert.equal(a._charsToAnnounce.length,A/2); a.dispose();
  ({a}=fresh());
  a._liveRegion.textContent='X'.repeat(A-1); a._handleTab(1);
  assert.throws(()=>a._handleTab(1),Limit); assert.equal(a._charsToAnnounce,' '); a.dispose();
  ({a}=fresh());
  a._liveRegion.textContent='X'.repeat(A-1-Strings.tooMuchOutput.length); a._liveRegionLineCount=20;
  a._handleChar('\n'); a._announceCharacters(); assert.equal(a._liveRegion.textContent.length,A); a.dispose();
  ({a}=fresh());
  a._liveRegion.textContent='X'.repeat(A-Strings.tooMuchOutput.length); a._liveRegionLineCount=20;
  assert.throws(()=>a._handleChar('\n'),Limit); assert.equal(a._charsToAnnounce,''); assert.equal(a._liveRegionLineCount,20); a.dispose();
  let f=fresh(); a=f.a;
  for(let i=0;i<E;i++) f.events.onKey.fire({key:'x'});
  assert.equal(a._charsToConsume.length,E); f.events.onKey.fire({key:'x'});
  assert.equal(f.failures(),1); assert.equal(a._charsToConsume.length,E); a.dispose();
  ({a}=fresh());
  for(let i=0;i<U/K;i++) a._handleKey('🐳'.repeat(K/2));
  assert.equal(a._researchEchoUnits,U); assert.throws(()=>a._handleKey('x'),Limit); a.dispose();
  ({a}=fresh()); assert.throws(()=>a._handleKey('x'.repeat(K+1)),Limit); assert.equal(a._researchEchoUnits,0); a.dispose();
  ({a,events}=fresh());
  a._handleChar('pending'); a._liveRegion.textContent='live'; a._clearLiveRegion();
  assert.equal(a._liveRegion.textContent,''); assert.equal(a._charsToAnnounce,'pending');
  events.onBlur.fire(); assert.equal(a._charsToAnnounce,'');
  a._handleKey('🐳'); a._handleChar('🐳'); assert.equal(a._charsToAnnounce,''); assert.equal(a._researchEchoUnits,0);
  a._handleKey('x'); a._handleChar('y'); assert.equal(a._charsToAnnounce,'y'); assert.equal(a._charsToConsume.length,0);
  a._handleKey('\r'); assert.equal(a._charsToConsume.length,0); // control keys aren't echo entries
  events.onBlur.fire(); assert.equal(a._charsToAnnounce,''); assert.equal(a._liveRegion.textContent,'');
  a._handleKey('x'); a._handleChar('y'); events.onBlur.fire();
  assert.equal(a._researchEchoUnits,0); assert.equal(a._charsToConsume.length,0); a._announceCharacters(); assert.equal(a._liveRegion.textContent,'');
  a._handleKey('x'); a._handleChar('z'); a.dispose();
  assert.equal(a._researchEchoUnits,0); assert.equal(a._charsToAnnounce,''); assert.equal(a._charsToConsume.length,0);
  a._handleChar('z'); a._renderRows(0,23); assert.equal(a._liveRegion.textContent,'');
  f=fresh(); a=f.a; a._liveRegion.textContent='X'.repeat(A);
  // Inject the prospective growth into the real debounced render callback.
  a._renderRows=()=>a._handleChar('X'); a._liveRegionDebouncer.refresh(0,0,24);
  await new Promise(resolve=>setTimeout(resolve,1100));
  assert.equal(f.failures(),1); assert.equal(a._charsToAnnounce,''); a.dispose();
  f=fresh(); a=f.a; let calls=0; a._renderRows=()=>calls++;
  a._liveRegionDebouncer.refresh(0,0,24); a.dispose();
  await new Promise(resolve=>setTimeout(resolve,1100)); assert.equal(calls,0);
  for(const asynchronous of [false,true]) {
    let success=0, failure=0;
    const w=new WriteBuffer(()=>{ if(asynchronous) return Promise.reject(new Limit()); throw new Limit(); });
    w.write('X',((code?:string)=>{assert.equal(code,'accessibility-limit');failure++;}) as any);
    w.write('suffix',()=>success++);
    await new Promise(resolve=>setTimeout(resolve,20));
    assert.equal(success,0); assert.equal(failure,1); assert.equal((w as any)._pendingData,0);
    assert.equal((w as any)._writeBuffer.length,0);
    w.write('later',((code?:string)=>assert.equal(code,'accessibility-limit')) as any); w.dispose();
  }
  let resume:any; let lateSuccess=0, lateFailure=0, actions=0;
  const pending=new WriteBuffer(()=>{actions++;return new Promise<boolean>(resolve=>resume=resolve);});
  pending.write('held',((code?:string)=>{assert.equal(code,'accessibility-limit');lateFailure++;}) as any);
  pending.write('queued',()=>lateSuccess++);
  await new Promise(resolve=>setTimeout(resolve,10)); pending.researchFail('accessibility-limit');
  resume(true); await new Promise(resolve=>setTimeout(resolve,10));
  assert.equal(actions,1); assert.equal(lateSuccess,0); assert.equal(lateFailure,1);
  assert.equal((pending as any)._pendingData,0); pending.dispose();
  let reentrant:any; let reentrantFailure=0, reentrantSuccess=0;
  reentrant=new WriteBuffer(()=>{reentrant.researchFail('accessibility-limit');});
  reentrant.write('active',((code?:string)=>{assert.equal(code,'accessibility-limit');reentrantFailure++;}) as any);
  reentrant.write('queued',()=>reentrantSuccess++);
  await new Promise(resolve=>setTimeout(resolve,10));
  assert.equal(reentrantFailure,1); assert.equal(reentrantSuccess,0); assert.equal(reentrant._pendingData,0); reentrant.dispose();
  console.log('XTERM_ACCESSIBILITY_SOURCE_CHECK_PASS boundaries, aggregate transfer, Unicode, tabs, excessive message, echo units/entries, clear/blur, disposal, guarded timers/keys, revoked writes');
}
