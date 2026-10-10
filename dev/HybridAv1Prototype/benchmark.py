"""Same retained source, rotated-order offline timings; never production latency."""
import dataclasses, json, math, statistics, subprocess, threading, time
from pathlib import Path
import prototype as p
from verify import validate

SAMPLES=[]; STOP=threading.Event()
def gpu_sampler():
    while not STOP.is_set():
        try:
            out=subprocess.check_output(['nvidia-smi','--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used,encoder.stats.sessionCount',
                                         '--format=csv,noheader,nounits'],creationflags=subprocess.CREATE_NO_WINDOW,timeout=5)
            SAMPLES.append(dict(time=time.perf_counter(),values=[float(v) for v in out.decode().strip().split(',')]))
        except Exception as e: SAMPLES.append(dict(time=time.perf_counter(),error=str(e)))
        STOP.wait(.2)

def main():
    rows=[]; target=p.ROOT/'benchmarks'; target.mkdir(parents=True,exist_ok=True)
    snapshots={}
    for seconds in [15,30,60,120]:
        for start in [0,.75]:
            snapshot=p.crop_audio(p.retained_snapshot(seconds,start),target/f'pcm-{seconds}-{start}',start)
            p.preindex([r.path for r in snapshot.intervals]); plan=p.make_plan(snapshot)
            snapshots[seconds,start]=(snapshot,plan)
    sampler=threading.Thread(target=gpu_sampler,daemon=True); sampler.start()
    try:
        for seconds in [15,30,60,120]:
            jobs=[('aligned-copy',0,'copy'),('aligned-nvdec',0,'nvdec'),('unaligned-hybrid',.75,'hybrid'),('unaligned-nvdec',.75,'nvdec')]
            for iteration in range(3):
                order=jobs[iteration:]+jobs[:iteration]
                for name,start,strategy in order:
                    directory=target/f'{seconds}-{name}-{iteration+1}'; snapshot,plan=snapshots[seconds,start]
                    begin=time.perf_counter(); result=p.reference(snapshot,directory) if strategy=='nvdec' else p.export(plan,directory,strategy)
                    end=time.perf_counter(); sample=[s['values'] for s in SAMPLES if begin<=s['time']<=end and 'values' in s]
                    metrics=dict(mean_gpu=sum(s[0] for s in sample)/len(sample),mean_encoder=sum(s[1] for s in sample)/len(sample),
                                 mean_decoder=sum(s[2] for s in sample)/len(sample),peak_gpu_memory_mib=max(s[3] for s in sample),
                                 max_nvenc_sessions=max(s[4] for s in sample)) if sample else None
                    row=dict(seconds=seconds,group=name,iteration=iteration+1,requested_duration=snapshot.duration,**result,gpu=metrics,
                             cpu_ms=sum(s.get('cpu_ms',0) or 0 for s in result['stages']))
                    rows.append(row); p.dump(target/'runs.json',rows)
                    print(seconds,name,iteration+1,round(result['total_ms'],2),flush=True)
    finally: STOP.set(); sampler.join(timeout=6); p.dump(target/'gpu-samples.json',SAMPLES)
    summary=[]
    for seconds in [15,30,60,120]:
        for name,_,_ in jobs:
            matching=[r for r in rows if r['seconds']==seconds and r['group']==name]
            values=[r['total_ms'] for r in matching]
            summary.append(dict(seconds=seconds,group=name,n=len(values),min_ms=min(values),median_ms=statistics.median(values),max_ms=max(values),
                                median_stages={stage:statistics.median([next(s['ms'] for s in r['stages'] if s['stage']==stage) for r in matching])
                                               for stage in set(s['stage'] for s in matching[0]['stages'])}))
    p.dump(target/'summary.json',summary)
    # Independent frame/audio reference checks kept outside the timed workloads.
    for seconds in [15,30,60,120]:
        for name,start in [('aligned-copy',0),('unaligned-hybrid',.75)]:
            directory=target/f'{seconds}-{name}-1'; snapshot,plan=snapshots[seconds,start]
            result=json.loads((directory/'export.json').read_text(encoding='utf-8'))
            validate(plan,result,directory)
            print('Integrity passed:',seconds,name,flush=True)

if __name__=='__main__': main()
