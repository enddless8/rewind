import argparse, dataclasses, json, math, os, time
from pathlib import Path
import numpy as np
import prototype as p
from verify import validate

CHECKS=[]
def check(condition,name,details=None):
    CHECKS.append(dict(passed=bool(condition),name=name,details=details))
    p.dump(p.ROOT/'checks-progress.json',CHECKS)
    if not condition: raise AssertionError(name)

def reject(action,name):
    try: action()
    except (ValueError,RuntimeError): check(True,name); return
    check(False,name)

def fixture():
    folder=p.ROOT/'fixtures'/'framegrid-v2'; folder.mkdir(parents=True,exist_ok=True)
    raw=folder/'indexed.yuv'
    if not (folder/'part-002.mkv').exists():
        x=np.arange(640)[None,:]; y=np.arange(360)[:,None]
        with raw.open('wb') as out:
            for n in range(180):
                image=((x*3+y*5+n*7)%192+32).astype(np.uint8)
                for bit in range(8): image[:60,bit*80:(bit+1)*80]=235 if n & (1<<bit) else 16
                out.write(image.tobytes()); out.write(bytes([128])*(640*360//2))
        p.execute(p.FF,[*p.BASE,'-f','rawvideo','-pixel_format','yuv420p','-video_size','640x360','-framerate','30','-i',raw,
                        '-vf','setsar=1','-an','-c:v','av1_nvenc','-preset','p4','-tune','hq','-rc','vbr','-cq','27','-b:v','0','-maxrate','15M','-bufsize','30M',
                        '-bf','0','-g','60','-force_key_frames','expr:gte(t,n_forced*2)','-r','30','-f','segment','-segment_time','2',
                        '-reset_timestamps','1','-y',folder/'part-%03d.mkv'])
        raw.unlink()
    for name,freq in [('System',440),('Microphone',880),('Discord',1320)]:
        if not (folder/f'{name}.wav').exists():
            p.execute(p.FF,[*p.BASE,'-f','lavfi','-i',f'sine=frequency={freq}:sample_rate=48000:duration=6','-af',
                            "volume=0:enable='between(t,1.5,2)'" if name=='Microphone' else 'anull','-ac','2','-c:a','pcm_s16le','-y',folder/f'{name}.wav'])
    return folder

def snap(folder,start,end, gap=False):
    ranges=[]
    for i in range(3):
        if gap and i==1: continue
        left=i*2; right=left+2
        if left<end and right>start: ranges.append(p.Interval(str(folder/f'part-{i:03d}.mkv'),left,right,max(start,left),min(end,right)))
    return p.Snapshot(end,start,-math.inf,0,30,tuple(ranges),tuple(str(folder/f'{n}.wav') for n in ['System','Microphone','Discord']))

def pcm_intervals(snapshot,directory):
    # Fixture-only equivalent of production JoinAudio gap removal; WAV input is already post gain/mute.
    directory.mkdir(parents=True,exist_ok=True); files=[]
    for path in snapshot.audio:
        filters=';'.join(f'[0:a]atrim=start={r.start:.6f}:end={r.end:.6f},asetpts=PTS-STARTPTS[a{i}]' for i,r in enumerate(snapshot.intervals))
        filters+=';'+''.join(f'[a{i}]' for i in range(len(snapshot.intervals)))+f'concat=n={len(snapshot.intervals)}:v=0:a=1[out]'
        out=directory/Path(path).name
        p.execute(p.FF,[*p.BASE,'-i',path,'-filter_complex',filters,'-map','[out]','-c:a','pcm_s16le','-y',out]); files.append(str(out))
    return dataclasses.replace(snapshot,audio=tuple(files))

def tests():
    folder=fixture(); paths=[str(folder/f'part-{i:03d}.mkv') for i in range(3)]; p.preindex(paths)
    text,_=p.execute(p.DOTNET,[p.INDEX,'guards',p.index(paths[0])['CodecPrivate']])
    for item in json.loads(text): check(item['passed'],item['name'])
    cases=[('aligned',0,4,False),('one-after',.033,5.25,False),('half-gop',1,5.25,False),
           ('one-before',1.967,5.25,False),('end-inside',0,3.15,False),('previous-cutoff',1.1,5.25,False),('gap',1,5.25,True)]
    for name,start,end,gap in cases:
        dest=p.ROOT/'tests'/name
        snapshot=pcm_intervals(snap(folder,start,end,gap),dest/'pcm')
        if name=='previous-cutoff': snapshot=dataclasses.replace(snapshot,previous_cutoff=start)
        plan=p.make_plan(snapshot); p.dump(dest/'accepted-snapshot.json',dataclasses.asdict(snapshot)); result=p.export(plan,dest)
        verification=validate(plan,result,dest)
        check(verification['passed'],name+' exact frames/packets/audio/PTS',verification)
        for boundary in verification['boundary_checks']: check(boundary['ambiguous_static_frames']==0,name+' unique boundary frame identity')
        # Decode binary source frame labels after lossy re-encoding, not only PSNR.
        from verify import thumbnails
        raw=thumbnails(result['output'],0,result['frames']); codes=[]
        for frame in raw:
            code=sum((1<<bit) if frame[:17,bit*24+3:bit*24+21].mean()>125 else 0 for bit in range(8)); codes.append(code)
        expected=[round(r.source_start*30)+n for r,piece in zip(snapshot.intervals,plan.pieces) for n in range(piece.first,piece.stop)]
        check(codes==expected,name+' decoded embedded source frame indices/no old history',dict(first=codes[0],last=codes[-1],count=len(codes)))
    source=snap(folder,0,4); original=p.make_plan(source)
    reject(lambda:p.make_plan(dataclasses.replace(source,history_start=.5)),'HistoryStart violation rejects before export')
    reject(lambda:p.make_plan(dataclasses.replace(source,previous_cutoff=.5)),'Previous cutoff violation rejects before export')
    reject(lambda:p.make_plan(dataclasses.replace(source,accepted=3.9)),'Acceptance overrun rejects before export')
    reject(lambda:p.make_plan(dataclasses.replace(source,accepted=math.nan)),'NaN acceptance rejected')
    reject(lambda:p.make_plan(dataclasses.replace(source,previous_cutoff=math.inf)),'Invalid previous cutoff rejected')
    reject(lambda:p.make_plan(snap(folder,.016,3.15)),'Ambiguous legacy frame rounding rejected')
    reject(lambda:p.export(p.make_plan(snap(folder,1,4)),p.ROOT/'tests'/'ineligible-copy','copy'),'Phase 2 copy rejects inside-GOP start')
    # Reuse native negative structures from the committed Phase 2 fixtures.
    old=p.REPO/'artifacts/gop-phase2/final-release-v4/media'
    for file in ['changed-private.mkv','unsupported-preroll.mkv','h264_nvenc-different.mkv','hevc_nvenc-different.mkv']:
        reject(lambda file=file:p.index(old/file),'Unsupported source '+file+' rejected')
    a=p.index(paths[0])
    check(not p.compatible(a,p.index(old/'ten-bit.mkv')),'8/10 bit and resolution difference incompatible')
    check(not p.compatible(a,p.index(old/'color.mkv')),'Colour metadata difference incompatible')
    # Real Main10 single segment: head transcode+remaining independent segment copy.
    ten=p.ROOT/'fixtures'/'ten'; ten.mkdir(exist_ok=True)
    for i in range(2):
        target=ten/f'part-{i:03d}.mkv'
        p.execute(p.FF,[*p.BASE,'-i',paths[i],'-vf','format=p010le','-an','-c:v','av1_nvenc','-preset','p4','-tune','hq',
                        '-rc','vbr','-cq','27','-b:v','0','-maxrate','15M','-bufsize','30M','-bf','0','-g','60','-r','30','-y',target])
    ten_snapshot=p.Snapshot(4,.5,-math.inf,0,30,tuple(p.Interval(str(ten/f'part-{i:03d}.mkv'),i*2,i*2+2,max(.5,i*2),i*2+2) for i in range(2)),())
    plan=p.make_plan(ten_snapshot); result=p.export(plan,p.ROOT/'tests'/'ten-bit')
    # Fully decoded Main10 output and unchanged copied packets. Raw hashes in 10-bit directly.
    from verify import md5
    expected,_=md5(ten/'part-001.mkv',p.ROOT/'tests'/'ten-bit'/'source-10.framemd5',fps=30)
    actual,_=md5(result['output'],p.ROOT/'tests'/'ten-bit'/'actual-10.framemd5',fps=30)
    check([r[-1] for r in expected]==[r[-1] for r in actual[45:]],'Main10 copied interior raw decoded hashes identical')
    check(p.probe(result['output'])['streams'][0]['pix_fmt']=='yuv420p10le','Main10 output retained 10-bit')
    # Invalid/interrupted output cannot pass the unchanged bitstream/container index.
    invalid=p.ROOT/'tests'/'truncated.mkv'; invalid.write_bytes(Path(result['output']).read_bytes()[:400])
    reject(lambda:p.index(invalid),'Interrupted/truncated output rejected')
    def vint(data,pos,identifier=False):
        first=data[pos]; length=next(i for i in range(1,9) if first & (1<<(8-i)))
        value=first if identifier else first & ((1<<(8-length))-1)
        for b in data[pos+1:pos+length]: value=value*256+b
        return value,length
    def blocks(data,start,end):
        pos=start
        while pos<end:
            identifier,n=vint(data,pos,True); pos+=n; size,n=vint(data,pos); pos+=n
            stop=end if size==(1<<(7*n))-1 else pos+size
            if identifier in [0x18538067,0x1f43b675]: yield from blocks(data,pos,stop)
            elif identifier==0xa3: yield pos
            pos=stop
    data=bytearray(Path(paths[0]).read_bytes()); offsets=list(blocks(data,0,len(data)))
    value,n=vint(data,offsets[1]); data[offsets[1]+n:offsets[1]+n+2]=bytes(2)
    bad=p.ROOT/'tests'/'nonmonotonic.mkv'; bad.write_bytes(data)
    reject(lambda:p.index(bad),'Real duplicate/non-monotonic SimpleBlock PTS rejected')
    mutable=p.ROOT/'tests'/'identity-change.mkv'; mutable.write_bytes(Path(paths[0]).read_bytes())
    mutable_snapshot=p.Snapshot(2,0,-math.inf,0,30,(p.Interval(str(mutable),0,2,0,2),),())
    frozen=p.make_plan(mutable_snapshot)
    with mutable.open('ab') as stream: stream.write(b'changed')
    reject(lambda:p.export(frozen,p.ROOT/'tests'/'changed-output'),'Changed owned source identity rejected before processing')
    p.dump(p.ROOT/'tests'/'results.json',dict(passed=True,count=len(CHECKS),checks=CHECKS))
    print('Tests passed:',len(CHECKS),flush=True)

if __name__=='__main__':
    try: tests()
    except BaseException as exc:
        p.dump(p.ROOT/'tests'/'failure.json',dict(error=str(exc),checks=CHECKS)); raise
