"""Production strategy fixtures. Research helpers only create/verify media; they
never implement the tested exporter. All writes stay inside task artifacts."""
import dataclasses, json, math, subprocess, sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'HybridAv1Prototype'))
import prototype as p
from run_checks import snap, pcm_intervals
from verify import validate, thumbnails
REPO=p.REPO
DLL=REPO/'dev/HybridExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll'
ROOT=REPO/'artifacts/hybrid-phase3b'

def dump(path,value):
    path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(value,indent=2),encoding='utf-8')
def job(snapshot,folder):
    folder.mkdir(parents=True,exist_ok=True)
    manifest=folder/'source.txt'
    p.manifest(manifest,[(Path(r.path),round((r.source_end-r.source_start)*snapshot.fps)) for r in snapshot.intervals],snapshot.fps)
    audio=p.audio_args(snapshot);idx=audio.index('-filter_complex') if '-filter_complex' in audio else -1
    trim=f'[0:v]trim=start={snapshot.intervals[0].start-snapshot.intervals[0].source_start:.6f}:duration={snapshot.duration:.6f},setpts=PTS-STARTPTS[v]'
    if idx>=0:audio[idx+1]=trim+';'+audio[idx+1]
    else:audio[:0]=['-filter_complex',trim]
    audio[audio.index('0:v:0')]='[v]'
    args=[*p.BASE,'-f','concat','-safe','0','-i',str(manifest),*audio,'-c:v','av1_nvenc','-preset','p4','-tune','hq','-rc','vbr','-cq','27','-b:v','0','-maxrate','15M','-bufsize','30M','-bf','0','-r',str(snapshot.fps),'-t',f'{snapshot.duration:.6f}','-y',str(folder/'unpublished.mkv')]
    data=dataclasses.asdict(snapshot)
    for key in ['previous_cutoff','epoch']:
        if not math.isfinite(data[key]):data[key]=None
    dump(folder/'job.json',dict(snapshot=data,arguments=args))
    return folder
def run(folder,mode='hybrid',plan=False):
    result=subprocess.run([str(p.DOTNET),str(DLL),'plan' if plan else 'export',str(folder),mode],capture_output=True,text=True,timeout=300,creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:raise RuntimeError(result.stderr)
    return json.loads((folder/('plan.json' if plan else 'results.json')).read_text(encoding='utf-8'))

def main():
    root=ROOT/(sys.argv[1] if len(sys.argv)>1 else 'checks-v1');root.mkdir(parents=True,exist_ok=True)
    p.ROOT=ROOT
    fixture=REPO/'artifacts/hybrid-phase3a/fixtures/framegrid-v2'
    checks=[]
    def check(ok,name,details=None):
        checks.append(dict(passed=bool(ok),name=name,details=details));dump(root/'progress.json',checks)
        if not ok:raise AssertionError(name+' '+str(details))
    cases=[('aligned',0,4,False),('one-after',.033,5.25,False),('mid-gop',1,5.25,False),('one-before',1.967,5.25,False),('tail',.5,3.15,False),('previous-cutoff',1.1,5.25,False),('gap',1,5.25,True)]
    for name,start,end,gap in cases:
        folder=root/name
        if (folder/'verification.json').exists():continue
        snapshot=pcm_intervals(snap(fixture,start,end,gap),folder/'pcm')
        snapshot=dataclasses.replace(snapshot,mix_inputs=(0,1),previous_cutoff=start if name=='previous-cutoff' else -math.inf)
        result=run(job(snapshot,folder));decoder=result['result']['Export']['Video']['Decoder']
        check(decoder==('packet-copy-av1' if name=='aligned' else 'hybrid-av1'),name+' preferred production strategy',result['result'])
        plan=p.make_plan(snapshot);export=dict(output=result['Output'],frames=result['PlannedFrames'] or sum(x.stop-x.first for x in plan.pieces))
        evidence=validate(plan,export,folder);check(evidence['passed'],name+' independent packets/frames/AAC/PTS',evidence)
        raw=thumbnails(export['output'],0,export['frames'])
        codes=[sum((1<<bit) if frame[:17,bit*24+3:bit*24+21].mean()>125 else 0 for bit in range(8)) for frame in raw]
        expected=[round(r.source_start*30)+n for r,piece in zip(snapshot.intervals,plan.pieces) for n in range(piece.first,piece.stop)]
        check(codes==expected,name+' lossy boundary source frame IDs, no stale/duplicate/gap')
        check(not list(folder.glob('.hybrid-*')),name+' attempt cleanup')
    if len(sys.argv)>2 and sys.argv[2]=='smoke':return
    base=dataclasses.replace(snap(fixture,1,5.25),audio=())
    for mode in ['default','head-fail','decoder-fail','mux-fail','head-corrupt','mux-corrupt','interrupted','software','validation-software','no-space','missing-scratch','cancel','locked']:
        folder=root/mode;result=run(job(base,folder),mode)
        if mode in ['cancel','locked']:
            check(result['passed'] and result['sourceRetained'] and not result['published'],mode+' retained snapshot/no publication')
            if mode=='cancel':check(not any('video-export-attempt-started'==s['stage'] for s in result['stages']),'Cancellation never retries')
        else:
            decoder=result['result']['Export']['Video']['Decoder']
            check(decoder==('hybrid-av1' if mode=='validation-software' else 'software-default' if mode=='software' else 'nvdec-av1'),mode+' exact-snapshot fallback',result['result'])
            if mode not in ['default','validation-software']:check(result['result']['HybridFallback'],mode+' rejected attempt recorded')
            p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',result['Output'],'-map','0','-f','null','-'])
            check(True,mode+' independently fully decoded')
        check(not list(folder.glob('.hybrid-*')),mode+' no abandoned scratch')
    for name,changes in [('history',dict(history_start=1.5)),('cutoff',dict(previous_cutoff=1.5)),('acceptance',dict(accepted=5)),('epoch',dict(epoch=math.inf))]:
        if name=='epoch':continue # JSON is finite; native epoch check covered separately.
        result=run(job(dataclasses.replace(base,**changes),root/('reject-'+name)),plan=True)
        check(not result['Eligible'],name+' snapshot violation rejected',result)
    ambiguous=dataclasses.replace(snap(fixture,.016,3.15),audio=())
    check(not run(job(ambiguous,root/'reject-rounding'),plan=True)['Eligible'],'Ambiguous legacy CFR rejected')
    ten=REPO/'artifacts/hybrid-phase3a/fixtures/ten'
    snapshot=p.Snapshot(4,.5,-math.inf,0,30,tuple(p.Interval(str(ten/f'part-{i:03d}.mkv'),i*2,i*2+2,max(.5,i*2),i*2+2) for i in range(2)),())
    result=run(job(snapshot,root/'main10'));check(result['result']['Export']['Video']['Decoder']=='hybrid-av1','Main10 direct CUDA head',result['result'])
    from verify import frame_rows
    folder=root/'main10'
    source,_=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-f','concat','-safe','0','-i',folder/'source.txt','-vf','trim=start=0.5:duration=3.5,setpts=PTS-STARTPTS,format=yuv420p10le','-r','30','-t','3.5','-f','framemd5','-'])
    actual,_=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',result['Output'],'-vf','format=yuv420p10le','-r','30','-f','framemd5','-'])
    src=frame_rows(source);dst=frame_rows(actual)
    check(len(src)==len(dst)==105 and [r[1:4] for r in src]==[r[1:4] for r in dst] and src[45:]==dst[45:],'Main10 independently decoded copied samples/PTS')
    raw=thumbnails(result['Output'],0,105)
    codes=[sum((1<<bit) if frame[:17,bit*24+3:bit*24+21].mean()>125 else 0 for bit in range(8)) for frame in raw]
    check(codes==list(range(15,120)),'Main10 decoded source frame IDs and transition')
    for name,file in [('hidden','artifacts/hybrid-phase3a/fixtures/alternate-reference.mkv'),('headers','artifacts/gop-phase2/final-release-v4/media/changed-private.mkv'),('timing','artifacts/hybrid-phase3a/tests/nonmonotonic.mkv'),('malformed','artifacts/hybrid-phase3a/tests/truncated.mkv')]:
        path=REPO/file
        if not path.exists():continue
        altered=dataclasses.replace(base,intervals=(dataclasses.replace(base.intervals[0],path=str(path)),*base.intervals[1:]))
        check(not run(job(altered,root/('reject-'+name)),plan=True)['Eligible'],name+' actual unsupported stream rejected')
    # Main8/Main10 and color changes in an otherwise eligible interval.
    for name,path in [('bitdepth',ten/'part-001.mkv'),('color',REPO/'artifacts/gop-phase2/final-release-v4/media/color.mkv')]:
        altered=dataclasses.replace(base,intervals=(base.intervals[0],dataclasses.replace(base.intervals[1],path=str(path)),*base.intervals[2:]))
        check(not run(job(altered,root/('reject-'+name)),plan=True)['Eligible'],name+' mixed metadata rejected')
    for mode in ['mutate-video','mutate-pcm']:
        import shutil
        folder=root/mode;folder.mkdir(parents=True,exist_ok=True)
        snapshot=pcm_intervals(snap(fixture,1,5.25),folder/'pcm') if mode=='mutate-pcm' else base
        video=folder/'owned-mutable.mkv';shutil.copy2(snapshot.intervals[0].path,video)
        snapshot=dataclasses.replace(snapshot,intervals=(dataclasses.replace(snapshot.intervals[0],path=str(video)),*snapshot.intervals[1:]))
        result=run(job(snapshot,folder),mode)
        check(result['sourceRetained'] and not result['published'] and 'изменились' in result['failure'],mode+' replaced accepted file identity rejected')
        check(not (folder/'unpublished.mkv').exists() and not list(folder.glob('.hybrid-*')),mode+' no unpublished output/scratch retained')
    source=REPO/'artifacts/gop-phase2/benchmark/snapshot-15-A-copy-1'
    snapshot=p.Snapshot(7.515001,.015,-math.inf,0,60,tuple(p.Interval(str(source/f'owned-{i:09d}.mkv'),i*2,i*2+2,max(.015,i*2),min(7.515001,i*2+2)) for i in range(4)),())
    rejection=run(job(snapshot,root/'reject-450-451'),plan=True)
    check(not rejection['Eligible'] and rejection['Reason']=='ambiguous-legacy-cfr-boundary','Actual 450-source-frame vs 451-legacy-CFR interval rejected',rejection)
    dump(root/'results.json',dict(passed=True,count=len(checks),checks=checks))
    print('Passed',len(checks))
if __name__=='__main__':main()
