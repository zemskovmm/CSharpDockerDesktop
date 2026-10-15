// Source/unit evidence, never described as native browser evidence. Synthetic values only.
import { strict as assert } from 'node:assert';
import { BufferLine, DEFAULT_ATTR_DATA, ExperimentalCombinedCellLimitError } from 'research-source/common/buffer/BufferLine';
import { CellData } from 'research-source/common/buffer/CellData';
import { WriteBuffer } from 'research-source/common/input/WriteBuffer';
import { widthCacheChecks } from './width-cache-checks';
import { accessibilityChecks } from './accessibility-checks';
async function main() {
const blank = CellData.fromCharData([0, '', 1, 0]);
function exact(line = new BufferLine(8), index = 0) {
  line.setCellFromCodepoint(index, 97, 1, DEFAULT_ATTR_DATA);
  for (let i=1; i<256; i++) line.addCodepointToCell(index, 0x301, 0);
  assert.equal(line.getString(index).length,256);
  return line;
}
function rejects(line:BufferLine,index:number) {
  const before=line.getString(index);
  assert.throws(()=>line.addCodepointToCell(index,0x301,0),ExperimentalCombinedCellLimitError);
  assert.equal(line.getString(index),before);
  assert.ok(Object.values((line as any)._combined).every((s:any)=>s.length<=256));
}
const line=exact(); rejects(line,0);
line.setCellFromCodepoint(0,98,1,DEFAULT_ATTR_DATA); // Stale entry must not act as append prefix.
line.addCodepointToCell(0,0x301,0); assert.equal(line.getString(0),'b\u0301');
exact(line,0); line.insertCells(0,2,blank); rejects(line,2);
line.deleteCells(0,1,blank); rejects(line,1);
line.resize(4,blank); rejects(line,1); line.resize(12,blank); rejects(line,1);
const clone=line.clone(); rejects(clone,1);
const copied=new BufferLine(12); copied.copyCellsFrom(clone,1,3,1,false); rejects(copied,3);
const scalar=new BufferLine(2); scalar.setCellFromCodepoint(0,0x1f433,2,DEFAULT_ATTR_DATA);
for(let i=0;i<127;i++) scalar.addCodepointToCell(0,0x1d185,0);
assert.equal(scalar.getString(0).length,256); rejects(scalar,0);
const over=CellData.fromCharData([0,'a'.repeat(257),1,97]);
assert.throws(()=>line.setCell(0,over),ExperimentalCombinedCellLimitError);
assert.throws(()=>line.set(0,[0,'a'.repeat(257),1,97]),ExperimentalCombinedCellLimitError);
  const guarded=exact(); let applied=0, acknowledgments=0, failures=0;
  const writes=new WriteBuffer(data=>{for(const cp of data as Uint8Array){applied++;guarded.addCodepointToCell(0,cp,0);}});
  await new Promise<void>(resolve=>{
    writes.write(new Uint8Array([1,2,3]),((failure?:string)=>{
      assert.equal(failure,'combined-cell-limit'); failures++; resolve();
    }) as any);
    writes.write(new Uint8Array([4]),()=>acknowledgments++);
  });
  await new Promise(resolve=>setTimeout(resolve,10));
  assert.equal(applied,1); assert.equal(acknowledgments,0); assert.equal(failures,1);
  assert.equal((writes as any)._pendingData,0); assert.equal((writes as any)._writeBuffer.length,0);
  writes.write(new Uint8Array([5]),((failure?:string)=>assert.equal(failure,'combined-cell-limit')) as any);
  assert.equal(applied,1); rejects(guarded,0);
  widthCacheChecks();
  await accessibilityChecks();
  console.log('XTERM_SOURCE_CHECK_PASS exact, supplementary, overwrite, insert/delete, resize, copy/clone, pre-growth rejection, queue revocation');
}
main().catch(()=>{console.error('XTERM_SOURCE_CHECK_FAIL');process.exitCode=1;});
