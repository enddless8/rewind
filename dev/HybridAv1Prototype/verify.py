"""Independent media checks: decoded frame content, packet PTS/DTS and every AAC track."""
import json, math, re, subprocess
from pathlib import Path
import numpy as np
import prototype as p

def frame_rows(text):
    return [tuple(x.strip() for x in line.split(',')) for line in text.splitlines() if line and not line.startswith('#')]

def md5(path,directory,video=True,source_filter=None,manifest=False,fps=60,duration=None):
    args=[*p.BASE]
    if video: args += p.GPU
    if manifest: args+=['-f','concat','-safe','0']
    args += ['-i',str(path),'-map','0:v:0' if video else '0:a']
    if video:
        fmt='p010le' if (not manifest and p.probe(path)['streams'][0]['pix_fmt']=='yuv420p10le') else 'nv12'
        filt=(source_filter+',' if source_filter else '')+f'hwdownload,format={fmt},format='+('yuv420p10le' if fmt=='p010le' else 'yuv420p')
        args += ['-vf',filt,'-r',str(fps)]
        if duration is not None: args+=['-t',f'{duration:.6f}']
    args += ['-f','framemd5','-']
    text,timing=p.execute(p.FF,args)
    Path(directory).write_text(text,encoding='utf-8'); return frame_rows(text),timing

def thumbnails(path,start,stop,source=True):
    # Per-frame tiny luma captures preserve order and make lossy boundary matching measurable.
    fmt='p010le' if p.probe(path)['streams'][0]['pix_fmt']=='yuv420p10le' else 'nv12'
    args=[*p.BASE,*p.GPU,'-i',str(path),'-vf',f'trim=start_frame={start}:end_frame={stop},hwdownload,format={fmt},scale=192:108,format=gray',
          '-an','-fps_mode','passthrough','-f','rawvideo','-']
    result=subprocess.run([str(p.FF),*args],capture_output=True,timeout=180,creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode or result.stderr: raise RuntimeError(result.stderr.decode())
    return np.frombuffer(result.stdout,np.uint8).reshape(-1,108,192).astype(np.float64)

def validate(plan,result,folder):
    folder=Path(folder); output=Path(result['output']); snapshot=plan.snapshot
    manifest=folder/'independent-source.txt'
    p.manifest(manifest,[(Path(r.path),round((r.source_end-r.source_start)*snapshot.fps)) for r in snapshot.intervals],snapshot.fps)
    filt=f'trim=start={snapshot.intervals[0].start-snapshot.intervals[0].source_start:.6f}:duration={snapshot.duration:.6f},setpts=PTS-STARTPTS'
    expected,_=md5(manifest,folder/'source-reference.framemd5',source_filter=filt,manifest=True,fps=snapshot.fps,duration=snapshot.duration)
    actual,_=md5(output,folder/'actual.framemd5',fps=snapshot.fps,duration=snapshot.duration)
    if len(expected)!=result['frames'] or len(actual)!=len(expected): raise AssertionError('Frame-count differs from original decoded trim')
    if [r[1:4] for r in actual] != [r[1:4] for r in expected]: raise AssertionError('Decoded frame PTS/DTS differ from decoded trim')
    copied=[]; cursor=0; boundary_checks=[]
    for piece in plan.pieces:
        boundary=piece.boundary_stop-piece.first
        for i in range(boundary,piece.stop-piece.first): copied.append(cursor+i)
        if boundary:
            src=thumbnails(piece.path,max(0,piece.first-2),min(piece.stop,piece.boundary_stop+2))
            dst=thumbnails(output,cursor,cursor+boundary)
            target=piece.first-max(0,piece.first-2); mse=((dst-src[target:target+boundary])**2).mean(axis=(1,2))
            psnr=10*np.log10(255**2/np.maximum(mse,1e-12))
            # Content alignment is exact where local source content is distinguishable;
            # identical/static adjacent source frames are explicitly marked ambiguous.
            mismatches=0; ambiguous=0
            for j,frame in enumerate(dst):
                choices=list(range(max(0,target+j-2),min(len(src),target+j+3)))
                distances=np.array([((frame-src[k])**2).mean() for k in choices]); desired=choices.index(target+j)
                minimum=distances.min()
                if distances[desired] > minimum+0.05: mismatches+=1
                if sum(abs(d-minimum)<0.05 for d in distances)>1: ambiguous+=1
            if float(psnr.min())<30 or mismatches: raise AssertionError(f'Boundary content misaligned: {mismatches}, PSNR {psnr.min()}')
            boundary_checks.append(dict(frames=boundary,min_psnr_db=float(psnr.min()),median_psnr_db=float(np.median(psnr)),index_mismatches=mismatches,ambiguous_static_frames=ambiguous))
        cursor+=piece.stop-piece.first
    if any(actual[i][-1]!=expected[i][-1] for i in copied): raise AssertionError('Copied interior decoded frame mismatch')
    ref=p.reference(snapshot,folder/'nvdec-reference')
    actual_index=p.index(output); reference_index=p.index(ref['output'])
    if [x['Pts'] for x in actual_index['Packets']] != [x['Pts'] for x in reference_index['Packets']]:
        raise AssertionError('Native MKV packet PTS differ from full decoded-trim reference')
    ref_frames,_=md5(ref['output'],folder/'nvdec-reference.framemd5',fps=snapshot.fps,duration=snapshot.duration)
    if len(ref_frames)!=len(actual) or [r[1:4] for r in ref_frames]!=[r[1:4] for r in actual]: raise AssertionError('Legacy NVDEC frame grid mismatch')
    audio_checks=[]
    if snapshot.audio:
        reference_audio,_=md5(ref['output'],folder/'reference-audio.framemd5',video=False)
        actual_audio,_=md5(output,folder/'actual-audio.framemd5',video=False)
        if actual_audio!=reference_audio: raise AssertionError('AAC decoded samples/PTS differ from reference')
        for stream in sorted(set(r[0] for r in actual_audio)):
            rows=[r for r in actual_audio if r[0]==stream]
            audio_checks.append(dict(stream=stream,decoded_frames=len(rows),samples=sum(int(r[3]) for r in rows),first_pts=int(rows[0][2]),last_pts=int(rows[-1][2])))
    text,_=p.execute(p.PROBE,['-v','error','-show_packets','-show_entries','packet=stream_index,pts,dts,duration','-of','json',output])
    packets=json.loads(text)['packets']; timestamps={}
    for packet in packets:
        stream=packet['stream_index']; pts=int(packet['pts']); dts=int(packet['dts'])
        if stream in timestamps and dts<=timestamps[stream]: raise AssertionError('Non-monotonic DTS')
        if stream==0 and pts!=dts: raise AssertionError('Video reordering not supported')
        timestamps[stream]=dts
    verification=dict(passed=True,frames=len(actual),copied_raw_hash_matches=len(copied),boundary_checks=boundary_checks,
                      audio=audio_checks,strictly_monotonic_streams=len(timestamps),source_frame_selection=[dict(path=x.path,first=x.first,stop=x.stop) for x in plan.pieces],
                      duration=p.probe(output)['format']['duration'])
    p.dump(folder/'verification.json',verification); return verification
