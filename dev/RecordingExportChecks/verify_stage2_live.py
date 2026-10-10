"""Independent comparison of a real Stage 2 Stop snapshot, Rewind and MP4."""
import json,math,os,sys
import checks as c

def snapshot():
    import numpy as np
    root=c.REPO/'artifacts/unified-stage2/corrected-live-snapshot'
    actual=json.loads((root/'results.json').read_text(encoding='utf-8'))
    prior=c.REPO/'artifacts/unified-stage2/live-candidate/legacy-reference'
    reference=json.loads((prior/'results.json').read_text(encoding='utf-8'))
    a,b=actual['clip']['Path'],reference['clip']['Path']
    metadata=json.loads(c.Path(a).with_suffix('.json').read_text(encoding='utf-8'))
    export=metadata['Export'];assert export['ParallelAudio'] and not export['Fallback']
    av,bv=c.frames(a),c.frames(b);ap,bp=c.packets(a),c.packets(b)
    assert len(av)==len(bv)==1802
    assert [r[1:5] for r in av]==[r[1:5] for r in bv]
    assert [(p['pts'],p['dts']) for p in ap]==[(p['pts'],p['dts']) for p in bp]
    assert c.frames(a,False)==c.frames(b,False) and c.packets(a,'a')==c.packets(b,'a')
    segments=list(map(json.loads,(root/'segments.jsonl').read_text(encoding='utf-8').splitlines()))
    begin=float(f"{metadata['ActualStart']-segments[0]['Start']:.6f}")
    duration=float(f"{actual['clip']['Duration']:.6f}");cursor=0;selected=[];raw=[];identities=[]
    for segment in segments:
        path=root/segment['Path']
        for i,(p,f) in enumerate(zip(c.packets(path),c.frames(path))):
            if begin<=cursor+int(p['pts'])/1000<begin+duration:
                selected.append(p);raw.append(f);identities.append((path,i))
        cursor+=float(f"{segment['End']-segment['Start']:.6f}")
    head=export['EncodedFrames'];assert len(selected)==len(av)
    assert [p['data_hash'] for p in ap[head:]]==[p['data_hash'] for p in selected[head:]]
    assert [f[-1] for f in av[head:]]==[f[-1] for f in raw[head:]]
    first,index=identities[0]
    text,*_=c.call(c.PROBE,['-v','error','-select_streams','v','-show_entries','stream=width,height','-of','json',first])
    fmt=json.loads(text)['streams'][0];width,height=fmt['width'],fmt['height']
    def luma(path,filter):
        import subprocess
        p=subprocess.run([str(c.LAB),'-v','error','-xerror','-i',str(path),'-vf',filter,'-frames:v',str(head),'-an',
            '-pix_fmt','gray','-fps_mode','passthrough','-f','rawvideo','-'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW,check=True)
        assert not p.stderr.strip()
        return np.frombuffer(p.stdout,np.uint8).reshape(head,height,width).astype(np.int16)
    expected=luma(first,f'trim=start_frame={index}:end_frame={index+head},extractplanes=y')
    observed=luma(a,'extractplanes=y')
    mae=np.abs(expected-observed).mean(axis=(1,2));assert all(v<8 for v in mae)
    c.dump(root/'integrity.json',dict(passed=True,frames=len(av),copied=export['CopiedFrames'],encoded=head,
        source_packet_and_decoded_frame_hashes_exact=True,frame_pts_dts_equal_reference=True,
        aac_payloads_samples_pts_priming_padding_equal=True,boundary_source_frame_indices=list(range(index,index+head)),
        boundary_luma_mae_per_frame=mae.tolist(),extra_tail_frame_removed=True))
    print(f"Corrected frozen real Stop: exact {len(av)} frames / {export['CopiedFrames']} copied packets / all AAC / boundary content passed",flush=True)

def main():
    root=c.REPO/'artifacts/unified-stage2'/('live' if len(sys.argv)<2 else sys.argv[1])
    result=json.loads((root/'results.json').read_text(encoding='utf-8'))
    fallback=result['metadata']['Export']['Strategy']=='legacy-reencode'
    source=root/'retained';reference=root/('legacy-reference' if fallback else 'stage1-reference')
    if not reference.exists():
        reference.mkdir()
        for file in source.iterdir():
            if file.suffix=='.mkv':os.link(file,reference/file.name)
            else:(reference/file.name).write_bytes(file.read_bytes())
        manifest=json.loads((reference/'recording.json').read_text(encoding='utf-8'))
        manifest['Settings']['OutputDirectory']=str(reference/'output')
        manifest['Settings']['BufferDirectory']=str(reference/'buffer')
        c.dump(reference/'recording.json',manifest)
    baseline=json.loads((reference/'results.json').read_text(encoding='utf-8')) if (reference/'results.json').exists() else c.finalize(reference,'legacy' if fallback else 'stage1')
    a=result['result']['Path'];b=baseline['clip']['Path']
    actual,expected=c.frames(a),c.frames(b)
    fps=result['metadata']['manifest']['Settings']['Fps']
    duration=float(f"{result['result']['Duration']:.6f}")
    assert len(actual)==len(expected)==math.ceil(duration*fps-1e-9)
    assert [r[1:5] for r in actual]==[r[1:5] for r in expected]
    assert c.frames(a,False)==c.frames(b,False)
    ap,bp=c.packets(a),c.packets(b)
    assert [(p['pts'],p['dts']) for p in ap]==[(p['pts'],p['dts']) for p in bp]
    assert all(int(p['dts'])>int(q['dts']) for q,p in zip(ap,ap[1:]))
    assert c.packets(a,'a')==c.packets(b,'a')
    selected=[];raw=[];cursor=0
    segments=list(map(json.loads,(source/'segments.jsonl').read_text(encoding='utf-8').splitlines()))
    begin=float(f"{result['metadata']['ActualStart']-segments[0]['Start']:.6f}")
    for segment in segments:
        path=source/segment['Path']
        for p,f in zip(c.packets(path),c.frames(path)):
            if begin<=cursor+int(p['pts'])/1000<begin+duration:selected.append(p);raw.append(f)
        cursor+=float(f"{segment['End']-segment['Start']:.6f}")
    encoded=result['metadata']['Export']['EncodedFrames']
    assert len(selected)==len(actual)
    if fallback:
        assert actual==expected
    else:
        assert [p['data_hash'] for p in ap[encoded:]]==[p['data_hash'] for p in selected[encoded:]]
        assert [f[-1] for f in actual[encoded:]]==[f[-1] for f in raw[encoded:]]
        assert [f[-1] for f in actual[:encoded]]==[f[-1] for f in expected[:encoded]]
    for clip in [result['replay']['Path'],result['replayDuringStop']['Path']]:c.frames(clip);c.frames(clip,False)
    first=json.loads(c.Path(result['replay']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
    later=json.loads(c.Path(result['replayDuringStop']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
    assert later['ActualStart']>=first['AcceptedSaveBoundary']
    job=json.loads((root/'compression-queue.json').read_text(encoding='utf-8'))[0]
    assert job['State']=='Completed' and c.Path(a).exists()
    c.frames(job['Output']);c.frames(job['Output'],False)
    assert [p['data_hash'] for p in c.packets(a,'a')]==[p['data_hash'] for p in c.packets(job['Output'],'a')]
    c.dump(root/'integrity.json',dict(passed=True,frames=len(actual),copied=0 if fallback else len(actual)-encoded,encoded=len(actual) if fallback else encoded,
        fallback=fallback,source_packet_hashes_exact=not fallback,source_frame_hashes_exact=not fallback,boundary_matches_same_snapshot_reference=True,
        native_pts_dts_equal=True,aac_payloads_samples_pts_priming_padding_equal=True,
        replay_full_decode=True,no_previous_history_leakage=True,mp4_all_streams_full_decode=True,
        mp4_aac_payloads_preserved=True,reference_strategy='legacy' if fallback else 'stage1',same_snapshot_reference_total_ms=baseline['totalMs']))
    print('Stage 2 real Stop/source/Stage 1/concurrent Rewind/MP4 independent integrity passed',flush=True)

if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='snapshot':snapshot()
    else:main()
