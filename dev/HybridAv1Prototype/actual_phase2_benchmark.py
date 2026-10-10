"""Invoke the committed Phase 2 exporter, not the prototype's aligned control."""
import json, statistics, threading, time
import prototype as p
from benchmark import gpu_sampler, SAMPLES, STOP

def main():
    rows=[]; root=p.ROOT/'actual-phase2'; sampler=threading.Thread(target=gpu_sampler,daemon=True); sampler.start()
    try:
        for seconds in [15,30,60,120]:
            for iteration in range(3):
                source=p.REPO/f'artifacts/gop-phase2/benchmark/snapshot-{seconds}-A-copy-1'
                dest=root/f'{seconds}-{iteration+1}'
                begin=time.perf_counter(); _,process=p.execute(p.DOTNET,[p.INDEX,'phase2',source,dest]); end=time.perf_counter()
                result=json.loads((dest/'results.json').read_text(encoding='utf-8')); output=p.index(result['output'])
                template=p.index(p.ROOT/'benchmarks'/f'{seconds}-aligned-copy-1'/'unpublished.mkv')
                if not p.compatible(output,template) or output['Packets'] != template['Packets']:
                    raise AssertionError('Actual Phase 2 differs from independently decoded same-source template')
                a,_=p.execute(p.PROBE,['-v','error','-select_streams','a','-show_packets','-show_data_hash','sha256',
                                     '-show_entries','packet=stream_index,pts,dts,duration,data_hash','-of','json',result['output']])
                b,_=p.execute(p.PROBE,['-v','error','-select_streams','a','-show_packets','-show_data_hash','sha256',
                                     '-show_entries','packet=stream_index,pts,dts,duration,data_hash','-of','json',p.ROOT/'benchmarks'/f'{seconds}-aligned-copy-1'/'unpublished.mkv'])
                if json.loads(a)!=json.loads(b):raise AssertionError('Actual Phase 2 AAC/PTS differs')
                samples=[s['values'] for s in SAMPLES if begin<=s['time']<=end and 'values' in s]
                result.update(seconds=seconds,iteration=iteration+1,byte_equivalent_verified=True,
                              gpu_mean=sum(s[0] for s in samples)/len(samples) if samples else None,
                              max_nvenc_sessions=max(s[4] for s in samples) if samples else None)
                rows.append(result);p.dump(root/'runs.json',rows)
                print(seconds,iteration+1,round(result['total_ms'],3),flush=True)
    finally:STOP.set();sampler.join(timeout=6);p.dump(root/'gpu-samples.json',SAMPLES)
    p.dump(root/'summary.json',[dict(seconds=d,n=3,min_ms=min(r['total_ms'] for r in rows if r['seconds']==d),
               median_ms=statistics.median(r['total_ms'] for r in rows if r['seconds']==d),max_ms=max(r['total_ms'] for r in rows if r['seconds']==d)) for d in [15,30,60,120]])

if __name__=='__main__':main()
