import json
import dataclasses
import math
import numpy as np
from pathlib import Path
import prototype as p
from verify import frame_rows, md5

def main():
    checks=[]
    def check(value,name,details=None):
        checks.append(dict(passed=bool(value),name=name,details=details))
        if not value: raise AssertionError(name)
    for name in ['aligned','one-after','half-gop','one-before','end-inside','previous-cutoff','gap','ten-bit']:
        folder=p.ROOT/'tests'/name; output=folder/'unpublished.mkv'
        _,record=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',output,'-map','0','-f','null','-'])
        text,_=p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',output,'-map','0:v:0','-r','30','-f','framemd5','-'])
        reference=folder/('actual-10.framemd5' if name=='ten-bit' else 'actual.framemd5')
        check(frame_rows(text)==frame_rows(reference.read_text(encoding='utf-8')),name+' independent libdav1d raw hashes/PTS and full audio decode')
    source=p.index(p.ROOT/'fixtures/framegrid-v2/part-000.mkv')
    real=p.index(p.REPO/'artifacts/gop-phase2/benchmark/snapshot-15-A-copy-1/owned-000000000.mkv')
    check(not p.compatible(source,real),'Changed resolution / level / color metadata cannot join')
    # Noncanonical aspect metadata is deliberately rejected, not stripped to improve eligibility.
    weird=p.index(p.ROOT/'fixtures/part-000.mkv')
    check(not p.compatible(source,weird),'Different DisplayUnit metadata remains incompatible')
    interval=p.Interval(str(p.ROOT/'fixtures/framegrid-v2/part-000.mkv'),0,2,0,2,epoch=1)
    try: p.make_plan(p.Snapshot(2,0,-math.inf,0,30,(interval,),()))
    except ValueError: check(True,'Mismatched accepted/source epoch rejected before export')
    else: raise AssertionError('Epoch isolation failed')
    try: p.artifact_directory(p.REPO/'dist'/'not-a-prototype-output')
    except ValueError: check(True,'Prototype cannot write into production/dist or outside artifacts')
    else: raise AssertionError('Output isolation failed')
    from run_checks import snap,pcm_intervals
    from verify import validate
    routing_folder=p.ROOT/'tests'/'discord-excluded-from-mix'
    routed=pcm_intervals(snap(p.ROOT/'fixtures/framegrid-v2',.5,3.15),routing_folder/'pcm')
    routed=dataclasses.replace(routed,mix_inputs=(0,1))
    result=p.export(p.make_plan(routed),routing_folder)
    check(validate(p.make_plan(routed),result,routing_folder)['passed'],'System mode excludes Discord from Mix but preserves all four AAC tracks')
    args=p.audio_args(routed);filters=args[args.index('-filter_complex')+1]
    check(filters.startswith('[1:a][2:a]amix=inputs=2') and '3:a:0' in args,'Original frozen audio routing retained, Discord remains separate')
    bad_route=dataclasses.replace(routed,mix_inputs=(0,4))
    try:p.make_plan(bad_route)
    except ValueError:check(True,'Invalid audio routing rejected before processing')
    else:raise AssertionError('Bad routing accepted')
    mutable_wave=p.ROOT/'tests'/'mutable-pcm.wav';mutable_wave.write_bytes(p.Path(routed.audio[0]).read_bytes())
    mutable_snapshot=dataclasses.replace(routed,audio=(str(mutable_wave),),mix_inputs=(0,))
    frozen_pcm=p.make_plan(mutable_snapshot)
    with mutable_wave.open('ab') as stream:stream.write(b'changed')
    try:p.export(frozen_pcm,p.ROOT/'tests'/'mutated-pcm-output')
    except ValueError:check(True,'Immutable PCM identity change rejected before export')
    else:raise AssertionError('Changed PCM accepted')
    from verify import thumbnails
    ten=p.ROOT/'tests'/'ten-bit'/'unpublished.mkv'
    before=thumbnails(p.ROOT/'fixtures/ten/part-000.mkv',15,60)
    after=thumbnails(ten,0,45)
    psnr=10*np.log10(255**2/np.maximum(((before-after)**2).mean(axis=(1,2)),1e-12))
    check(float(psnr.min())>30,'Main10 boundary content alignment/PSNR',dict(min_psnr_db=float(psnr.min()),frames=45))
    frames=thumbnails(ten,0,105)
    codes=[sum((1<<bit) if frame[:17,bit*24+3:bit*24+21].mean()>125 else 0 for bit in range(8)) for frame in frames]
    check(codes==list(range(15,120)),'Main10 decoded frame IDs/count/transition/no old history',dict(first=codes[0],last=codes[-1],count=len(codes)))
    ten_source=p.ROOT/'fixtures/ten'
    ten_snapshot=p.Snapshot(4,.5,-math.inf,0,30,tuple(p.Interval(str(ten_source/f'part-{i:03d}.mkv'),i*2,i*2+2,max(.5,i*2),i*2+2) for i in range(2)),())
    ten_reference=p.reference(ten_snapshot,p.ROOT/'tests'/'ten-bit'/'legacy-reference')
    check([x['Pts'] for x in p.index(ten)['Packets']]==[x['Pts'] for x in p.index(ten_reference['output'])['Packets']],
          'Main10 native packet PTS/frame count identical to full NVDEC reference')
    alternate=p.ROOT/'fixtures'/'alternate-reference.mkv'
    if not alternate.exists():
        p.execute(p.FF,[*p.BASE,'-f','lavfi','-i','testsrc2=size=320x180:rate=30:duration=3','-an','-c:v','libaom-av1',
                        '-cpu-used','8','-threads','4','-lag-in-frames','25','-auto-alt-ref','1','-crf','32','-b:v','0','-g','60','-y',alternate])
    p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',alternate,'-map','0','-f','null','-'])
    _,trace=p.execute(p.FF,['-hide_banner','-v','verbose','-i',alternate,'-map','0:v:0','-c:v','copy',
                           '-bsf:v','trace_headers','-f','null','-'],allow_failure=True)
    if trace['code']:raise AssertionError('Real alternate-reference bitstream invalid')
    import re
    hidden=len(re.findall(r'show_frame\s+0\s+=\s+0',trace['stderr']))
    existing=len(re.findall(r'show_existing_frame\s+1\s+=\s+1',trace['stderr']))
    check(hidden>0 and existing>0,'Valid libaom AV1 fixture actually contains hidden/show-existing frames',dict(hidden=hidden,show_existing=existing))
    p.dump(p.ROOT/'fixtures'/'alternate-reference-headers.json',dict(hidden=hidden,show_existing=existing,trace=trace['stderr']))
    try:p.index(alternate)
    except RuntimeError:check(True,'Valid hidden/show-existing AV1 file rejected by conservative hybrid index')
    else:raise AssertionError('Unsafe hidden-frame file unexpectedly eligible')
    high=p.ROOT/'fixtures'/'high-profile.mkv'
    if not high.exists():
        p.execute(p.FF,[*p.BASE,'-f','lavfi','-i','testsrc2=size=320x180:rate=30:duration=1','-vf','format=yuv444p','-an',
                        '-c:v','libaom-av1','-cpu-used','8','-threads','4','-crf','32','-b:v','0','-profile:v','1','-y',high])
    p.execute(p.LAB,[*p.BASE,'-c:v','libdav1d','-i',high,'-map','0','-f','null','-'])
    check(p.probe(high)['streams'][0]['profile']=='High','Real independently decoded AV1 High/4:4:4 fixture')
    try:p.index(high)
    except RuntimeError:check(True,'AV1 profile/chroma change rejected before hybrid processing')
    else:raise AssertionError('Incompatible High profile unexpectedly eligible')
    # Native cancellation: execute kills and reaps this child, output cannot be parsed/published.
    interrupted=p.ROOT/'tests'/'interrupted-live.mkv'
    try:
        p.execute(p.FF,[*p.BASE,'-re','-f','lavfi','-i','testsrc2=size=640x360:rate=30','-c:v','av1_nvenc','-t','60','-y',interrupted],timeout=.2)
    except Exception:
        try: p.index(interrupted)
        except Exception: check(True,'Actual FFmpeg timeout reaped process and rejected partial media')
        else: raise AssertionError('Partial media unexpectedly valid')
    else: raise AssertionError('Timeout did not occur')
    p.dump(p.ROOT/'tests'/'extra-results.json',dict(passed=True,count=len(checks),checks=checks))
    print('Extra checks passed:',len(checks))

if __name__=='__main__': main()
