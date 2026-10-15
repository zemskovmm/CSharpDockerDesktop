// Actual WidthCache source with deterministic layout only; not browser acceptance.
import { strict as assert } from 'node:assert';
import { WidthCache } from 'research-source/browser/renderer/dom/WidthCache';
export function widthCacheChecks() {
  let measurements = 0, maximumText = 0, fail = false;
  const elements: any[] = [];
  const document: any = {createElement: () => {
    const el: any = {textContent:'', style:{}, classList:{add(){}}, setAttribute(){}, appendChild(){}, remove(){this.removed=true;}};
    Object.defineProperty(el,'offsetWidth',{get(){
      measurements++; maximumText=Math.max(maximumText,el.textContent.length);
      if(fail) throw Error('synthetic layout failure');
      return el.textContent.length * (el.style.fontStyle==='italic'?3:2) * (el.style.fontWeight==='bold'?2:1);
    }}); elements.push(el); return el;
  }};
  const cache: any = new WidthCache(document, {appendChild(){}} as any);
  const check = () => {
    assert.ok(cache._holey.size<=512); assert.ok(cache._keyUnits<=32768);
    assert.equal(cache._keyUnits,[...cache._holey.keys()].reduce((n,k:any)=>n+k.length,0));
    assert.ok(elements.every(e=>e.textContent===''));
  };
  cache.setFont('mono',14,'normal','bold');
  // Exact entry capacity, one beyond, FIFO eviction and remeasurement.
  for(let i=0;i<512;i++){cache.get('λ'+i,false,false);check();}
  assert.equal(cache._holey.size,512); assert.equal(cache._researchEvictions,0);
  const first=cache.get('λ0',false,false), before=measurements;
  for(let i=0;i<100;i++)assert.equal(cache.get('λ0',false,false),first);
  assert.equal(measurements,before); check();
  cache.get('new',false,false); assert.equal(cache._researchEvictions,1); check();
  assert.equal(cache.get('λ0',false,false),first); assert.equal(measurements,before+2);check();
  // All four variants, literal suffix collision, regular flat/non-flat separation.
  cache.clear();
  for(const [bold,italic] of [[false,false],[true,false],[false,true],[true,true]]) {
    assert.equal(cache.get('λ',bold,italic),(italic?3:2)*(bold?2:1));check();
  }
  assert.equal(cache._holey.size,4);
  assert.equal(cache.get('λB',false,false),4); assert.equal(cache.get('λ',true,false),4);
  assert.equal(cache._holey.size,5);
  cache.get('a',false,false);assert.equal(cache._holey.size,5); assert.equal(cache._flat.byteLength,1024);
  // Exact aggregate capacity: each key is 256 UTF-16 units including its prefix.
  cache.clear();
  for(let i=0;i<128;i++){cache.get(String.fromCharCode(0x400+i)+'\u0301'.repeat(254),false,false);check();}
  assert.equal(cache._keyUnits,32768);assert.equal(cache._holey.size,128);
  const evictions=cache._researchEvictions;
  cache.get('🐳'+'\u0301'.repeat(254),true,true);check();
  assert.ok(cache._researchEvictions>evictions);assert.equal(maximumText,8192);
  // Cache admission bypasses oversized keys before concatenation (outside cell contract).
  cache.clear();cache.get('x'.repeat(32768),true,true);check();assert.equal(cache._holey.size,0);
  // Zero measurements must not be cached; measurement exceptions release hidden text.
  const measure=cache._measure;
  cache._measure=()=>0;cache.get('zero',false,false);check();assert.equal(cache._holey.size,0);cache._measure=measure;
  fail=true;assert.throws(()=>cache.get('failure',false,false));fail=false;check();
  cache.get('🐳\u0301',true,true);const count=measurements;
  cache.setFont('mono',14,'normal','bold');assert.equal(cache._holey.size,1);
  cache.get('🐳\u0301',true,true);assert.equal(measurements,count);
  for(const args of [['other',14,'normal','bold'],['other',16,'normal','bold'],['other',16,400,'bold'],['other',16,400,700]] as any[]) {
    cache.setFont(...args);check();assert.equal(cache._keyUnits,0);assert.equal(cache._holey.size,0);
    cache.get('🐳\u0301',true,true);check();
  }
  cache.clear();check();assert.equal(cache._keyUnits,0);assert.ok([...cache._flat].every(n=>n===-9999));
  cache.get('a\u0301',true,false);cache.dispose();assert.equal(cache._holey,undefined);
  assert.equal(cache._keyUnits,0);assert.equal(cache._flat.byteLength,0);assert.equal(cache._measureElements.length,0);
  assert.ok(elements[0].removed); assert.ok(elements.every(e=>e.textContent===''));
  console.log('WIDTH_CACHE_SOURCE_CHECK_PASS entry/unit boundaries, FIFO remeasurement, hits, variants, Unicode, clear/font, span cleanup, disposal');
}
