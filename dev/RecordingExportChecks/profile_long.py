"""Stage 2: isolated, repeated wall/CPU/IO profiling of retained Stage 1 media."""
import ctypes,json,os,statistics,subprocess,sys,time,concurrent.futures
from pathlib import Path
import checks as c

ROOT=c.REPO/'artifacts/unified-stage2'
ROOT.mkdir(exist_ok=True)
kernel=ctypes.WinDLL('kernel32',use_last_error=True)
class IO(ctypes.Structure):
    _fields_=[(n,ctypes.c_ulonglong) for n in ('read_ops','write_ops','other_ops','read_bytes','write_bytes','other_bytes')]
kernel.GetProcessIoCounters.argtypes=[ctypes.c_void_p,ctypes.POINTER(IO)]
kernel.GetProcessTimes.argtypes=[ctypes.c_void_p,*([ctypes.POINTER(ctypes.c_ulonglong)]*4)]

def run(name,args):
    folder=ROOT/'components';folder.mkdir(exist_ok=True)
    resultfile=folder/(name+'.json')
    if resultfile.exists():return json.loads(resultfile.read_text(encoding='utf-8'))
    start=time.perf_counter()
    proc=subprocess.Popen([str(c.FF),'-hide_banner','-v','error','-xerror','-progress','pipe:1','-stats_period','1',*map(str,args)],
                          stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
    try:out,err=proc.communicate(timeout=600)
    except BaseException:proc.kill();proc.communicate();raise
    elapsed=(time.perf_counter()-start)*1000
    times=[ctypes.c_ulonglong() for _ in range(4)];io=IO()
    assert kernel.GetProcessTimes(int(proc._handle),*(ctypes.byref(x) for x in times))
    assert kernel.GetProcessIoCounters(int(proc._handle),ctypes.byref(io))
    result=dict(name=name,elapsedMs=elapsed,cpuMs=(times[2].value+times[3].value)/10000,
                io={n:getattr(io,n) for n,_ in IO._fields_},args=list(map(str,args)),code=proc.returncode,
                progress=out.decode('utf-8',errors='replace'),error=err.decode('utf-8',errors='replace'))
    c.dump(resultfile,result)
    print(name,round(elapsed,2),'ms CPU',round(result['cpuMs'],2),flush=True)
    if proc.returncode or err.strip():raise RuntimeError(str(result))
    return result

def source(seconds):
    folder=c.REPO/'artifacts/unified-stage1/benchmarks'/f'{seconds}-fast'
    dest=ROOT/'sources'/str(seconds);dest.mkdir(parents=True,exist_ok=True)
    concat=dest/'video.txt'
    if not concat.exists():
        head=dest/'head.mkv'
        c.call(c.FF,['-v','error','-xerror','-hwaccel','cuda','-hwaccel_output_format','cuda','-c:v','av1_cuvid',
            '-i',folder/'video-000000000.mkv','-vf','trim=start_frame=30:end_frame=60,setpts=PTS-STARTPTS','-an',
            '-c:v','av1_nvenc','-preset','p4','-tune','hq','-rc','vbr','-cq','27','-b:v','0','-maxrate','15M',
            '-bufsize','30M','-bf','0','-g','60','-r','30','-y',head])
        concat.write_text("file '"+head.as_posix()+"'\nduration 1.000000\n"+''.join(
            "file '"+p.as_posix()+"'\nduration 2.000000\n" for p in sorted(folder.glob('video-*.mkv'))[1:]),encoding='utf-8')
    saved=json.loads((folder/'results.json').read_text(encoding='utf-8'))
    original=next(s['details']['Arguments'] for s in saved['stages'] if s['stage']=='process-start-requested' and
                  any(a.startswith('setts=') for a in s['details']['Arguments']))
    args=original.copy();args[args.index('-i')+1]=str(concat)
    return folder,args

def profile(seconds,repetitions=3):
    folder,combined=source(seconds);duration=str(seconds-1);dest=ROOT/'components'
    video=['-f','concat','-safe','0','-i',combined[combined.index('-i')+1],'-map','0:v:0','-an','-c:v','copy',
           '-t',duration,'-bsf:v','setts=pts=round(N/(30*TB)):dts=round(N/(30*TB))']
    pcm=lambda track:['-f','s16le','-ar','48000','-ac','2','-i',folder/(track+'.pcm')]
    filt='atrim=start=0:duration='+duration+',asetpts=PTS-STARTPTS,apad=whole_dur='+duration
    audio=pcm('System')+pcm('Microphone')
    graph=combined[combined.index('-filter_complex')+1]
    # Audio-only graph uses indices 0/1, rather than combined video input 0.
    graph=graph.replace('[1:a]','[0:a]').replace('[2:a]','[1:a]')
    for repeat in range(repetitions):
        prefix=f'{seconds}-r{repeat}'
        run(prefix+'-video',video+['-y',dest/(prefix+'-video.mkv')])
        if seconds==300:run(prefix+'-video-probe-single',['-threads:v','1',*video,'-y',dest/(prefix+'-video-single.mkv')])
        for track in ('System','Microphone'):
            run(prefix+'-'+track,pcm(track)+['-af',filt,'-c:a','aac','-b:a','160k','-t',duration,'-y',dest/(prefix+'-'+track+'.mka')])
        run(prefix+'-filters',audio+['-filter_complex',graph,'-map','[mix]','-map','[a0]','-map','[a1]',
            '-c:a','pcm_s16le','-t',duration,'-f','null','-'])
        run(prefix+'-aac-all',audio+['-filter_complex',graph,'-map','[mix]','-map','[a0]','-map','[a1]',
            '-c:a','aac','-b:a','160k','-t',duration,'-y',dest/(prefix+'-aac.mka')])
        out=dest/(prefix+'-combined.mkv');args=combined[:-1]+[str(out)]
        run(prefix+'-combined',args)
        if seconds==300:
            run(prefix+'-combined-probe-single',['-threads:v','1',*combined[:-1],dest/(prefix+'-probe-single.mkv')])
            run(prefix+'-combined-serial',['-filter_complex_threads','1',*combined[:-1],dest/(prefix+'-serial.mkv')])
        run(prefix+'-mux-only',['-i',dest/(prefix+'-video.mkv'),'-i',dest/(prefix+'-aac.mka'),'-map','0:v','-map','1:a',
            '-c','copy','-y',dest/(prefix+'-mux.mkv')])
        run(prefix+'-validation',['-hwaccel','cuda','-hwaccel_output_format','cuda','-c:v','av1_cuvid','-i',out,
            '-map','0','-fps_mode:v','passthrough','-f','null','-'])
    summary()

def summary():
    groups={}
    for path in (ROOT/'components').glob('*.json'):
        r=json.loads(path.read_text(encoding='utf-8'))
        if 'name' not in r or 'elapsedMs' not in r:continue
        name=r['name'].split('-');key=name[0]+'-'+ '-'.join(name[2:])
        groups.setdefault(key,[]).append(r['elapsedMs'])
    rows={k:dict(n=len(v),medianMs=statistics.median(v),minMs=min(v),maxMs=max(v)) for k,v in sorted(groups.items())}
    c.dump(ROOT/'components-summary.json',rows)
    print(json.dumps(rows,indent=2),flush=True)

def probing(seconds,repetitions=3):
    folder,combined=source(seconds);dest=ROOT/'components'
    concat=Path(combined[combined.index('-i')+1]);limited=concat.with_name('video-limited.txt')
    if not limited.exists():
        # These sources are already structurally indexed. Inspect the cost of
        # repeating avformat_find_stream_info inside every concat child.
        limited.write_text(concat.read_text(encoding='utf-8').replace('\nduration ',
            '\noption analyzeduration 1\noption probesize 4096\nduration '),encoding='utf-8')
    args=combined.copy();args[args.index('-i')+1]=str(limited)
    video=['-f','concat','-safe','0','-i',limited,'-map','0:v:0','-an','-c:v','copy','-t',str(seconds-1),
           '-bsf:v','setts=pts=round(N/(30*TB)):dts=round(N/(30*TB))']
    for repeat in range(repetitions):
        prefix=f'{seconds}-r{repeat}'
        run(prefix+'-video-limited-probe',video+['-y',dest/(prefix+'-video-limited.mkv')])
        run(prefix+'-combined-limited-probe',args[:-1]+[dest/(prefix+'-limited.mkv')])
    summary()

def parser(seconds,repetitions=3):
    _,combined=source(seconds);dest=ROOT/'components';concat=Path(combined[combined.index('-i')+1]);raw=concat.with_name('video-no-parser.txt')
    if not raw.exists():raw.write_text(concat.read_text(encoding='utf-8').replace('\nduration ',
        '\noption fflags +noparse+nofillin\nduration '),encoding='utf-8')
    args=combined.copy();args[args.index('-i')+1]=str(raw)
    for repeat in range(repetitions):
        prefix=f'{seconds}-r{repeat}'
        run(prefix+'-video-no-parser',['-f','concat','-safe','0','-i',raw,'-map','0:v','-c:v','copy','-t',str(seconds-1),
            '-bsf:v','setts=pts=round(N/(30*TB)):dts=round(N/(30*TB))','-y',dest/(prefix+'-video-no-parser.mkv')])
        run(prefix+'-combined-no-parser',args[:-1]+[dest/(prefix+'-no-parser.mkv')])
    summary()

def parallel(seconds,repetitions=3):
    folder,combined=source(seconds);dest=ROOT/'components';duration=str(seconds-1)
    graph=combined[combined.index('-filter_complex')+1].replace('[1:a]','[0:a]').replace('[2:a]','[1:a]')
    inputs=[]
    for track in ('System','Microphone'):inputs+=['-f','s16le','-ar','48000','-ac','2','-i',folder/(track+'.pcm')]
    outputs=['mix','a0','a1']
    for repeat in range(repetitions):
        prefix=f'{seconds}-r{repeat}';start=time.perf_counter()
        if (dest/(prefix+'-parallel-total.json')).exists():continue
        def worker(target):
            filters=graph+''.join(';['+v+']anullsink' for v in outputs if v!=target)
            return run(prefix+'-parallel-'+target,inputs+['-filter_complex',filters,'-map','['+target+']',
                '-c:a','aac','-b:a','160k','-t',duration,'-y',dest/(prefix+'-'+target+'.mka')])
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            jobs=[pool.submit(worker,out) for out in outputs]
            jobs.append(pool.submit(run,prefix+'-parallel-video',['-f','concat','-safe','0','-i',combined[combined.index('-i')+1],
                '-map','0:v','-c:v','copy','-t',duration,'-bsf:v','setts=pts=round(N/(30*TB)):dts=round(N/(30*TB))',
                '-y',dest/(prefix+'-parallel-video.mkv')]))
            for job in jobs:job.result()
        mux=['-copyts','-i',dest/(prefix+'-parallel-video.mkv')]
        for out in outputs:mux+=['-i',dest/(prefix+'-'+out+'.mka')]
        mux+=['-map','0:v','-map','1:a','-map','2:a','-map','3:a','-c','copy','-avoid_negative_ts','disabled',
              '-y',dest/(prefix+'-parallel.mkv')]
        run(prefix+'-parallel-mux',mux)
        elapsed=(time.perf_counter()-start)*1000
        c.dump(dest/(prefix+'-parallel-total.json'),dict(name=prefix+'-parallel-total',elapsedMs=elapsed))
        print(prefix,'parallel TOTAL',round(elapsed,2),flush=True)
    summary()

def disk():
    rows=[];folder=c.REPO/'artifacts/unified-stage1/benchmarks/600-fast'
    for repeat in range(3):
        start=time.perf_counter();read=0
        for name in ('System.pcm','Microphone.pcm'):
            with (folder/name).open('rb') as stream:
                while data:=stream.read(8*1024*1024):read+=len(data)
        read_ms=(time.perf_counter()-start)*1000
        target=ROOT/f'disk-{repeat}.bin';assert not target.exists()
        block=bytes(8*1024*1024);start=time.perf_counter()
        with target.open('xb') as stream:
            for _ in range(8):stream.write(block)
            stream.flush();os.fsync(stream.fileno())
        write_ms=(time.perf_counter()-start)*1000
        rows.append(dict(repeat=repeat,readBytes=read,cachedReadMs=read_ms,readMBps=read/1e3/read_ms,
            writeBytes=len(block)*8,flushWriteMs=write_ms,writeMBps=len(block)*8/1e3/write_ms))
    c.dump(ROOT/'disk-throughput.json',rows);print(json.dumps(rows,indent=2))

if __name__=='__main__':
    if sys.argv[1]=='summary':summary()
    elif sys.argv[1]=='probe':probing(int(sys.argv[2]),int(sys.argv[3]) if len(sys.argv)>3 else 3)
    elif sys.argv[1]=='parser':parser(int(sys.argv[2]),int(sys.argv[3]) if len(sys.argv)>3 else 3)
    elif sys.argv[1]=='parallel':parallel(int(sys.argv[2]),int(sys.argv[3]) if len(sys.argv)>3 else 3)
    elif sys.argv[1]=='disk':disk()
    else:profile(int(sys.argv[1]),int(sys.argv[2]) if len(sys.argv)>2 else 3)
