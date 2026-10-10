"""Native finalizer fixtures, packet/frame/audio verification and offline benchmarks."""
import json,math,os,subprocess,sys,time
from pathlib import Path
import numpy as np

REPO=Path(__file__).resolve().parents[2]
ROOT=Path(os.environ.get('REWIND_RECORDING_TEST_ROOT',REPO/'artifacts/unified-stage1')).resolve()
if (REPO/'artifacts').resolve() not in ROOT.parents:
    raise ValueError('Test output разрешён только внутри artifacts')
DOTNET=REPO/'.tools/dotnet/dotnet.exe'
HARNESS=REPO/'dev/RecordingExportChecks/bin/Release/net10.0-windows/FidelityChecks.dll'
FF=REPO/'.tools/ffmpeg/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
PROBE=FF.with_name('ffprobe.exe')
LAB=REPO/'artifacts/save-profile/decoder-lab/ffmpeg-9.0.2-full_build/bin/ffmpeg.exe'
SOURCE=REPO/'artifacts/hybrid-phase3a/fixtures/framegrid-v2'
REAL=REPO/'artifacts/hybrid-phase3a/live-final/snapshot-0'
CHECKS=[]
def dump(path,obj):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
def call(binary,args,timeout=2400,allowed=False):
    begin=time.perf_counter()
    child=subprocess.Popen([str(binary),*map(str,args)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
    try:out,err=child.communicate(timeout=timeout)
    except BaseException:
        child.kill();child.communicate();raise
    out,err=out.decode('utf-8',errors='replace'),err.decode('utf-8',errors='replace')
    if child.returncode and not allowed:raise RuntimeError(err[-5000:])
    return out,err,child.returncode,(time.perf_counter()-begin)*1000
def check(ok,name,details=None):
    CHECKS.append(dict(ok=bool(ok),name=name,details=details));dump(ROOT/'checks-progress.json',CHECKS)
    if not ok:raise AssertionError(name)
def fixture(name,seconds=6,start=0,end=None,real=False,tracks=('System','Microphone','Discord'),gap=False):
    folder=ROOT/name;folder.mkdir(parents=True,exist_ok=False)
    source=sorted((REAL if real else SOURCE).glob('owned-*.mkv' if real else 'part-*.mkv'))
    n=math.ceil(seconds/2); segments=[]
    for i in range(n):
        target=folder/f'video-{i:09}.mkv';os.link(source[i%len(source)],target)
        segments.append(dict(Path=target.name,Start=i*2+(1 if gap and i>0 else 0),End=i*2+2+(1 if gap and i>0 else 0)))
    # Real post-processing PCM fixtures: distinct tones and intentional microphone silence.
    frames=int(seconds*48000); t=np.arange(48000)/48000
    for i,track in enumerate(tracks):
        mono=np.round(np.sin(2*np.pi*(440+i*220)*t)*5000).astype('<i2');block=np.repeat(mono[:,None],2,axis=1).tobytes()
        with (folder/f'{track}.pcm').open('wb') as out:
            for sec in range(math.ceil(seconds)):
                data=bytes(len(block)) if track=='Microphone' and sec%5==2 else block
                out.write(data[:min(48000,frames-sec*48000)*4])
    settings=dict(Fps=30,Height=720 if real else 360,Quality=27,MaxBitrateMbps=15,AudioMode='System',
                  OutputDirectory=str(folder/'output'),BufferDirectory=str(folder/'buffer'),GroupByApplication=False)
    manifest=dict(Version=1,Settings=settings,ApplicationName='Проверка записи',Encoder='av1_nvenc',Backend='fixture',
                  StartedAt=start,StoppedAt=seconds if end is None else end,State=4,Tracks=list(tracks),Warnings=[])
    dump(folder/'recording.json',manifest)
    (folder/'segments.jsonl').write_text(''.join(json.dumps(s)+'\n' for s in segments),encoding='utf-8')
    return folder
def finalize(folder,mode='fast'):
    call(DOTNET,[HARNESS,'finalize',folder,mode])
    return json.loads((folder/'results.json').read_text(encoding='utf-8'))
def packets(path,stream='v'):
    text,*_=call(PROBE,['-v','error','-select_streams',stream,'-show_packets','-show_data_hash','sha256',
                        '-show_entries','packet=stream_index,pts,dts,duration,data_hash','-of','json',path])
    return json.loads(text)['packets']
def frames(path,video=True):
    args=['-v','error','-xerror','-i',path,'-map','0:v:0' if video else '0:a','-fps_mode','passthrough','-f','framemd5','-']
    text,err,*_=call(LAB,args)
    if err.strip():raise AssertionError(err)
    return [tuple(x.strip() for x in line.split(',')) for line in text.splitlines() if line and not line.startswith('#')]
def verify(fast,legacy,folder):
    a,b=fast['clip']['Path'],legacy['clip']['Path'];meta=json.loads(Path(a).with_suffix('.json').read_text(encoding='utf-8'))
    export=meta['Export'];check(export['Strategy']!='legacy-reencode','Eligible real finalizer used packet reuse',export)
    av,bv=frames(a),frames(b)
    check(len(av)==len(bv),'Independent libdav1d decoded frame count equals legacy',len(av))
    check([r[1:5] for r in av]==[r[1:5] for r in bv],'Decoded native frame PTS/order equals legacy')
    check(frames(a,False)==frames(b,False),'All decoded AAC sample hashes/counts/PTS equal legacy')
    ap,bp=packets(a),packets(b)
    check([(p['pts'],p['dts']) for p in ap]==[(p['pts'],p['dts']) for p in bp],'Native packet PTS/DTS equals legacy')
    check(all(int(p['dts'])>int(q['dts']) for q,p in zip(ap,ap[1:])),'Strictly monotonic DTS')
    manifest=json.loads((folder/'recording.json').read_text(encoding='utf-8'));start=round(manifest['StartedAt'],6);end=round(manifest['StoppedAt'],6)
    source=[];source_frames=[]
    for s in map(json.loads,(folder/'segments.jsonl').read_text(encoding='utf-8').splitlines()):
        ps=packets(folder/s['Path']);fs=frames(folder/s['Path'])
        for p,f in zip(ps,fs):
            if start<=s['Start']+int(p['pts'])/1000<end:source.append(p);source_frames.append(f)
    encoded=export['EncodedFrames']
    check([p['data_hash'] for p in ap[encoded:]]==[p['data_hash'] for p in source[encoded:]],'Copied original packet payload hashes identical')
    check([f[-1] for f in av[encoded:]]==[f[-1] for f in source_frames[encoded:]],'Copied interior decoded frames identical')
    if encoded:
        # Framegrid-v2 encodes absolute source IDs in eight top-row patches.
        proc=subprocess.run([str(LAB),'-v','error','-xerror','-i',a,'-vf','crop=640:60:0:0,extractplanes=y','-frames:v',str(encoded),'-f','rawvideo','-'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW,check=True)
        values=np.frombuffer(proc.stdout,np.uint8).reshape(encoded,60,640)
        ids=[sum((int(frame[10:40,k*80+10:k*80+70].mean())>128)<<k for k in range(8)) for frame in values]
        first=int(next(p['pts'] for p in source)/1000*30+.5)
        check(ids==list(range(first,first+encoded)),'Lossy boundary retains exact source frame IDs',ids)
    dump(folder/'integrity.json',dict(passed=True,frames=len(av),copied=export['CopiedFrames'],encoded=encoded,audio_tracks=4))
def run_checks():
    cases=[('aligned',0,6),('one-after',.033,5.233),('half',1,5),('one-before',1.967,5.2),('tail',0,5.1)]
    for name,start,end in cases:
        a=fixture('checks/'+name+'-fast',start=start,end=end);b=fixture('checks/'+name+'-legacy',start=start,end=end)
        fast,legacy=finalize(a),finalize(b,'legacy');verify(fast,legacy,a)
        print(name,'passed',flush=True)
    for name,mode in [('retry-error','fail'),('retry-timeout','timeout'),('retry-corruption','corrupt'),('software-validation','decode-fail')]:
        folder=fixture('checks/'+name,start=0,end=5)
        result=finalize(folder,mode);metadata=json.loads(Path(result['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
        check(metadata['Export']['Fallback'] if mode!='decode-fail' else metadata['Export']['Strategy']=='av1-packet-copy',name)
        check((folder/'video-000000000.mkv').exists() and (folder/'System.pcm').exists(),'Retry retains owned media '+name)
        check(bool(frames(result['clip']['Path'])),'Retried output independently decodes '+name)
    folder=fixture('checks/cancel');result=finalize(folder,'cancel')
    check(result.get('cancelled') and not list((folder/'output').glob('*.mkv')),'Cancellation cannot publish or retry')
    check((folder/'video-000000000.mkv').exists(),'Cancellation retains recovery material')
    for name,start,end,gap in [('ambiguous',.016,3.15,False),('gap',0,6,True)]:
        folder=fixture('checks/'+name,start=start,end=end,gap=gap)
        text,*_=call(DOTNET,[HARNESS,'plan',folder]);check(not json.loads(text)['eligible'],'Unsafe plan rejected '+name,json.loads(text))
    dump(ROOT/'checks-results.json',dict(passed=True,count=len(CHECKS),checks=CHECKS))
def benchmark():
    rows=[]
    for seconds in (60,300,600):
        for mode in ('fast','legacy'):
            folder=fixture(f'benchmarks/{seconds}-{mode}',seconds,start=1,end=seconds,real=True,tracks=('System','Microphone'))
            result=finalize(folder,mode);path=result['clip']['Path'];metadata=json.loads(Path(path).with_suffix('.json').read_text(encoding='utf-8'))
            rows.append(dict(seconds=seconds,mode=mode,output=path,export=metadata['Export'],totalMs=result['totalMs'],stages=result['stages']))
            dump(ROOT/'benchmark.json',rows);print(seconds,mode,result['totalMs'],metadata['Export'],flush=True)
    return rows
if __name__=='__main__':
    ROOT.mkdir(parents=True,exist_ok=True)
    if len(sys.argv)>1 and sys.argv[1]=='benchmark':benchmark()
    else:run_checks()
