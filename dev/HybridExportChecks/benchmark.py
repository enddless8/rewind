"""Actual Recorder.Save, native capture and production overlay. Resumable;
completed rows are never rerun or overwritten. Offline pairs are separate."""
import json, math, statistics, subprocess, sys, time
from pathlib import Path
from checks import ROOT,REPO,DLL,dump
DOTNET=REPO/'.tools/dotnet/dotnet.exe'

def live(root,category,seconds,iteration):
    base=root/f'{seconds}-{category}-{iteration}';base.mkdir(parents=True,exist_ok=True)
    pointer=base/'completed-attempt.json'
    folder=Path(json.loads(pointer.read_text())['folder']) if pointer.exists() else base
    if not (folder/'results.json').exists() and (folder/'invocation.json').exists():
        attempt=2
        while (base/f'attempt-{attempt}').exists():attempt+=1
        folder=base/f'attempt-{attempt}';folder.mkdir()
    result=folder/'results.json'
    if not result.exists():
        command=[str(DOTNET),str(DLL),'live',str(folder),category,str(seconds)]
        started=time.perf_counter()
        child=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
        try:out,err=child.communicate(timeout=seconds+300)
        except BaseException:child.kill();child.communicate();raise
        dump(folder/'invocation.json',dict(command=command,elapsed_ms=(time.perf_counter()-started)*1000,code=child.returncode,stdout=out,stderr=err))
        if child.returncode:raise RuntimeError(err)
        if folder!=base:dump(pointer,dict(folder=str(folder)))
    data=json.loads(result.read_text(encoding='utf-8'))
    # Each native run contains one accepted Save. First render is measured from
    # SaveTiming, publication is the completed atomic original-file publication.
    stages=data['stages'];mark={s['stage']:s['elapsed'] for s in stages}
    metrics=json.loads((folder/'resources.json').read_text(encoding='utf-8'))
    publication=mark['playable-original-available'];start=mark['history-boundary-frozen']
    # Recorder accepts after the overlay's "saving" notification. Use the
    # original accepted QPC, not the earlier harness/command stopwatch origin.
    trace=[json.loads(line) for line in (folder/'timing.jsonl').read_text(encoding='utf-8').splitlines()]
    boundary=next(s for s in trace if s['Stage']=='history-boundary-frozen')
    start=boundary['ElapsedMs']-(boundary['Tick']/boundary['Frequency']-boundary['Details']['Requested'])*1000
    # Sampler has its own origin; GPU critical window is identified by device
    # usage/command trace, while resource peaks over the whole capture are kept.
    critical=[s for s in metrics if s.get('ms',-1)>=data['ResourceSaveStartedMs'] and s.get('ms',math.inf)<=data['ResourceSaveEndedMs']]
    gpu=[[float(v) for v in s['values'].split(',')] for s in critical if s['stage']=='gpu']
    def duration(a,b):return mark[b]-mark[a] if a in mark and b in mark else None
    row=dict(seconds=seconds,category=category,iteration=iteration,decoder=data['metadata']['ExportDecoder'],folder=str(folder),
        playable_ms=publication-start,command_origin_to_playable_ms=publication,acceptance_origin_ms=start,
        overlay_ms=mark['success-first-wpf-render']-start if 'success-first-wpf-render' in mark else None,overlay_after_ms=duration('playable-original-available','success-first-wpf-render'),
        closure_ms=duration('history-boundary-frozen','closed-segment-ready'),
        planning_ms=duration('hybrid-planning-started','hybrid-planning-completed') or duration('export-planning-started','export-planning-completed'),
        boundary_ms=duration('hybrid-boundary-started','hybrid-boundary-completed'),copy_aac_mux_ms=duration('hybrid-copy-aac-mux-started','hybrid-copy-aac-mux-completed') or duration('packet-copy-started','packet-copy-processing-completed'),
        validation_ms=duration('hybrid-validation-started','hybrid-validation-completed') or duration('packet-copy-processing-completed','packet-copy-validated'),
        publication_ms=duration('media-validation-completed','playable-original-available'),
        cpu_ms=sum(s['details'].get('CpuMs',0) or 0 for s in stages if s['stage']=='process-exited' and s['elapsed']<=publication),
        peak_exporter_memory=max((s['peakWorkingSet'] for s in critical if s['stage']=='memory' and s['role'] in ['ffmpeg','ffprobe']),default=0),
        peak_harness_memory=max((s['peakWorkingSet'] for s in critical if s['stage']=='memory' and s['role']=='harness'),default=0),
        temporary_disk_bytes=max((s['bytes'] for s in critical if s['stage']=='temporary-disk'),default=0),
        gpu_mean=[statistics.mean(s[i] for s in gpu) for i in range(3)] if gpu else None,
        gpu_memory_mib=max((s[3] for s in gpu),default=0),nvenc_sessions=max((s[4] for s in gpu),default=0),
        actual_seconds=data['clip']['Duration'],encoded=data['metadata']['EncodedFrames'],copied=data['metadata']['CopiedFrames'],
        fallback=data['metadata']['HybridFallback'],hybrid_reason=data['metadata']['HybridReason'],
        drops=data['CaptureSyncDropped'],duplicates=data['CaptureSyncDuplicated'],running=data['CaptureRunning'])
    dump(folder/'measurement.json',row);return row
def main():
    root=ROOT/(sys.argv[1] if len(sys.argv)>1 else 'live-matrix-v1');root.mkdir(parents=True,exist_ok=True)
    seconds_list=[int(s) for s in sys.argv[2].split(',')] if len(sys.argv)>2 else [15,30,60,120]
    categories=sys.argv[3].split(',') if len(sys.argv)>3 else ['A','B','C','D']
    count=int(sys.argv[4]) if len(sys.argv)>4 else 3
    rows=[]
    for seconds in seconds_list:
        for iteration in range(1,count+1):
            order=categories[(iteration-1)%len(categories):]+categories[:(iteration-1)%len(categories)]
            for category in order:
                row=live(root,category,seconds,iteration);rows.append(row);dump(root/'runs.json',rows)
                print(seconds,category,iteration,row['decoder'],round(row['playable_ms'],3),row['hybrid_reason'],flush=True)
    summary=[]
    for seconds in seconds_list:
        for category in categories:
            group=[r for r in rows if r['seconds']==seconds and r['category']==category]
            summary.append(dict(seconds=seconds,category=category,n=len(group),decoders=[r['decoder'] for r in group],
                stats={key:dict(min=min(v),median=statistics.median(v),max=max(v)) for key in ['playable_ms','closure_ms','planning_ms','boundary_ms','copy_aac_mux_ms','validation_ms','overlay_after_ms','cpu_ms','temporary_disk_bytes'] if (v:=[r[key] for r in group if r[key] is not None])}))
    dump(root/'summary.json',summary)
if __name__=='__main__':main()
