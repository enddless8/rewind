"""Independent all-stream evidence for the native history/controls exercises."""
import json, math, sys
from pathlib import Path
from checks import ROOT,REPO,dump
sys.path.insert(0,str(REPO/'dev/HybridAv1Prototype'))
import prototype as p
from verify import frame_rows

def main():
    root=ROOT/sys.argv[1]
    data=json.loads((root/'results.json').read_text(encoding='utf-8'))
    second=json.loads((root/'second-save.json').read_text(encoding='utf-8'))
    conventional=json.loads((root/'conventional.json').read_text(encoding='utf-8'))
    if not data['CaptureRunning'] or not data['ConcurrentRecording']:raise AssertionError('Concurrent producer/recording stopped')
    if second['secondMeta']['ActualStart']<data['metadata']['AcceptedSaveBoundary']:raise AssertionError('Stale previous history')
    recording_meta=json.loads(Path(conventional['Path']).with_suffix('.json').read_text(encoding='utf-8'))
    if recording_meta['Export']['ParallelAudio']:raise AssertionError('Experimental Stage 2 enabled')
    jobs=json.loads((root/'compression-queue.json').read_text(encoding='utf-8'))
    if len(jobs)!=1 or jobs[0]['State']!='Completed' or not Path(jobs[0]['Source']).exists():raise AssertionError('MP4/original retention')
    evidence=[]
    for kind,clip in [('first',data['clip']),('second',second['second']),('recording',conventional),('mp4',dict(Path=jobs[0]['Output']))]:
        output=Path(clip['Path']);probe=p.probe(output)
        args=[*p.BASE]
        if probe['streams'][0]['codec_name']=='av1':args+=['-c:v','libdav1d']
        text,_=p.execute(p.LAB,[*args,'-i',output,'-map','0','-f','framemd5','-'])
        rows=frame_rows(text);video=[r for r in rows if r[0]=='0']
        if len({r[0] for r in rows})!=len(probe['streams']):raise AssertionError('Incomplete stream decode')
        if 'Duration' in clip and len(video)!=math.ceil(float(f"{clip['Duration']:.6f}")*60-1e-9):raise AssertionError('Legacy frame count')
        evidence.append(dict(kind=kind,path=str(output),frames=len(video),streams=len(probe['streams']),full_decode=True))
    if (root/'screenshot.json').exists():
        shot=json.loads((root/'screenshot.json').read_text(encoding='utf-8'))
        p.execute(p.LAB,[*p.BASE,'-i',shot['Path'],'-f','null','-'])
        if shot['Width']<=0 or shot['Height']<=0:raise AssertionError('Empty screenshot')
    dump(root/'independent-integrity.json',dict(passed=True,evidence=evidence,previous_cutoff=True,
        acceptance_delta=second['secondMeta']['AcceptedSaveBoundary']-data['metadata']['AcceptedSaveBoundary'],
        second_duration=second['second']['Duration'],conventional_strategy=recording_meta['Export']['Strategy'],
        stage2_parallel_audio=False,compression_at_second_save=second['compressionAtSecondSave'],original_mkv_retained=True))
    print(root.name,'native history/controls/recording/MP4 integrity passed',flush=True)
if __name__=='__main__':main()
