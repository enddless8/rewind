"""Same retained accepted sources/PCM/original args, actual production strategies.
Supplementary offline timing; it has no new Save acceptance or publication."""
import json, shutil, statistics, subprocess, sys
from pathlib import Path
from checks import ROOT,REPO,DLL,dump,run
sys.path.insert(0,str(REPO/'dev/HybridAv1Prototype'))
import prototype as p
from verify import frame_rows

def main():
    matrix=ROOT/(sys.argv[1] if len(sys.argv)>1 else 'live-matrix-v1')
    root=ROOT/'paired-v1';rows=[]
    durations=[int(s) for s in sys.argv[2].split(',')] if len(sys.argv)>2 else [15,30,60,120]
    def tracks(rows):return {k:[r for r in rows if r[0]==k] for k in {r[0] for r in rows}}
    def completed(seconds,strategy,iteration):
        folder=root/f'{seconds}-{strategy}-{iteration}';pointer=folder/'completed-attempt.json'
        return Path(json.loads(pointer.read_text())['folder']) if pointer.exists() else folder
    for seconds in durations:
        source_folder=matrix/f'{seconds}-C-1'
        if (source_folder/'completed-attempt.json').exists():source_folder=Path(json.loads((source_folder/'completed-attempt.json').read_text())['folder'])
        source=source_folder/'retained';job=json.loads((source/'job.json').read_text(encoding='utf-8'))
        for iteration in range(1,4):
            for strategy in (['nvdec','hybrid'] if iteration%2 else ['hybrid','nvdec']):
                base=root/f'{seconds}-{strategy}-{iteration}';folder=completed(seconds,strategy,iteration);folder.mkdir(parents=True,exist_ok=True)
                if (folder/'excluded-timing.json').exists() or not (folder/'results.json').exists() and (folder/'job.json').exists():
                    attempt=2
                    while (base/f'attempt-{attempt}').exists():attempt+=1
                    folder=base/f'attempt-{attempt}';folder.mkdir()
                if not (folder/'results.json').exists():
                    dump(folder/'job.json',job);run(folder,strategy)
                    if folder!=base:dump(base/'completed-attempt.json',dict(folder=str(folder)))
                result=json.loads((folder/'results.json').read_text(encoding='utf-8'))
                rows.append(dict(seconds=seconds,strategy=strategy,iteration=iteration,ms=result['totalMs'],decoder=result['result']['Export']['Video']['Decoder'],folder=str(folder)))
                dump(root/'runs.json',rows);print(seconds,strategy,iteration,round(result['totalMs'],3),flush=True)
        # Every repeated output fully decoded independently. Lossy pixels need
        # not be byte-identical; copied pixels and decoded AAC must be exact.
        for strategy in ['nvdec','hybrid']:
            reference=None
            for iteration in range(1,4):
                folder=completed(seconds,strategy,iteration);output=folder/'unpublished.mkv'
                if (folder/'independent-decode.json').exists():continue
                out,_=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',output,'-map','0','-f','framemd5','-'])
                frames=frame_rows(out)
                current=tracks(frames)
                if reference is not None:
                    if {k:v for k,v in current.items() if k!='0'}!={k:v for k,v in reference.items() if k!='0'}:raise AssertionError('Nonreproducible AAC samples/PTS')
                    if [r[:4] for r in current['0']]!=[r[:4] for r in reference['0']]:raise AssertionError('Nonreproducible video timing/count')
                    encoded=json.loads((folder/'results.json').read_text(encoding='utf-8'))['result']['EncodedFrames']
                    if strategy=='hybrid' and current['0'][encoded:]!=reference['0'][encoded:]:raise AssertionError('Copied pixels differ')
                reference=current;dump(folder/'independent-decode.json',dict(passed=True,streams=len({r[0] for r in frames}),video_frames=sum(r[0]=='0' for r in frames),lossy_pixel_byte_equality_required=False))
        # Compare AAC packets/decoded samples/PTS across strategies, including
        # first/last AAC frames (priming/padding). Full video is deliberately lossy.
        if (root/f'{seconds}-audio-integrity.json').exists():continue
        a=completed(seconds,'nvdec',1)/'unpublished.mkv';b=completed(seconds,'hybrid',1)/'unpublished.mkv'
        def audio(path):
            out,_=p.execute(p.LAB,[*p.BASE,'-i',path,'-map','0:a','-f','framemd5','-']);return frame_rows(out)
        if tracks(audio(a))!=tracks(audio(b)):raise AssertionError('Same-snapshot AAC mismatch')
        dump(root/f'{seconds}-audio-integrity.json',dict(passed=True,decoded_samples_pts_priming_padding=True))
    summary=[]
    for seconds in durations:
        for strategy in ['nvdec','hybrid']:
            v=[r['ms'] for r in rows if r['seconds']==seconds and r['strategy']==strategy]
            summary.append(dict(seconds=seconds,strategy=strategy,n=len(v),min_ms=min(v),median_ms=statistics.median(v),max_ms=max(v)))
    dump(root/'summary.json',summary)
if __name__=='__main__':main()
